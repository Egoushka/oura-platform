using System.Net;
using System.Security.Cryptography;

namespace OuraPlatform.Api;

/// <summary>Log-category marker for the callback handler.</summary>
public sealed class OAuthCallback;

/// <summary>
/// Single-use CSRF state values for the authorization redirect.
/// </summary>
/// <remarks>
/// In memory, not in the database: the handshake is a one-off done by hand against a single
/// instance (see the single-instance invariant in CLAUDE.md), and a state value that does not
/// survive a restart just means clicking <c>/oauth/start</c> again.
/// </remarks>
public sealed class OAuthStateStore(TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DateTimeOffset> _issued = [];

    public string Issue()
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        lock (_issued)
        {
            var now = time.GetUtcNow();
            foreach (var expired in _issued.Where(entry => entry.Value < now).Select(entry => entry.Key).ToArray())
            {
                _issued.Remove(expired);
            }

            _issued[state] = now + Lifetime;
        }

        return state;
    }

    public bool Consume(string? state)
    {
        if (string.IsNullOrEmpty(state))
        {
            return false;
        }

        lock (_issued)
        {
            return _issued.Remove(state, out var expiresAt) && expiresAt >= time.GetUtcNow();
        }
    }
}

internal static class Pages
{
    public static string Success(DateTimeOffset expiresAt, string scope) => $"""
        <!doctype html><meta charset="utf-8"><title>Oura connected</title>
        <body style="font-family:system-ui;max-width:40rem;margin:4rem auto;line-height:1.5">
        <h1>Connected</h1>
        <p>The token pair is stored. The access token expires <code>{expiresAt:u}</code> and will be
        refreshed automatically from here on.</p>
        <p>Scopes granted: <code>{WebUtility.HtmlEncode(scope)}</code>. A collection that comes back
        empty for a range that should have data usually means a scope is missing — Oura answers those
        with empty arrays rather than an error.</p>
        <p>The ingest worker picks this up on its next cycle. You can close this tab.</p>
        </body>
        """;

    public static string Failure(string reason) => $"""
        <!doctype html><meta charset="utf-8"><title>Oura authorization failed</title>
        <body style="font-family:system-ui;max-width:40rem;margin:4rem auto;line-height:1.5">
        <h1>Not connected</h1>
        <p>{WebUtility.HtmlEncode(reason)}</p>
        <p><a href="/oauth/start">Try again</a></p>
        </body>
        """;
}
