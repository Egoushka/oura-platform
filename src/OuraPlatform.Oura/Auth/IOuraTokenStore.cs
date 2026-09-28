namespace OuraPlatform.Oura.Auth;

/// <summary>
/// Persistence for the rotating OAuth token pair. Implemented in <c>OuraPlatform.Storage</c>.
/// </summary>
public interface IOuraTokenStore
{
    /// <summary>Current token pair, or null if the browser handshake has not been done yet.</summary>
    Task<OuraTokenSnapshot?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Writes the pair obtained from the authorization-code exchange, replacing any
    /// existing row.</summary>
    Task<OuraTokenSnapshot> SaveAsync(OuraTokenResponse response, CancellationToken cancellationToken);

    /// <summary>
    /// Spends the stored refresh token for a new pair and persists both, holding a row lock for the
    /// duration so a concurrent caller cannot spend the same single-use token.
    /// </summary>
    /// <exception cref="OuraTokenPersistenceException">
    /// The exchange succeeded but the new pair could not be written. Fatal — see the exception's
    /// own documentation.
    /// </exception>
    Task<OuraTokenSnapshot> RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>The OAuth2 authorization-code endpoints. Personal Access Tokens were deprecated in
/// December 2025 and have no code path here.</summary>
public interface IOuraOAuthClient
{
    Uri BuildAuthorizationUri(string state);

    Task<OuraTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken);

    Task<OuraTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
}
