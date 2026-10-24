using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace QNote.Sync;

/// <summary>
/// The L2 OneDrive <see cref="ISyncDriver"/>: raw Microsoft Graph REST over one
/// <c>HttpClient</c> + source-gen JSON (the Graph SDK was rejected at survey time —
/// 45.2 MB of reflection-heavy managed payload against the AOT/memory goals). The
/// storage root is the special <b>approot</b> (<c>Apps/QNote</c> in the user's
/// OneDrive — per-user, uninstallable, invisible to other apps).
///
/// Driver-root mapping: every engine logical path (<c>notes/{uuid}.qnote</c>, …)
/// becomes <c>{Base}:/{path}:/content</c> (or <c>/children</c> for listings); the
/// engine never concatenates paths. Errors funnel into
/// <see cref="SyncDriverException"/> via the pinned taxonomy: 401/403 →
/// AuthRequired, 412 → PreconditionFailed, 507 → QuotaExceeded, network/timeout →
/// NetworkUnavailable, 429 → <see cref="ThrottleGate.Defer"/> + ServiceError,
/// everything else → ServiceError. A token acquisition failure from
/// <see cref="ITokenProvider"/> is wrapped into the same taxonomy (the engine's
/// cycle-level catch only knows <see cref="SyncDriverException"/>).
///
/// Redirect discipline: <c>GET ...:/content</c> answers 302 to a pre-authenticated
/// CDN URL — the redirect is followed WITHOUT the Authorization header (CDNs
/// reject forwarded bearer tokens with 401), which is why this driver owns an
/// <see cref="HttpClient"/> with <c>AllowAutoRedirect = false</c>.
///
/// Budget discipline: <see cref="ThrottleGate"/> (optional, injected) admits every
/// top-level Graph request; redirect followers are pre-authenticated CDN hops and
/// deliberately do NOT consume budget.
/// </summary>
public sealed class OneDriveDriver : ISyncDriver, IDisposable
{
    public const string DefaultBaseUrl = "https://graph.microsoft.com/v1.0/me/drive/special/approot";

    private const int PageSize = 200;
    private const int MaxListingPages = 1000;
    private const int MaxRedirectHops = 4;

    private readonly ITokenProvider _tokens;
    private readonly ThrottleGate? _throttle;
    private readonly ILogger<OneDriveDriver> _log;
    private readonly HttpClient _http;
    private readonly bool _ownsHandler;
    private readonly string _baseUrl;
    private string? _rootItemUrl;
    private readonly HashSet<string> _ensuredFolders = new(StringComparer.Ordinal);

    public OneDriveDriver(
        ITokenProvider tokens,
        ILogger<OneDriveDriver> log,
        ThrottleGate? throttle = null,
        HttpMessageHandler? handler = null,
        string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(log);
        _tokens = tokens;
        _throttle = throttle;
        _log = log;
        _baseUrl = baseUrl ?? DefaultBaseUrl;
        if (handler is null)
        {
            handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            };
            _ownsHandler = true;
        }
        _http = new HttpClient(handler, disposeHandler: _ownsHandler);
    }

    /// <summary>
    /// OneDrive honors <c>Range</c> on content downloads (verified by the scope
    /// probe before L2 ships; flip to false there if a backend stops honoring it —
    /// the engine then full-downloads every probe, which stays correct).
    /// </summary>
    public bool SupportsRangeRead => true;

    public void Dispose() => _http.Dispose();

    // ---------- ISyncDriver ----------

    public async Task<DriverListing> ListAsync(string folderPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(folderPath);

        var files = new List<DriverFile>();
        var next = $"{_baseUrl}:/{EscapePath(folderPath)}:/children?$top={PageSize}&$select=id,name,eTag,size";
        for (var page = 0; next is not null; page++)
        {
            if (page >= MaxListingPages)
                throw new SyncDriverException(
                    SyncDriverErrorKind.ServiceError,
                    $"The listing of '{folderPath}' exceeded {MaxListingPages} pages; aborting the pagination walk.");

            var resp = await SendCommandAsync(HttpMethod.Get, next, content: null, ifMatch: null, ct);
            OneDriveChildrenResponse? dto;
            using (resp)
            {
                // A folder that does not exist yet (fresh cloud, first cycle) is an
                // EMPTY listing, not an error — phase 1 UPLOAD creates it.
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    if (page == 0)
                        return new DriverListing(files, IsTruncated: false);
                    throw new SyncDriverException(
                        SyncDriverErrorKind.ServiceError,
                        $"The folder '{folderPath}' disappeared mid-listing; the cycle should retry.");
                }
                await EnsureSuccessAsync(resp, ct);
                dto = await ReadJsonAsync(resp, OneDriveGraphJsonContext.Default.OneDriveChildrenResponse, ct);
            }

            foreach (var item in dto?.Value ?? [])
            {
                if (string.IsNullOrEmpty(item.Name))
                    continue;
                files.Add(new DriverFile($"{folderPath}/{item.Name}", item.ETag ?? string.Empty, item.Size ?? -1));
            }
            next = dto?.NextLink;
        }
        return new DriverListing(files, IsTruncated: false);
    }

    public async Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default)
        => await GetContentBytesAsync(path, rangeHeader: null, ct);

    public async Task<DriverReadResult?> ReadFileWithEtagAsync(string path, CancellationToken ct = default)
    {
        var before = await ReadTagAsync();
        if (before is null) return null;
        var bytes = await ReadFileAsync(path, ct);
        var after = await ReadTagAsync();
        if (bytes is null || before != after)
            throw new SyncDriverException(SyncDriverErrorKind.PreconditionFailed, "The cloud file changed while reading its version.");
        return new DriverReadResult(bytes, after ?? "");

        async Task<string?> ReadTagAsync()
        {
            using var response = await SendCommandAsync(HttpMethod.Get, $"{_baseUrl}:/{EscapePath(path)}?$select=eTag", null, null, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            await EnsureSuccessAsync(response, ct);
            var item = await ReadJsonAsync(response, OneDriveGraphJsonContext.Default.OneDriveDriveItem, ct);
            return item?.ETag ?? "";
        }
    }

    public async Task<byte[]?> ReadRangeAsync(string path, long offset, int count, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var body = await GetContentBytesAsync(path, $"bytes={offset}-{offset + count - 1}", ct);
        if (body is null)
            return null;
        // 206 → exactly the requested window; 200 (server ignored Range) → the full
        // body, sliced to the contract; a short file returns as-is.
        return body.Length <= count ? body : body[..count];
    }

    public async Task<DriverWriteResult> WriteFileAsync(
        string path, byte[] content, string? ifMatchEtag = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);

        var parent = ParentFolder(path);
        if (parent.Length > 0)
            await EnsureFolderAsync(parent, ct);

        var resp = await SendCommandAsync(HttpMethod.Put, $"{_baseUrl}:/{EscapePath(path)}:/content", content, ifMatchEtag, ct);
        using (resp)
        {
            await EnsureSuccessAsync(resp, ct);
            var item = await ReadJsonAsync(resp, OneDriveGraphJsonContext.Default.OneDriveDriveItem, ct);
            if (item?.Id is null)
                throw new SyncDriverException(
                    SyncDriverErrorKind.ServiceError, "The OneDrive PUT response carried no item id.");
            return new DriverWriteResult(item.Id, item.ETag ?? string.Empty);
        }
    }

    public async Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Path-addressed item delete (the ':/content' suffix is content-stream
        // only; DELETE targets the item resource).
        var resp = await SendCommandAsync(HttpMethod.Delete, $"{_baseUrl}:/{EscapePath(path)}", null, null, ct);
        using (resp)
        {
            // Idempotent by contract: a missing file is success.
            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NotFound)
                return;
            await EnsureSuccessAsync(resp, ct);
        }
    }

    // ---------- HTTP plumbing ----------

    /// <summary>
    /// Content download with manual redirect following and an optional Range
    /// header. One hop budget is charged for the whole logical read; the
    /// pre-authenticated redirect targets are not billed.
    /// </summary>
    private async Task<byte[]?> GetContentBytesAsync(string path, string? rangeHeader, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}:/{EscapePath(path)}:/content");
        try
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));
            if (rangeHeader is not null)
                request.Headers.TryAddWithoutValidation("Range", rangeHeader);

            for (var hop = 0; ; hop++)
            {
                var resp = await SendNetworkAsync(request, ct, countInBudget: true);
                if (hop < MaxRedirectHops
                    && (int)resp.StatusCode is 301 or 302 or 307 or 308
                    && resp.Headers.Location is { } location)
                {
                    resp.Dispose();
                    request.Dispose();
                    // The location is pre-authenticated (token in the query) — the
                    // Authorization header must NOT follow it.
                    request = new HttpRequestMessage(HttpMethod.Get, location);
                    if (rangeHeader is not null)
                        request.Headers.TryAddWithoutValidation("Range", rangeHeader);
                    continue;
                }
                using (resp)
                {
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                        return null;
                    await EnsureSuccessAsync(resp, ct);
                    return await resp.Content.ReadAsByteArrayAsync(ct);
                }
            }
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>One authorized, budgeted, single-shot request (no redirect following).</summary>
    private async Task<HttpResponseMessage> SendCommandAsync(
        HttpMethod method, string url, byte[]? content, string? ifMatch, CancellationToken ct,
        string contentType = "application/octet-stream")
    {
        var request = new HttpRequestMessage(method, url);
        try
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));
            if (ifMatch is not null)
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            if (content is not null)
            {
                request.Content = new ByteArrayContent(content);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            }
            return await SendNetworkAsync(request, ct, countInBudget: true);
        }
        finally
        {
            request.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendNetworkAsync(
        HttpRequestMessage request, CancellationToken ct, bool countInBudget)
    {
        if (countInBudget && _throttle is { } gate && !gate.TryAcquire(out var retryAfter))
            throw new SyncDriverException(
                SyncDriverErrorKind.ServiceError,
                $"The OneDrive request budget is exhausted; retry after {retryAfter.TotalSeconds:F0}s.", retryAfter: retryAfter);

        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SyncDriverException(
                SyncDriverErrorKind.NetworkUnavailable, $"OneDrive is unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // A timeout surfaces as TaskCanceled without the caller's token being
            // cancelled; a genuine cancellation propagates as OCE per the error spec.
            throw new SyncDriverException(
                SyncDriverErrorKind.NetworkUnavailable, $"OneDrive timed out: {ex.Message}", ex);
        }
    }

    private async Task EnsureFolderAsync(string folder, CancellationToken ct)
    {
        var segments = folder.Split('/');
        var current = string.Empty;
        var rootItemUrl = await GetRootItemUrlAsync(ct);
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                throw new ArgumentException($"The folder path '{folder}' has an empty segment.", nameof(folder));
            current = current.Length == 0 ? segment : $"{current}/{segment}";
            if (_ensuredFolders.Contains(current))
                continue;

            var body = JsonSerializer.SerializeToUtf8Bytes(
                new OneDriveFolderCreateRequest { Name = segment }, OneDriveGraphJsonContext.Default.OneDriveFolderCreateRequest);
            var parent = ParentFolder(current);
            var parentItemUrl = rootItemUrl;
            if (parent.Length > 0)
            {
                using var parentResponse = await SendCommandAsync(
                    HttpMethod.Get, $"{rootItemUrl}:/{EscapePath(parent)}", null, null, ct);
                await EnsureSuccessAsync(parentResponse, ct);
                var parentItem = await ReadJsonAsync(parentResponse, OneDriveGraphJsonContext.Default.OneDriveDriveItem, ct);
                if (string.IsNullOrEmpty(parentItem?.Id))
                    throw new SyncDriverException(SyncDriverErrorKind.ServiceError, "The parent folder response carried no item id.");
                parentItemUrl = new Uri(new Uri(rootItemUrl), Uri.EscapeDataString(parentItem.Id)).AbsoluteUri;
            }
            var resp = await SendCommandAsync(
                HttpMethod.Post, $"{parentItemUrl}/children", body, ifMatch: null, ct, contentType: "application/json");
            using (resp)
            {
                // 409 nameAlreadyExists = the folder is already there (a peer
                // created it, or a previous run's cache was lost) — expected.
                if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.Conflict)
                    await EnsureSuccessAsync(resp, ct);
            }
            _ensuredFolders.Add(current);
        }
    }

    private async Task<string> GetRootItemUrlAsync(CancellationToken ct)
    {
        if (_rootItemUrl is not null)
            return _rootItemUrl;

        // The special-folder alias supports content access, but folder creation
        // must target the concrete driveItem's /children collection.
        using var response = await SendCommandAsync(HttpMethod.Get, $"{_baseUrl}?$select=id", null, null, ct);
        await EnsureSuccessAsync(response, ct);
        var item = await ReadJsonAsync(response, OneDriveGraphJsonContext.Default.OneDriveDriveItem, ct);
        if (string.IsNullOrEmpty(item?.Id))
            throw new SyncDriverException(SyncDriverErrorKind.ServiceError, "The app folder response carried no item id.");
        _rootItemUrl = new Uri(new Uri(_baseUrl), $"../items/{Uri.EscapeDataString(item.Id)}").AbsoluteUri;
        return _rootItemUrl;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        try
        {
            return (await _tokens.AcquireSilentAsync(ct)).AccessToken;
        }
        catch (TokenProviderException ex)
        {
            var kind = ex.Kind switch
            {
                TokenProviderErrorKind.AuthRequired => SyncDriverErrorKind.AuthRequired,
                TokenProviderErrorKind.NetworkUnavailable => SyncDriverErrorKind.NetworkUnavailable,
                _ => SyncDriverErrorKind.ServiceError,
            };
            throw new SyncDriverException(kind, $"Token acquisition failed: {ex.Message}", ex);
        }
    }

    // ---------- status mapping ----------

    private async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return;
        var snippet = await ReadSnippetAsync(resp, ct);
        var description =
            $"OneDrive returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}{ErrorDetail(snippet)}: {snippet}";
        throw MapStatus((int)resp.StatusCode, resp, description);
    }

    private SyncDriverException MapStatus(int code, HttpResponseMessage resp, string description)
    {
        if (code is 401 or 403)
            return new SyncDriverException(SyncDriverErrorKind.AuthRequired, description);
        if (code == 412)
            return new SyncDriverException(SyncDriverErrorKind.PreconditionFailed, description);
        if (code is 429 or 503)
        {
            var retry = resp.Headers.RetryAfter?.Delta
                ?? (resp.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            if (retry is { } r && r > TimeSpan.Zero)
            {
                _throttle?.Defer(r);
                description += $" (throttled; pausing the request budget for {r.TotalSeconds:F0}s)";
            }
            return new SyncDriverException(SyncDriverErrorKind.ServiceError, description, retryAfter: retry);
        }
        if (code == 507)
            return new SyncDriverException(SyncDriverErrorKind.QuotaExceeded, description);
        return new SyncDriverException(SyncDriverErrorKind.ServiceError, description);
    }

    private async Task<T?> ReadJsonAsync<T>(HttpResponseMessage resp, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : class
    {
        var bytes = resp.Content is null ? [] : await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0)
            return null;
        try
        {
            return JsonSerializer.Deserialize(bytes, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new SyncDriverException(
                SyncDriverErrorKind.ServiceError, $"OneDrive returned an unparseable body: {ex.Message}", ex);
        }
    }

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            if (resp.Content is null)
                return string.Empty;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length <= 512 ? System.Text.Encoding.UTF8.GetString(bytes) : System.Text.Encoding.UTF8.GetString(bytes, 0, 512);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    private static string ErrorDetail(string snippet)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize(snippet, OneDriveGraphJsonContext.Default.OneDriveErrorResponse);
            return parsed?.Error is { } error ? $" ({error.Code})" : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    // ---------- small helpers ----------

    private static string EscapePath(string logicalPath) =>
        string.Join("/", logicalPath.Split('/').Select(Uri.EscapeDataString));

    private static string ParentFolder(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash <= 0 ? string.Empty : path[..lastSlash];
    }
}
