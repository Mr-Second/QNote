using Microsoft.Extensions.Logging.Abstractions;
using QNote.Infrastructure;
using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// <see cref="StartupUpdateCheck"/> channel dispatch. The load-bearing
/// contract is the ZERO-DRIFT Store path: while the Store backend reports
/// nothing, no dialog — even when a GitHub tag is already ahead of a pending
/// Store certification (user ruling 2026-10-04).
/// </summary>
public class StartupUpdateCheckTests
{
    private sealed class FakeGitHub : IGitHubUpdateService
    {
        public UpdateCheckResult Result { get; set; } =
            new(UpdateCheckStatus.UpToDate, "https://github.com/Mr-Second/QNote/releases/latest", "v1.0.0", null);

        public Task<UpdateCheckResult> CheckLatestAsync(CancellationToken ct = default) => Task.FromResult(Result);
    }

    private sealed class FakeStore : IStoreUpdateChecker
    {
        public StoreUpdateInfo? Result { get; set; }
        public Task<StoreUpdateInfo?> CheckAsync() => Task.FromResult(Result);
    }

    private static StartupUpdateCheck Create(FakeGitHub gitHub, FakeStore store, bool isPackaged) =>
        new(gitHub, store, isPackaged, NullLogger<StartupUpdateCheck>.Instance);

    [Fact]
    public async Task Portable_NewerGitHubRelease_ReturnsInfoWithReleaseUrl()
    {
        var gitHub = new FakeGitHub
        {
            Result = new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                "https://github.com/Mr-Second/QNote/releases/tag/v1.5.1",
                "v1.5.1", null, "- 新增：启动时检查更新"),
        };

        var info = await Create(gitHub, new FakeStore(), isPackaged: false).CheckAsync();

        Assert.NotNull(info);
        Assert.Equal("v1.5.1", info.Version);
        Assert.Equal("- 新增：启动时检查更新", info.Notes);
        Assert.Equal("https://github.com/Mr-Second/QNote/releases/tag/v1.5.1", info.Target.ToString());
    }

    [Fact]
    public async Task Portable_UpToDateOrFailed_StaysSilent()
    {
        var upToDate = new FakeGitHub
        {
            Result = new UpdateCheckResult(UpdateCheckStatus.UpToDate, "url", "v1.0.0", null),
        };
        Assert.Null(await Create(upToDate, new FakeStore(), isPackaged: false).CheckAsync());

        var failed = new FakeGitHub { Result = UpdateCheckResult.Failed("timeout") };
        Assert.Null(await Create(failed, new FakeStore(), isPackaged: false).CheckAsync());
    }

    [Fact]
    public async Task Store_NoStoreUpdate_StaysSilentEvenWhenGitHubIsAhead()
    {
        // The zero-drift contract: GitHub tag v1.5.1 is out, the Store backend
        // has nothing yet (pending certification) → NO dialog.
        var gitHub = new FakeGitHub
        {
            Result = new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable, "https://github.com/x", "v1.5.1", null, "notes"),
        };
        var store = new FakeStore { Result = null };

        Assert.Null(await Create(gitHub, store, isPackaged: true).CheckAsync());
    }

    [Fact]
    public async Task Store_UpdateWithMatchingTag_CarriesChangelogAndStoreTarget()
    {
        var gitHub = new FakeGitHub
        {
            Result = new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable, "https://github.com/x", "v1.5.1", null, "- notes here"),
        };
        var store = new FakeStore { Result = new StoreUpdateInfo("1.5.1.0") };

        var info = await Create(gitHub, store, isPackaged: true).CheckAsync();

        Assert.NotNull(info);
        Assert.Equal("v1.5.1", info.Version);
        Assert.Equal("- notes here", info.Notes);
        Assert.Equal(StoreLink.ProductPage, info.Target);
    }

    [Fact]
    public async Task Store_UpdateWithMismatchedTag_ShowsVersionOnly()
    {
        // Store serves 1.5.1 but the GitHub latest tag is already v1.5.2 — the
        // newer release's notes must not pass off as 1.5.1's changelog.
        var gitHub = new FakeGitHub
        {
            Result = new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable, "https://github.com/x", "v1.5.2", null, "- other notes"),
        };
        var store = new FakeStore { Result = new StoreUpdateInfo("1.5.1.0") };

        var info = await Create(gitHub, store, isPackaged: true).CheckAsync();

        Assert.NotNull(info);
        Assert.Equal("v1.5.1", info.Version);
        Assert.Null(info.Notes);
        Assert.Equal(StoreLink.ProductPage, info.Target);
    }

    [Fact]
    public async Task Store_UpdateWithGitHubUnreachable_StillShowsVersionOnly()
    {
        var gitHub = new FakeGitHub { Result = UpdateCheckResult.Failed("offline") };
        var store = new FakeStore { Result = new StoreUpdateInfo("1.5.1.0") };

        var info = await Create(gitHub, store, isPackaged: true).CheckAsync();

        Assert.NotNull(info);
        Assert.Equal("v1.5.1", info.Version);
        Assert.Null(info.Notes);
    }

    [Theory]
    [InlineData("1.5.1.0", "v1.5.1")]
    [InlineData("1.10.0.0", "v1.10.0")] // real zero in the minor — must survive
    [InlineData("2.0.0.0", "v2.0.0")]
    [InlineData("1.5.1", "v1.5.1")] // short store version degrades gracefully
    public async Task StoreVersionDisplay_DropsRevisionKeepsRealZeros(string storeVersion, string expected)
    {
        var store = new FakeStore { Result = new StoreUpdateInfo(storeVersion) };
        var gitHub = new FakeGitHub { Result = UpdateCheckResult.Failed("offline") };

        var info = await Create(gitHub, store, isPackaged: true).CheckAsync();

        Assert.Equal(expected, info!.Version);
    }
}
