using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using QNote.Infrastructure;
using QNote.Sync;

namespace QNote.Services;

/// <summary>
/// Presentation-side <see cref="ITokenProvider"/> over MSAL.NET + the Windows
/// WAM broker (PRD decision; the pure-WinRT stack stays a drop-in rollback —
/// swap this registration, remove the package, nothing else changes). Design:
///
/// - Broker credentials stay WAM-owned. MSAL's account metadata is persisted in
///   a DPAPI-encrypted cache under the app data root so restarts retain sign-in.
/// - <see cref="AcquireSilentAsync"/> NEVER shows UI (engine cycles run on
///   background timers); <see cref="AcquireInteractiveAsync"/> binds the
///   system account picker to the main window via the injected handle delegate
///   and must be called from the UI thread (Settings sign-in button).
/// - ⚠️ AOT: MSAL ships ZERO trim annotations — every MSAL version bump must
///   re-run the win-x64-portable-aot publish + a startup smoke (PRD ruling).
/// </summary>
public sealed class MsalTokenProvider : ITokenProvider
{
    /// <summary>
    /// The least-privileged Graph scope (app folder only). Widening happens ONLY
    /// if the live endpoint probe returns 403 (PRD warning) — then update the
    /// Azure registration too.
    /// </summary>
    public const string GraphScope = "Files.ReadWrite.AppFolder";

    /// <summary>
    /// The Azure app-registration client id (a public value, not a secret —
    /// public clients have no credentials). Registered on the personal-account
    /// directory ("QNote - Sticky Notes", app folder granted).
    /// </summary>
    public const string ClientId = "7637ef2c-45ac-4e90-82c5-cbd8d020392e";

    private static readonly string[] Scopes = [GraphScope];

    private readonly IPublicClientApplication _app;
    private readonly ILogger<MsalTokenProvider> _log;
    private readonly Func<nint>? _parentWindowHandle;
    private readonly Task<MsalCacheHelper> _cacheReady;

    /// <summary>
    /// The account captured from the last INTERACTIVE acquisition. Broker
    /// enumeration (<c>GetAccountsAsync</c>) does not reliably surface the
    /// freshly-interactive account on every WAM state (live-caught 2026-10-22:
    /// sign-in succeeded, the sync account gate still saw zero accounts), so
    /// this field is the authoritative fallback until an explicit sign-out.
    /// </summary>
    private IAccount? _currentAccount;

    public MsalTokenProvider(
        ILogger<MsalTokenProvider> log,
        AppPaths paths,
        Func<nint>? parentWindowHandle = null,
        string? clientId = null)
        : this(CreateApplication(clientId), paths, log, parentWindowHandle)
    {
    }

    internal MsalTokenProvider(
        IPublicClientApplication app,
        AppPaths paths,
        ILogger<MsalTokenProvider> log,
        Func<nint>? parentWindowHandle = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(paths);
        _log = log;
        _parentWindowHandle = parentWindowHandle;
        _app = app;
        _cacheReady = MsalCachePersistence.RegisterAsync(_app.UserTokenCache, paths.Root);
    }

    private static IPublicClientApplication CreateApplication(string? clientId)
    {
        var effectiveClientId = clientId ?? ClientId;
        if (string.IsNullOrWhiteSpace(effectiveClientId) || effectiveClientId == "PENDING-REGISTRATION")
            throw new ArgumentException(
                "The MSAL client id is not configured — create the Azure app registration and set MsalTokenProvider.ClientId.", nameof(clientId));

        return PublicClientApplicationBuilder.Create(effectiveClientId)
            .WithAuthority(AadAuthorityAudience.PersonalMicrosoftAccount)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
            .WithDefaultRedirectUri()
            .Build();
    }

    public async Task<AuthResult> AcquireSilentAsync(CancellationToken ct = default)
    {
        var account = await GetFirstAccountAsync(ct);
        if (account is null)
            throw new TokenProviderException(
                TokenProviderErrorKind.AuthRequired, "No signed-in sync account — sign in from Settings first.");

        try
        {
            var result = await _app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
            return ToAuthResult(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Map(ex, interactive: false);
        }
    }

    public async Task<AuthResult> AcquireInteractiveAsync(CancellationToken ct = default)
    {
        try
        {
            await _cacheReady.WaitAsync(ct);
            var builder = _app.AcquireTokenInteractive(Scopes).WithPrompt(Prompt.SelectAccount);
            if (_parentWindowHandle is { } handle)
                builder = builder.WithParentActivityOrWindow(handle());
            var result = await builder.ExecuteAsync(ct);
            _currentAccount = result.Account;
            _log.LogInformation("MSAL interactive sign-in succeeded for {User}.", result.Account?.Username);
            return ToAuthResult(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (MsalClientException canceled) when (canceled.ErrorCode == "authentication_canceled")
        {
            // WAM user-cancel (closing the account picker) surfaces as
            // MsalClientException, NOT OperationCanceledException — normalize so
            // callers treat it as the no-op path (settings: no error, no toast).
            throw new OperationCanceledException("The user canceled the interactive sign-in.", canceled);
        }
        catch (Exception ex)
        {
            throw Map(ex, interactive: true);
        }
    }

    public async Task<AccountInfo?> GetSignedInAccountAsync(CancellationToken ct = default)
    {
        var account = await GetFirstAccountAsync(ct);
        if (account is null)
            _log.LogInformation("No signed-in sync account is visible (broker enumeration empty).");
        return account is null
            ? null
            : new AccountInfo(account.Username ?? string.Empty, account.HomeAccountId?.Identifier ?? string.Empty);
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await _cacheReady.WaitAsync(ct);
        var accounts = (await _app.GetAccountsAsync()).ToList();
        // Enumeration can omit the interactive account. Remove that fallback
        // too, before forgetting it, so a restart cannot restore a signed-out user.
        if (_currentAccount is { } current && !accounts.Any(account =>
            account.HomeAccountId?.Identifier == current.HomeAccountId?.Identifier))
            accounts.Add(current);
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            await _app.RemoveAsync(account);
        }
        _currentAccount = null;
        _log.LogInformation("Signed out of every sync account.");
    }

    private async Task<IAccount?> GetFirstAccountAsync(CancellationToken ct)
    {
        await _cacheReady.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        var accounts = await _app.GetAccountsAsync();
        if (accounts.FirstOrDefault() is { } broker)
            return broker;
        // Broker enumeration gap: fall back to the interactively acquired
        // account so a just-completed sign-in is visible to the sync gate.
        if (_currentAccount is not null)
            _log.LogInformation("Broker enumeration returned no accounts; using the interactively acquired account {User}.", _currentAccount.Username);
        return _currentAccount;
    }

    private static AuthResult ToAuthResult(AuthenticationResult result) => new(
        result.AccessToken,
        result.ExpiresOn,
        result.Account is null
            ? null
            : new AccountInfo(result.Account.Username ?? string.Empty, result.Account.HomeAccountId?.Identifier ?? string.Empty));

    private TokenProviderException Map(Exception ex, bool interactive)
    {
        if (ex is MsalUiRequiredException ui)
            return new(
                TokenProviderErrorKind.AuthRequired,
                interactive
                    ? $"Interactive sign-in was refused: {ui.Message}"
                    : "The cached token cannot be renewed silently — sign in again.",
                ui);
        if (ex is MsalServiceException service)
        {
            if (service.StatusCode is 401 or 403)
                return new(TokenProviderErrorKind.AuthRequired, $"The account was rejected by the identity service: {service.Message}", service);
            return new(TokenProviderErrorKind.ServiceError, $"The identity service failed: {service.Message}", service);
        }
        // Best-effort network classification: MSAL surfaces transport failures as
        // service exceptions wrapping HttpRequestException (refine after the
        // live smoke; wrong guesses degrade to ServiceError, never a crash).
        if (ex.InnerException is HttpRequestException or TaskCanceledException
            || ex is HttpRequestException
            || ex is TaskCanceledException)
            return new(TokenProviderErrorKind.NetworkUnavailable, $"The identity service is unreachable: {ex.Message}", ex);
        return new(TokenProviderErrorKind.ServiceError, $"Sign-in failed: {ex.Message}", ex);
    }
}
