using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OuraPlatform.Oura;
using OuraPlatform.Oura.Auth;

namespace OuraPlatform.Oura.Tests;

public sealed class OuraOAuthClientTests
{
    private static OuraOAuthClient Build(Action<OuraOptions>? configure = null)
    {
        var options = new OuraOptions
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            RedirectUri = "http://localhost:8080/oauth/callback",
        };

        configure?.Invoke(options);

        return new OuraOAuthClient(
            new HttpClient(new StubHttp()),
            Options.Create(options),
            NullLogger<OuraOAuthClient>.Instance);
    }

    /// <summary>
    /// <c>Uri.ToString()</c> returns the human-readable form and unescapes the <c>%20</c> between
    /// scopes, which puts raw spaces into the redirect's <c>Location</c> header — invalid per
    /// RFC 7230. Callers must use <see cref="Uri.AbsoluteUri"/>, which keeps the escaping.
    /// </summary>
    [Fact]
    public void Authorization_uri_escapes_the_space_between_scopes()
    {
        var uri = Build().BuildAuthorizationUri("state-value");

        Assert.Contains("scope=personal%20daily", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain(' ', uri.AbsoluteUri);
    }

    [Fact]
    public void Authorization_uri_carries_the_code_flow_parameters()
    {
        var uri = Build().BuildAuthorizationUri("state-value");

        Assert.Equal("cloud.ouraring.com", uri.Host);
        Assert.Equal("/oauth/authorize", uri.AbsolutePath);
        Assert.Contains("response_type=code", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("client_id=client-id", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("state=state-value", uri.AbsoluteUri, StringComparison.Ordinal);

        // Oura allowlists redirect URIs verbatim, so this has to survive the round trip intact.
        Assert.Contains(
            "redirect_uri=http%3A%2F%2Flocalhost%3A8080%2Foauth%2Fcallback",
            uri.AbsoluteUri,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Email_scope_is_not_requested_by_default()
    {
        Assert.DoesNotContain("email", new OuraOptions().Scopes, StringComparison.Ordinal);

        // Names taken from Oura's 401 bodies, not from the spec: the spec declares spo2Daily and
        // never mentions stress or heart_health, and consent succeeds either way.
        foreach (var scope in new[] { "spo2", "stress", "heart_health" })
        {
            Assert.Contains(scope, new OuraOptions().Scopes, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("spo2Daily", new OuraOptions().Scopes, StringComparison.Ordinal);
    }
}
