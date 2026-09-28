using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OuraPlatform.Oura.Auth;

/// <summary>
/// Attaches the bearer token, refreshes proactively before expiry and reactively once on a 401.
/// </summary>
/// <remarks>
/// The in-process semaphore stops a burst of parallel requests from all deciding to refresh at
/// once. It is a stampede guard, not a correctness mechanism — correctness comes from the
/// <c>SELECT ... FOR UPDATE</c> inside <see cref="IOuraTokenStore.RefreshAsync"/>, which is what
/// makes it safe for the single-use refresh token.
/// </remarks>
public sealed class OuraAuthenticationHandler : DelegatingHandler
{
    private readonly IOuraTokenStore _tokens;
    private readonly OuraOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OuraAuthenticationHandler> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private OuraTokenSnapshot? _cached;

    public OuraAuthenticationHandler(
        IOuraTokenStore tokens,
        IOptions<OuraOptions> options,
        TimeProvider time,
        ILogger<OuraAuthenticationHandler> logger)
    {
        _tokens = tokens;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_options.UseSandbox)
        {
            // The sandbox accepts any string and has no token to spend.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "sandbox");
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var token = await CurrentTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // Re-consenting at /oauth/start writes a brand new access token straight into the database,
        // in the API process. This process is holding the old one and — because it is valid for
        // thirty days — would keep presenting it long past the point where the user has fixed the
        // problem. So a 401 always re-reads the store before concluding anything about the token.
        if (await ReloadIfChangedAsync(token, cancellationToken).ConfigureAwait(false) is { } reconsented)
        {
            _logger.LogInformation("Token changed underneath this process; retrying with the stored one.");
            response.Dispose();
            return await RetryWithAsync(request, reconsented, cancellationToken).ConfigureAwait(false);
        }

        // Oura answers a missing scope with a 401 too. No token will ever fix that, so refreshing
        // would spend a rotation per request for nothing — and there are 80 windows per collection.
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (OuraScopeErrors.Parse(body) is not null)
        {
            return response;
        }

        // Otherwise: Oura's access-token lifetime is documented inconsistently (24 hours in one
        // place, 30 days in another), so expires_at is a hint and a 401 is the authority.
        _logger.LogWarning("Oura returned 401 on {Path}; refreshing and retrying once.", request.RequestUri?.AbsolutePath);
        response.Dispose();

        var refreshed = await ForceRefreshAsync(token, cancellationToken).ConfigureAwait(false);
        return await RetryWithAsync(request, refreshed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the stored pair only when it is not the one that just failed.</summary>
    private async Task<OuraTokenSnapshot?> ReloadIfChangedAsync(
        OuraTokenSnapshot rejected,
        CancellationToken cancellationToken)
    {
        var stored = await _tokens.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null || string.Equals(stored.AccessToken, rejected.AccessToken, StringComparison.Ordinal))
        {
            return null;
        }

        return _cached = stored;
    }

    private async Task<HttpResponseMessage> RetryWithAsync(
        HttpRequestMessage request,
        OuraTokenSnapshot token,
        CancellationToken cancellationToken)
    {
        using var retry = await CloneAsync(request, cancellationToken).ConfigureAwait(false);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OuraTokenSnapshot> CurrentTokenAsync(CancellationToken cancellationToken)
    {
        var cached = _cached;
        if (cached is not null && !cached.IsStale(_time.GetUtcNow(), _options.RefreshSkew))
        {
            return cached;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _cached;
            if (cached is not null && !cached.IsStale(_time.GetUtcNow(), _options.RefreshSkew))
            {
                return cached;
            }

            var stored = await _tokens.ReadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "No Oura token stored. Complete the OAuth handshake at /oauth/start first.");

            if (!stored.IsStale(_time.GetUtcNow(), _options.RefreshSkew))
            {
                return _cached = stored;
            }

            _logger.LogInformation("Oura access token expires at {ExpiresAt:o}; refreshing.", stored.ExpiresAt);
            return _cached = await _tokens.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<OuraTokenSnapshot> ForceRefreshAsync(
        OuraTokenSnapshot rejected,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another request may have refreshed while this one was waiting on the gate. Only
            // refresh again if the cached pair is still the one that just got rejected.
            if (_cached is not null && !ReferenceEquals(_cached, rejected) && _cached.RotatedAt > rejected.RotatedAt)
            {
                return _cached;
            }

            return _cached = await _tokens.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static async Task<HttpRequestMessage> CloneAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in (IDictionary<string, object?>)request.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        if (request.Content is not null)
        {
            var buffered = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            clone.Content = new ByteArrayContent(buffered);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshGate.Dispose();
        }

        base.Dispose(disposing);
    }
}
