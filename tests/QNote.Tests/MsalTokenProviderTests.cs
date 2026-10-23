using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;
using QNote.Infrastructure;
using QNote.Services;

namespace QNote.Tests;

public sealed class MsalTokenProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "QNoteAuthTests", Guid.NewGuid().ToString("N"));

    // Synthetic identity only: these tests never contact WAM or an identity service.
    private static readonly string AccountCache = $$"""
        {
          "Account": {
            "sample-user.sample-tenant-login.microsoftonline.com-consumers": {
              "home_account_id": "sample-user.sample-tenant",
              "environment": "login.microsoftonline.com",
              "realm": "consumers",
              "local_account_id": "sample-user",
              "username": "cache-user@example.test",
              "authority_type": "MSSTS"
            }
          },
          "RefreshToken": {
            "sample-user.sample-tenant-login.microsoftonline.com-refreshtoken-{{MsalTokenProvider.ClientId}}--": {
              "home_account_id": "sample-user.sample-tenant",
              "environment": "login.microsoftonline.com",
              "credential_type": "RefreshToken",
              "client_id": "{{MsalTokenProvider.ClientId}}",
              "secret": "synthetic-token-not-valid-for-authentication"
            }
          }
        }
        """;

    [Fact]
    public async Task NewProvider_RestoresTheAccountAfterRestart()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var firstApp = CreateApplication();
        var store = await MsalCachePersistence.RegisterAsync(firstApp.UserTokenCache, _root);
        store.SaveUnencryptedTokenCache(Encoding.UTF8.GetBytes(AccountCache));
        Assert.Single(await firstApp.GetAccountsAsync());

        // A new MSAL application has no process-local account fallback.
        var provider = CreateProvider();
        var account = await provider.GetSignedInAccountAsync();

        Assert.NotNull(account);
        Assert.Equal("cache-user@example.test", account.Username);
        Assert.Equal("sample-user.sample-tenant", account.HomeAccountId);
    }

    [Fact]
    public async Task SignOut_RemovesThePersistedAccountFromTheNextLaunch()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var seedApp = CreateApplication();
        var store = await MsalCachePersistence.RegisterAsync(seedApp.UserTokenCache, _root);
        store.SaveUnencryptedTokenCache(Encoding.UTF8.GetBytes(AccountCache));
        var provider = CreateProvider();
        Assert.NotNull(await provider.GetSignedInAccountAsync());

        await provider.SignOutAsync();

        Assert.Null(await provider.GetSignedInAccountAsync());
        Assert.Null(await CreateProvider().GetSignedInAccountAsync());
    }

    [Fact]
    public async Task PersistentCache_IsProtectedWithWindowsUserDpapi()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var app = CreateApplication();
        var store = await MsalCachePersistence.RegisterAsync(app.UserTokenCache, _root);
        var plaintext = Encoding.UTF8.GetBytes(AccountCache);
        store.SaveUnencryptedTokenCache(plaintext);

        var ciphertext = await File.ReadAllBytesAsync(Path.Combine(_root, MsalCachePersistence.FileName));

        Assert.NotEqual(plaintext, ciphertext);
        Assert.Equal(plaintext, ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser));
    }

    private MsalTokenProvider CreateProvider() => new(
        CreateApplication(), new AppPaths(_root), NullLogger<MsalTokenProvider>.Instance);

    private static IPublicClientApplication CreateApplication() =>
        PublicClientApplicationBuilder.Create(MsalTokenProvider.ClientId)
            .WithAuthority(AadAuthorityAudience.PersonalMicrosoftAccount)
            .Build();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
