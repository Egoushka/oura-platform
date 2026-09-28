using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace OuraPlatform.Oura;

public interface IOuraApiClient
{
    /// <summary>
    /// Streams every document in a window, following <c>next_token</c> to the end. Windows are
    /// produced by <see cref="OuraWindows.Split"/>; collections that take no date filter ignore the
    /// window entirely.
    /// </summary>
    IAsyncEnumerable<OuraRawDocument> FetchAsync(
        OuraCollection collection,
        DateWindow window,
        CancellationToken cancellationToken);
}

public sealed class OuraApiException : Exception
{
    public OuraApiException(string message, HttpStatusCode statusCode, string? missingScope = null)
        : base(message)
    {
        StatusCode = statusCode;
        MissingScope = missingScope;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>The scope Oura says the token lacks, when that is why the request failed. Set from
    /// the 401 body — see <see cref="OuraScopeErrors"/>.</summary>
    public string? MissingScope { get; }
}

/// <summary>
/// Oura reports a scope it did not grant as a 401 with a specific body, not as a 403 and not as the
/// empty array the docs imply. Recognising it matters twice over: refreshing the token cannot fix
/// it, and the authorize endpoint accepted the consent without complaint, so this 401 is the only
/// place the wrong scope name ever surfaces.
/// </summary>
public static partial class OuraScopeErrors
{
    /// <summary>Matches <c>{"detail":"Token is not authorized access spo2 scope."}</c>.</summary>
    [GeneratedRegex(@"not authorized access (\w+) scope", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static string? Parse(string body)
    {
        var match = Pattern().Match(body);
        return match.Success ? match.Groups[1].Value : null;
    }
}

public sealed class OuraApiClient : IOuraApiClient
{
    public const string HttpClientName = "oura-api";

    private readonly HttpClient _http;
    private readonly ILogger<OuraApiClient> _logger;

    public OuraApiClient(HttpClient http, ILogger<OuraApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async IAsyncEnumerable<OuraRawDocument> FetchAsync(
        OuraCollection collection,
        DateWindow window,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? nextToken = null;
        var page = 0;

        do
        {
            var uri = BuildUri(collection, window, nextToken);
            using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(collection, uri, response, cancellationToken).ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (collection.Style == OuraQueryStyle.Singleton)
            {
                // personal_info returns a bare document with no `data` envelope.
                yield return ToRawDocument(collection, json.RootElement);
                yield break;
            }

            foreach (var element in json.RootElement.GetProperty("data").EnumerateArray())
            {
                yield return ToRawDocument(collection, element);
            }

            nextToken = json.RootElement.TryGetProperty("next_token", out var token) && token.ValueKind == JsonValueKind.String
                ? token.GetString()
                : null;

            page++;
            if (page % 20 == 0)
            {
                _logger.LogInformation(
                    "{Collection} {Window} still paging after {Pages} pages.", collection.Name, window, page);
            }
        }
        while (!string.IsNullOrEmpty(nextToken));
    }

    private static Uri BuildUri(OuraCollection collection, DateWindow window, string? nextToken)
    {
        var query = new List<string>(3);

        switch (collection.Style)
        {
            case OuraQueryStyle.Daily:
                query.Add($"start_date={window.Start:yyyy-MM-dd}");
                query.Add($"end_date={window.End:yyyy-MM-dd}");
                break;

            case OuraQueryStyle.TimeSeries:
                // Sent in UTC. Windows are contiguous, so nothing falls between them; if Oura
                // treats end_datetime as inclusive the boundary row simply arrives twice and
                // collapses onto the same synthesised document id.
                query.Add($"start_datetime={Iso8601Utc(window.Start)}");
                query.Add($"end_datetime={Iso8601Utc(window.End.AddDays(1))}");
                break;

            case OuraQueryStyle.Undated:
            case OuraQueryStyle.Singleton:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(collection), collection.Style, "Unknown query style.");
        }

        if (!string.IsNullOrEmpty(nextToken))
        {
            query.Add($"next_token={Uri.EscapeDataString(nextToken)}");
        }

        var path = query.Count == 0 ? collection.Path : $"{collection.Path}?{string.Join('&', query)}";
        return new Uri(path, UriKind.Relative);
    }

    private static string Iso8601Utc(DateOnly day) =>
        Uri.EscapeDataString(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            .ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));

    /// <summary>
    /// Pulls the document id and calendar day out of a payload without deserializing it into a
    /// model, so an unparseable document still reaches <c>oura_raw</c>.
    /// </summary>
    private static OuraRawDocument ToRawDocument(OuraCollection collection, JsonElement element)
    {
        var payload = element.GetRawText();

        var day = TryGetDate(element, "day") ?? TryGetDate(element, "start_day");

        var id = element.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()!
            : SynthesiseId(collection, element);

        return new OuraRawDocument(collection.Name, id, day, payload);
    }

    /// <summary>Time-series rows carry no id. The timestamp is the natural key: re-fetching an
    /// overlapping window must upsert, not duplicate.</summary>
    private static string SynthesiseId(OuraCollection collection, JsonElement element)
    {
        if (element.TryGetProperty("timestamp", out var timestamp) &&
            timestamp.ValueKind == JsonValueKind.String &&
            timestamp.TryGetDateTimeOffset(out var instant))
        {
            return $"{collection.Name}:{instant.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}";
        }

        throw new OuraApiException(
            $"{collection.Name}: document has neither an `id` nor a parseable `timestamp` to key on.",
            HttpStatusCode.OK);
    }

    private static DateOnly? TryGetDate(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateOnly.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var day)
            ? day
            : null;

    private async Task EnsureSuccessAsync(
        OuraCollection collection,
        Uri uri,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // 429s that reach here have already exhausted the resilience pipeline's retries.
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogError(
                "Rate limited on {Collection} after retries. Tier={Tier} Limit={Limit} Window={Window} Reset={Reset}",
                collection.Name,
                Header(response, "X-RateLimit-Tier"),
                Header(response, "X-RateLimit-Limit"),
                Header(response, "X-RateLimit-Window"),
                Header(response, "X-RateLimit-Reset"));
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized && OuraScopeErrors.Parse(body) is { } scope)
        {
            throw new OuraApiException(
                $"{collection.Name} needs the '{scope}' scope, which the stored token was not granted. " +
                "Add it to Oura__Scopes and re-consent at /oauth/start — the authorize endpoint " +
                "silently ignores scope names it does not recognise, so a typo looks like success.",
                response.StatusCode,
                scope);
        }

        // 403 means the Oura subscription lapsed, not that the request was malformed — worth
        // saying out loud because it looks like an auth bug.
        var hint = response.StatusCode switch
        {
            HttpStatusCode.Forbidden => " (Oura subscription may have expired)",
            HttpStatusCode.Unauthorized => " (token refresh already retried once)",
            _ => string.Empty,
        };

        throw new OuraApiException(
            $"GET {uri} returned {(int)response.StatusCode}{hint}: {Truncate(body)}",
            response.StatusCode);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(',', values) : "-";

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : string.Concat(value.AsSpan(0, 500), "…");
}
