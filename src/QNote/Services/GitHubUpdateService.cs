using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using QNote.Update;

namespace QNote.Services;

/// <summary>
/// <see cref="IGitHubUpdateService"/> over the GitHub Releases REST API. One
/// <see cref="HttpClient"/> for the singleton lifetime (default handler =
/// system proxy defaults), a User-Agent header (the GitHub API rejects requests
/// without one), ~10 s timeout. Version comparison is the pure Core helper
/// <see cref="VersionTag"/>; the running version comes from the main module's
/// FileVersion (exe VERSIONINFO). JSON parsing uses a source-generated context
/// (trim-safe; no reflection — see csharp-conventions). All failures map to
/// <see cref="UpdateCheckStatus.Failed"/> — no exceptions cross the boundary.
/// </summary>
public sealed class GitHubUpdateService : IGitHubUpdateService
{
    /// <summary>Latest-release API endpoint — the ONE place this URL lives.</summary>
    private const string LatestReleaseUrl = "https://api.github.com/repos/Mr-Second/QNote/releases/latest";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<GitHubUpdateService> _log;
    private readonly HttpClient _http;

    public GitHubUpdateService(ILogger<GitHubUpdateService> log)
    {
        _log = log;
        _http = new HttpClient { Timeout = Timeout };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "QNote");
    }

    public async Task<UpdateCheckResult> CheckLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(LatestReleaseUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("GitHub release check failed with HTTP {Status}.", (int)response.StatusCode);
                return UpdateCheckResult.Failed($"HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var release = JsonSerializer.Deserialize(json, GitHubReleaseJsonContext.Default.GitHubReleaseDto);
            if (release?.TagName is not { Length: > 0 } tag || release.HtmlUrl is not { Length: > 0 } url)
            {
                _log.LogWarning("GitHub release payload missing tag_name/html_url.");
                return UpdateCheckResult.Failed("missing tag_name/html_url");
            }

            return VersionTag.Compare(tag, CurrentVersion()) switch
            {
                VersionTagComparison.Newer => new UpdateCheckResult(
                    UpdateCheckStatus.UpdateAvailable, url, tag, null,
                    ReleaseNotes.ExtractChangelog(release.Body)),
                VersionTagComparison.Invalid => UpdateCheckResult.Failed($"unparseable release tag '{tag}'"),
                _ => new UpdateCheckResult(UpdateCheckStatus.UpToDate, url, tag, null),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient timeout surfaces as TaskCanceled/OperationCanceled.
            _log.LogWarning("GitHub release check timed out after {Seconds} s.", Timeout.TotalSeconds);
            return UpdateCheckResult.Failed("timeout");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GitHub release check failed.");
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Running app version from the main module's VERSIONINFO (e.g. "1.1.0.0",
    /// pinned by the csproj FileVersion). Throws when unreadable so the broad
    /// catch maps it to a Failed check — an unknown version must never compare
    /// as "everything is newer".
    /// </summary>
    private static string CurrentVersion()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Process path unavailable.");
        var version = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
        return string.IsNullOrEmpty(version)
            ? throw new InvalidOperationException("Main module has no FileVersion.")
            : version;
    }
}

// ---------- GitHub API payload (trim-safe source-gen JSON; see csharp-conventions) ----------

internal sealed record GitHubReleaseDto(string? TagName, string? HtmlUrl, string? Body);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubReleaseDto))]
internal sealed partial class GitHubReleaseJsonContext : JsonSerializerContext;
