using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OuraPlatform.Oura.Auth;

public sealed class OuraOAuthClient : IOuraOAuthClient
{
    /// <summary>Named client for the token endpoint. Deliberately separate from the API client:
    /// it must not carry the bearer handler, and it must not be retried into a rate limit while
    /// holding the token-table row lock.</summary>
    public const string HttpClientName = "oura-oauth";

    private readonly HttpClient _http;
    private readonly OuraOptions _options;
    private readonly ILogger<OuraOAuthClient> _logger;

    public OuraOAuthClient(HttpClient http, IOptions<OuraOptions> options, ILogger<OuraOAuthClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public Uri BuildAuthorizationUri(string state)
    {
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.RedirectUri,
            ["scope"] = _options.Scopes,
            ["state"] = state,
        };

        var builder = new UriBuilder(_options.AuthorizeEndpoint)
        {
            Query = string.Join('&', query.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value!)}")),
        };

        return builder.Uri;
    }

    public Task<OuraTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken) =>
        PostAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = _options.RedirectUri,
            },
            "authorization_code",
            cancellationToken);

    public Task<OuraTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        PostAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            },
            "refresh_token",
            cancellationToken);

    private async Task<OuraTokenResponse> PostAsync(
        Dictionary<string, string> form,
        string grantType,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };

        // Client credentials go in the Basic header rather than the form so they never appear in a
        // request body that might be captured.
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The body of a failed token request can echo the submitted secret. Only the status
            // reaches the log or the exception.
            _logger.LogError(
                "Oura token endpoint rejected a {GrantType} grant with {StatusCode}.",
                grantType,
                (int)response.StatusCode);

            throw new HttpRequestException(
                $"Oura token endpoint returned {(int)response.StatusCode} for a {grantType} grant.",
                inner: null,
                response.StatusCode);
        }

        var token = await response.Content
            .ReadFromJsonAsync<OuraTokenResponse>(OuraJson.Options, cancellationToken)
            .ConfigureAwait(false);

        if (token is null || string.IsNullOrEmpty(token.AccessToken) || string.IsNullOrEmpty(token.RefreshToken))
        {
            throw new HttpRequestException(
                $"Oura token endpoint returned a {grantType} response without a usable token pair.");
        }

        return token;
    }
}
