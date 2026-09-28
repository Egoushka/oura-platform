using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OuraPlatform.Oura;
using OuraPlatform.Oura.Auth;

namespace OuraPlatform.Oura.Tests;

public sealed class OuraAuthenticationHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    /// <param name="reads">
    /// One snapshot per <c>ReadAsync</c>, so a test can model the store changing underneath the
    /// handler between requests. The last one is returned for every read after the queue drains.
    /// </param>
    private static (HttpClient Client, StubHttp Stub, RecordingTokenStore Tokens) Build(
        params OuraTokenSnapshot[] reads)
    {
        var stub = new StubHttp();
        var tokens = new RecordingTokenStore(reads);

        var handler = new OuraAuthenticationHandler(
            tokens,
            Options.Create(new OuraOptions { ClientId = "id", ClientSecret = "secret" }),
            new FixedTime(Now),
            NullLogger<OuraAuthenticationHandler>.Instance)
        {
            InnerHandler = stub,
        };

        return (new HttpClient(handler) { BaseAddress = new Uri("https://api.ouraring.com/v2/usercollection/") },
            stub, tokens);
    }

    private static OuraTokenSnapshot Snapshot(string suffix) =>
        new($"access-{suffix}", $"refresh-{suffix}", Now.AddDays(30), "daily", Now);

    /// <summary>
    /// Re-consenting at /oauth/start writes a new access token from the API process. The ingest
    /// process holds the old one, and it stays valid for thirty days, so without re-reading the
    /// store on a 401 the fix would appear to do nothing until someone restarted the container.
    /// </summary>
    [Fact]
    public async Task A_401_re_reads_the_store_so_a_re_consent_takes_effect_without_a_restart()
    {
        // First read gets the token the request goes out with; the second models the user having
        // re-consented in the browser while that request was in flight.
        var (client, stub, tokens) = Build(Snapshot("old"), Snapshot("new"));
        stub.Respond(HttpStatusCode.Unauthorized, """{"detail":"Token is not authorized access spo2 scope."}""")
            .RespondJson("""{"data":[],"next_token":null}""");

        var response = await client.GetAsync("daily_spo2");

        Assert.Equal("access-new", stub.LastAuthorization);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, tokens.RefreshCalls);
    }

    /// <summary>Refreshing cannot add a scope, and there are 80 windows per collection — one
    /// rotation each would be 80 chances to lose the token pair for nothing.</summary>
    [Fact]
    public async Task A_scope_401_never_refreshes_the_token()
    {
        var (client, stub, tokens) = Build(Snapshot("only"));
        stub.Respond(HttpStatusCode.Unauthorized, """{"detail":"Token is not authorized access stress scope."}""");

        var response = await client.GetAsync("daily_resilience");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, tokens.RefreshCalls);
    }

    [Fact]
    public async Task An_ordinary_401_refreshes_once_and_retries()
    {
        var (client, stub, tokens) = Build(Snapshot("expired"));
        stub.Respond(HttpStatusCode.Unauthorized, """{"detail":"Unauthorized"}""")
            .RespondJson("""{"data":[],"next_token":null}""");

        var response = await client.GetAsync("daily_sleep");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, tokens.RefreshCalls);
    }

    [Fact]
    public async Task The_bearer_token_is_attached()
    {
        var (client, stub, _) = Build(Snapshot("live"));
        stub.RespondJson("""{"data":[],"next_token":null}""");

        await client.GetAsync("daily_sleep");

        Assert.Equal("access-live", stub.LastAuthorization);
    }

    private sealed class RecordingTokenStore(OuraTokenSnapshot[] reads) : IOuraTokenStore
    {
        private readonly Queue<OuraTokenSnapshot> _reads = new(reads);

        public int RefreshCalls { get; private set; }

        public Task<OuraTokenSnapshot?> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<OuraTokenSnapshot?>(_reads.Count > 1 ? _reads.Dequeue() : _reads.Peek());

        public Task<OuraTokenSnapshot> SaveAsync(OuraTokenResponse response, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OuraTokenSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCalls++;

            var rotated = new OuraTokenSnapshot(
                "access-refreshed", "refresh-refreshed", Now.AddDays(30), _reads.Peek().Scope, Now);

            _reads.Clear();
            _reads.Enqueue(rotated);
            return Task.FromResult(rotated);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
