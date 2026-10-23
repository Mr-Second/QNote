namespace QNote.Sync;

/// <summary>
/// Signed-in account identity in QNote's own terms. The PRD's rollback constraint:
/// the auth stack behind <see cref="ITokenProvider"/> must never leak its types, so
/// MSAL's <c>IAccount</c>/<c>AuthenticationResult</c> never cross this boundary.
/// </summary>
public sealed record AccountInfo(string Username, string HomeAccountId);

/// <summary>A bearer access token plus its expiry, in QNote's own terms.</summary>
public sealed record AuthResult(string AccessToken, DateTimeOffset ExpiresOn, AccountInfo? Account);

/// <summary>
/// Classification of token-acquisition failures — deliberately a subset of
/// <see cref="SyncDriverErrorKind"/> so the driver can map provider failures onto
/// its own taxonomy without guessing.
/// </summary>
public enum TokenProviderErrorKind
{
    /// <summary>
    /// No signed-in account, interaction/consent is required, or the credentials
    /// were rejected by the identity service.
    /// </summary>
    AuthRequired,

    /// <summary>The identity service is unreachable (offline, DNS, timeout).</summary>
    NetworkUnavailable,

    /// <summary>Any other identity-service failure.</summary>
    ServiceError,
}

/// <summary>
/// An expected token-acquisition failure (error-handling spec: expected outcomes
/// are modeled, not buried in generic exceptions). Messages are English
/// diagnostics — Core never owns user-visible wording.
/// </summary>
public sealed class TokenProviderException : Exception
{
    public TokenProviderException(TokenProviderErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public TokenProviderErrorKind Kind { get; }
}

/// <summary>
/// The sync auth port (PRD decision: MSAL.NET + WAM broker, isolated behind this
/// interface so a pure-WinRT implementation remains a drop-in rollback). Contract:
/// <see cref="AcquireSilentAsync"/> must NEVER show UI — engine cycles run on
/// background timers; <see cref="AcquireInteractiveAsync"/> is an explicit
/// Settings action and may show the system account picker bound to the caller's
/// window. Implementations live in Presentation (HWND / package identity);
/// consumers are the OneDrive driver and the sync settings surface.
/// </summary>
public interface ITokenProvider
{
    /// <summary>
    /// Acquires a token without ever showing UI. Throws
    /// <see cref="TokenProviderException"/> (<see cref="TokenProviderErrorKind.AuthRequired"/>
    /// when nobody is signed in or the cached token cannot be renewed silently).
    /// </summary>
    Task<AuthResult> AcquireSilentAsync(CancellationToken ct = default);

    /// <summary>
    /// Acquires a token interactively (system account picker). Only called from a
    /// user-initiated surface; the implementation binds the prompt to its owner
    /// window and may require the UI thread.
    /// </summary>
    Task<AuthResult> AcquireInteractiveAsync(CancellationToken ct = default);

    /// <summary>The currently signed-in account, or null when signed out.</summary>
    Task<AccountInfo?> GetSignedInAccountAsync(CancellationToken ct = default);

    /// <summary>Signs out every account the token cache holds.</summary>
    Task SignOutAsync(CancellationToken ct = default);
}
