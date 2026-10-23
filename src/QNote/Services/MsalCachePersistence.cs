using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace QNote.Services;

/// <summary>
/// Persists MSAL's account metadata as well as its cache. WAM owns broker
/// credentials, but GetAccountsAsync still needs MSAL's cache after a restart.
/// The Microsoft helper provides Windows user-bound DPAPI and file locking.
/// </summary>
internal static class MsalCachePersistence
{
    internal const string FileName = "msalcache.bin3";

    internal static Task<MsalCacheHelper> RegisterAsync(ITokenCache cache, string directory) => Task.Run(async () =>
    {
        var storage = new StorageCreationPropertiesBuilder(FileName, directory).Build();
        var helper = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
        helper.VerifyPersistence();
        helper.RegisterCache(cache);
        return helper;
    });
}
