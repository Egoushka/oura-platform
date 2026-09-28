using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OuraPlatform.Oura;

namespace OuraPlatform.Oura.Tests;

public sealed class OuraApiClientTests
{
    private static (OuraApiClient Client, StubHttp Stub) Build()
    {
        var stub = new StubHttp();
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.ouraring.com/v2/usercollection/") };
        return (new OuraApiClient(http, NullLogger<OuraApiClient>.Instance), stub);
    }

    private static async Task<List<OuraRawDocument>> DrainAsync(
        OuraApiClient client,
        OuraCollection collection,
        DateWindow window)
    {
        var documents = new List<OuraRawDocument>();
        await foreach (var document in client.FetchAsync(collection, window, CancellationToken.None))
        {
            documents.Add(document);
        }

        return documents;
    }

    [Fact]
    public async Task Follows_next_token_to_the_end()
    {
        var (client, stub) = Build();
        stub.RespondJson("""{"data":[{"id":"a","day":"2026-07-01"}],"next_token":"page2"}""")
            .RespondJson("""{"data":[{"id":"b","day":"2026-07-02"}],"next_token":"page3"}""")
            .RespondJson("""{"data":[{"id":"c","day":"2026-07-03"}],"next_token":null}""");

        var documents = await DrainAsync(
            client, OuraCollections.ByName("daily_sleep"), new DateWindow(new(2026, 7, 1), new(2026, 7, 31)));

        Assert.Equal(["a", "b", "c"], documents.Select(d => d.DocId));
        Assert.Equal(3, stub.Requests.Count);
        Assert.DoesNotContain("next_token", stub.Requests[0].Query);
        Assert.Contains("next_token=page2", stub.Requests[1].Query);
        Assert.Contains("next_token=page3", stub.Requests[2].Query);
    }

    [Fact]
    public async Task Daily_collections_are_queried_by_date()
    {
        var (client, stub) = Build();
        stub.RespondJson("""{"data":[],"next_token":null}""");

        await DrainAsync(client, OuraCollections.ByName("sleep"), new DateWindow(new(2026, 7, 1), new(2026, 7, 31)));

        Assert.Contains("start_date=2026-07-01", stub.Requests[0].Query);
        Assert.Contains("end_date=2026-07-31", stub.Requests[0].Query);
    }

    [Fact]
    public async Task Time_series_collections_are_queried_by_datetime()
    {
        var (client, stub) = Build();
        stub.RespondJson("""{"data":[]}""");

        await DrainAsync(client, OuraCollections.ByName("heartrate"), new DateWindow(new(2026, 7, 1), new(2026, 7, 7)));

        var query = Uri.UnescapeDataString(stub.Requests[0].Query);
        Assert.Contains("start_datetime=2026-07-01T00:00:00+00:00", query);
        // End is the day after the window's last day, so contiguous windows leave no gap.
        Assert.Contains("end_datetime=2026-07-08T00:00:00+00:00", query);
    }

    [Fact]
    public async Task Ring_configuration_sends_no_date_filter()
    {
        var (client, stub) = Build();
        stub.RespondJson("""{"data":[{"id":"r1"}],"next_token":null}""");

        await DrainAsync(
            client, OuraCollections.ByName("ring_configuration"), new DateWindow(new(2020, 1, 1), new(2026, 7, 31)));

        Assert.Equal(string.Empty, stub.Requests[0].Query);
    }

    [Fact]
    public async Task Personal_info_has_no_data_envelope()
    {
        var (client, stub) = Build();
        stub.RespondJson("""{"id":"me","age":31}""");

        var documents = await DrainAsync(
            client, OuraCollections.ByName("personal_info"), new DateWindow(new(2026, 7, 1), new(2026, 7, 1)));

        Assert.Equal("me", Assert.Single(documents).DocId);
    }

    /// <summary>Time-series rows carry no id. The synthesised one must be stable so that
    /// re-fetching an overlapping window upserts instead of duplicating.</summary>
    [Fact]
    public async Task Time_series_rows_get_a_deterministic_id_from_their_timestamp()
    {
        var (client, stub) = Build();
        stub.RespondJson("""{"data":[{"timestamp":"2026-07-01T22:15:00.000Z","bpm":58,"source":"sleep"}]}""")
            .RespondJson("""{"data":[{"timestamp":"2026-07-01T22:15:00.000Z","bpm":58,"source":"sleep"}]}""");

        var window = new DateWindow(new(2026, 7, 1), new(2026, 7, 7));
        var first = await DrainAsync(client, OuraCollections.ByName("heartrate"), window);
        var second = await DrainAsync(client, OuraCollections.ByName("heartrate"), window);

        Assert.Equal("heartrate:2026-07-01T22:15:00.000Z", first[0].DocId);
        Assert.Equal(first[0].DocId, second[0].DocId);
        Assert.Null(first[0].Day);
    }

    [Fact]
    public async Task Enhanced_tag_takes_its_day_from_start_day()
    {
        var (client, stub) = Build();
        stub.RespondJson("""{"data":[{"id":"t1","start_day":"2026-07-04","start_time":"2026-07-04T09:00:00+03:00"}],"next_token":null}""");

        var documents = await DrainAsync(
            client, OuraCollections.ByName("enhanced_tag"), new DateWindow(new(2026, 7, 1), new(2026, 7, 31)));

        Assert.Equal(new DateOnly(2026, 7, 4), documents[0].Day);
    }

    [Fact]
    public async Task Raw_payload_is_preserved_verbatim()
    {
        var (client, stub) = Build();
        stub.RespondJson("""{"data":[{"id":"a","day":"2026-07-01","something_new":{"nested":1}}],"next_token":null}""");

        var documents = await DrainAsync(
            client, OuraCollections.ByName("daily_sleep"), new DateWindow(new(2026, 7, 1), new(2026, 7, 31)));

        // A field no model binds still has to reach oura_raw intact.
        Assert.Contains("something_new", documents[0].Payload);
    }

    [Fact]
    public async Task Rate_limit_details_survive_into_the_exception()
    {
        var (client, stub) = Build();
        stub.Respond(
            HttpStatusCode.TooManyRequests,
            """{"detail":"slow down"}""",
            ("X-RateLimit-Tier", "application"),
            ("Retry-After", "30"));

        var exception = await Assert.ThrowsAsync<OuraApiException>(() => DrainAsync(
            client, OuraCollections.ByName("daily_sleep"), new DateWindow(new(2026, 7, 1), new(2026, 7, 31))));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
    }

    /// <summary>
    /// Oura reports a scope it did not grant as a 401 with this body — not a 403, and not the empty
    /// array the docs imply. It is the only place a wrong scope name ever surfaces, because the
    /// authorize endpoint accepts unknown names without complaint.
    /// </summary>
    [Theory]
    [InlineData("spo2")]
    [InlineData("stress")]
    [InlineData("heart_health")]
    public async Task A_missing_scope_is_named_rather_than_buried_in_a_401(string scope)
    {
        var (client, stub) = Build();
        stub.Respond(HttpStatusCode.Unauthorized, $$"""{"detail":"Token is not authorized access {{scope}} scope."}""");

        var exception = await Assert.ThrowsAsync<OuraApiException>(() => DrainAsync(
            client, OuraCollections.ByName("daily_spo2"), new DateWindow(new(2026, 7, 1), new(2026, 7, 31))));

        Assert.Equal(scope, exception.MissingScope);
        Assert.Contains("re-consent", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_ordinary_401_is_not_mistaken_for_a_scope_problem()
    {
        var (client, stub) = Build();
        stub.Respond(HttpStatusCode.Unauthorized, """{"detail":"Unauthorized"}""");

        var exception = await Assert.ThrowsAsync<OuraApiException>(() => DrainAsync(
            client, OuraCollections.ByName("daily_sleep"), new DateWindow(new(2026, 7, 1), new(2026, 7, 31))));

        Assert.Null(exception.MissingScope);
    }

    [Fact]
    public async Task Forbidden_is_reported_as_a_lapsed_subscription()
    {
        var (client, stub) = Build();
        stub.Respond(HttpStatusCode.Forbidden, """{"detail":"forbidden"}""");

        var exception = await Assert.ThrowsAsync<OuraApiException>(() => DrainAsync(
            client, OuraCollections.ByName("daily_sleep"), new DateWindow(new(2026, 7, 1), new(2026, 7, 31))));

        Assert.Contains("subscription", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class OuraWindowsTests
{
    [Fact]
    public void Daily_collections_are_chunked_monthly_and_cover_the_range_exactly_once()
    {
        var windows = OuraWindows.Split(
            OuraCollections.ByName("daily_sleep"), new DateOnly(2026, 1, 15), new DateOnly(2026, 4, 3)).ToArray();

        Assert.Equal(new DateOnly(2026, 1, 15), windows[0].Start);
        Assert.Equal(new DateOnly(2026, 4, 3), windows[^1].End);
        AssertContiguous(windows);
    }

    [Fact]
    public void Time_series_collections_are_chunked_weekly()
    {
        var windows = OuraWindows.Split(
            OuraCollections.ByName("heartrate"), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)).ToArray();

        Assert.Equal(5, windows.Length);
        Assert.Equal(7, windows[0].End.DayNumber - windows[0].Start.DayNumber + 1);
        AssertContiguous(windows);
    }

    [Fact]
    public void Undated_collections_produce_a_single_window()
    {
        Assert.Single(OuraWindows.Split(
            OuraCollections.ByName("ring_configuration"), new DateOnly(2020, 1, 1), new DateOnly(2026, 7, 31)));

        Assert.Single(OuraWindows.Split(
            OuraCollections.ByName("personal_info"), new DateOnly(2020, 1, 1), new DateOnly(2026, 7, 31)));
    }

    [Fact]
    public void An_inverted_range_produces_nothing()
    {
        Assert.Empty(OuraWindows.Split(
            OuraCollections.ByName("daily_sleep"), new DateOnly(2026, 7, 31), new DateOnly(2026, 7, 1)));
    }

    [Fact]
    public void A_single_day_produces_one_window()
    {
        var window = Assert.Single(OuraWindows.Split(
            OuraCollections.ByName("daily_sleep"), new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 1)));

        Assert.Equal(window.Start, window.End);
    }

    /// <summary>Gaps would silently lose days; overlaps would re-fetch them. Both are wrong, and
    /// only the first is invisible.</summary>
    private static void AssertContiguous(DateWindow[] windows)
    {
        for (var index = 1; index < windows.Length; index++)
        {
            Assert.Equal(windows[index - 1].End.AddDays(1), windows[index].Start);
        }
    }
}
