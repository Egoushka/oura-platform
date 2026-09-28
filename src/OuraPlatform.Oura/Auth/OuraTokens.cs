using System.Globalization;
using System.Text;

namespace OuraPlatform.Oura.Auth;

/// <summary>
/// Raw <c>/oauth/token</c> response. <see cref="RefreshToken"/> is single-use: the value here
/// replaces the one that was spent obtaining it, and losing it means redoing the browser handshake
/// by hand.
/// </summary>
public sealed record OuraTokenResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    string? Scope,
    string? TokenType)
{
    /// <summary>Records print every member by default. These must never reach a log, an exception
    /// message or a Serilog property, so the generated implementation is suppressed.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("redacted");
        return true;
    }
}

/// <summary>Persisted token state, as read back from <c>oauth_tokens</c>.</summary>
public sealed record OuraTokenSnapshot(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string Scope,
    DateTimeOffset RotatedAt)
{
    public bool IsStale(DateTimeOffset now, TimeSpan skew) => now >= ExpiresAt - skew;

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"ExpiresAt = {ExpiresAt:O}, Scope = {Scope}, AccessToken = redacted, RefreshToken = redacted");
        return true;
    }
}

/// <summary>
/// Thrown when a refreshed token pair could not be persisted. Once this is thrown the process is
/// holding an access token the database does not know about and Oura has already invalidated the
/// old refresh token — continuing would burn the account's authorization. Both hosts treat it as
/// fatal.
/// </summary>
public sealed class OuraTokenPersistenceException : Exception
{
    public OuraTokenPersistenceException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
