using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace QNote.Sync;

/// <summary>A WebDAV transport rooted at a user-selected, dedicated HTTP(S) folder.</summary>
public sealed class WebDavDriver : ISyncDriver, IDisposable
{
    private static readonly XNamespace Dav = "DAV:";
    private const string Properties = "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:getetag/><d:getcontentlength/><d:resourcetype/></d:prop></d:propfind>";
    private readonly Uri _root;
    private readonly AuthenticationHeaderValue _authorization;
    private readonly HttpClient _http;
    private readonly ThrottleGate _throttle;
    private readonly HashSet<string> _folders = new(StringComparer.Ordinal);

    public WebDavDriver(string folderUrl, string username, string password,
        ThrottleGate throttle, HttpMessageHandler? handler = null)
    {
        _root = NormalizeFolderUrl(folderUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (username.Contains(':') || username.Any(char.IsControl) || password.Any(char.IsControl))
            throw new ArgumentException("WebDAV credentials contain invalid characters.");
        _authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
        _throttle = throttle;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        }, disposeHandler: handler is null) { Timeout = TimeSpan.FromMinutes(2) };
    }

    // ponytail: full reads are already cached by the engine. Add Range probing
    // only if real WebDAV libraries make downloading headers materially costly.
    public bool SupportsRangeRead => false;

    public static Uri NormalizeFolderUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || uri.Host.Length == 0
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("The WebDAV folder must be an HTTP or HTTPS URL without credentials, a query or a fragment.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    /// <summary>Verifies access without creating a directory or touching user data.</summary>
    public async Task CheckConnectionAsync(CancellationToken ct = default)
    {
        if (await IsCollectionAsync(_root, ct))
            return;
        // A dedicated folder may be new, but its parent must already exist.
        if (!await IsCollectionAsync(new Uri(_root, "../"), ct))
            throw new SyncDriverException(SyncDriverErrorKind.ServiceError,
                "The WebDAV folder and its parent do not exist.");
    }

    public async Task<DriverListing> ListAsync(string folderPath, CancellationToken ct = default)
    {
        var folder = Resolve(folderPath + "/");
        using var response = await PropFindAsync(folder, "1", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new DriverListing([], false);
        var resources = await ReadResourcesAsync(response, ct);
        var files = new List<DriverFile>();
        var foundCollection = false;
        foreach (var resource in resources)
        {
            var uri = ResourceUri(resource, folder);
            var properties = SuccessfulProperties(resource);
            var isCollection = properties.Any(p => p.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null);
            if (uri.AbsolutePath.TrimEnd('/') == folder.AbsolutePath.TrimEnd('/'))
            {
                if (!isCollection)
                    throw ProtocolError("The requested WebDAV path is not a readable collection.");
                foundCollection = true;
                continue;
            }
            var relative = folder.MakeRelativeUri(uri).ToString();
            if (relative.Length == 0 || relative.TrimEnd('/').Contains('/'))
                throw ProtocolError("The directory response contains a non-child resource.");
            if (isCollection)
                continue;
            var name = Uri.UnescapeDataString(relative);
            if (name is "." or ".." || name.Contains('/') || name.Contains('\\'))
                throw ProtocolError("The directory response contains an invalid filename.");
            var etag = StrongEtag(properties.Select(p => (string?)p.Element(Dav + "getetag")).FirstOrDefault(v => v is not null));
            var sizeText = properties.Select(p => (string?)p.Element(Dav + "getcontentlength")).FirstOrDefault(v => v is not null);
            var size = long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var length) ? length : -1;
            files.Add(new DriverFile(folderPath + "/" + name, etag, size));
        }
        if (!foundCollection)
            throw ProtocolError("The directory response omits the requested collection.");
        // Nutstore counts both the requested collection and child collections.
        var capped = _root.IdnHost.Equals("dav.jianguoyun.com", StringComparison.OrdinalIgnoreCase)
            && resources.Count >= 750;
        return new DriverListing(files, capped);
    }

    public async Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, Resolve(path), ct: ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        EnsureSuccess(response);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]?> ReadRangeAsync(string path, long offset, int count, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var bytes = await ReadFileAsync(path, ct);
        if (bytes is null)
            return null;
        var start = (int)Math.Min(offset, bytes.LongLength);
        return bytes.AsSpan(start, Math.Min(count, bytes.Length - start)).ToArray();
    }

    public async Task<DriverWriteResult> WriteFileAsync(string path, byte[] content,
        string? ifMatchEtag = null, CancellationToken ct = default)
    {
        var uri = Resolve(path);
        var parent = path.LastIndexOf('/');
        await EnsureFolderAsync(parent < 0 ? "" : path[..parent] + "/", ct);
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(HttpMethod.Put, uri, body, ifMatchEtag, ct);
        EnsureSuccess(response);
        return new DriverWriteResult(uri.AbsoluteUri, StrongEtag(response.Headers.ETag?.ToString()));
    }

    public async Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, Resolve(path), ct: ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
            EnsureSuccess(response);
    }

    public void Dispose() => _http.Dispose();

    private Uri Resolve(string path)
    {
        if (path.Contains('\\') || path.StartsWith('/') || path.Contains('?') || path.Contains('#')
            || path.Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException("The WebDAV path must be root-relative.", nameof(path));
        return new Uri(_root, string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));
    }

    private Uri ResourceUri(XElement resource, Uri folder)
    {
        var href = (string?)resource.Element(Dav + "href");
        if (string.IsNullOrEmpty(href) || !Uri.TryCreate(folder, href, out var uri)
            || uri.Scheme != _root.Scheme || uri.Authority != _root.Authority
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !uri.AbsolutePath.StartsWith(folder.AbsolutePath, StringComparison.Ordinal)
                && uri.AbsolutePath.TrimEnd('/') != folder.AbsolutePath.TrimEnd('/'))
            throw ProtocolError("The directory response contains a resource outside the requested folder.");
        return uri;
    }

    private async Task EnsureFolderAsync(string path, CancellationToken ct)
    {
        if (_folders.Contains(path))
            return;
        if (path.Length > 0)
        {
            var trimmed = path.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            await EnsureFolderAsync(slash < 0 ? "" : trimmed[..slash] + "/", ct);
        }
        var uri = path.Length == 0 ? _root : Resolve(path);
        if (!await IsCollectionAsync(uri, ct))
        {
            using var response = await SendAsync(new HttpMethod("MKCOL"), uri, ct: ct);
            if (response.StatusCode != HttpStatusCode.MethodNotAllowed)
                EnsureSuccess(response);
            if (!await IsCollectionAsync(uri, ct))
                throw ProtocolError("The WebDAV directory was not created.");
        }
        _folders.Add(path);
    }

    private async Task<bool> IsCollectionAsync(Uri uri, CancellationToken ct)
    {
        using var response = await PropFindAsync(uri, "0", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        var resources = await ReadResourcesAsync(response, ct);
        foreach (var resource in resources)
        {
            var resourceUri = ResourceUri(resource, uri);
            if (resourceUri.AbsolutePath.TrimEnd('/') == uri.AbsolutePath.TrimEnd('/')
                && SuccessfulProperties(resource).Any(p => p.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null))
                return true;
        }
        throw ProtocolError("The WebDAV path is not a collection.");
    }

    private Task<HttpResponseMessage> PropFindAsync(Uri uri, string depth, CancellationToken ct) =>
        SendAsync(new HttpMethod("PROPFIND"), uri,
            new StringContent(Properties, Encoding.UTF8, "application/xml"), ct: ct, depth: depth);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri,
        HttpContent? content = null, string? ifMatch = null, CancellationToken ct = default, string? depth = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!_throttle.TryAcquire(out var delay))
            throw new SyncDriverException(SyncDriverErrorKind.ServiceError,
                $"The WebDAV request budget is paused for {delay.TotalSeconds:F0} seconds.");
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = _authorization;
        if (depth is not null)
            request.Headers.Add("Depth", depth);
        var etag = StrongEtag(ifMatch);
        if (etag.Length != 0)
            request.Headers.IfMatch.ParseAdd(etag);
        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new SyncDriverException(SyncDriverErrorKind.NetworkUnavailable,
                "The WebDAV service could not be reached.", ex);
        }
    }

    private void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        if ((int)response.StatusCode is 429 or 503)
        {
            var delay = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMinutes(1));
            _throttle.Defer(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1));
        }
        var kind = (int)response.StatusCode switch
        {
            401 or 403 => SyncDriverErrorKind.AuthRequired,
            412 => SyncDriverErrorKind.PreconditionFailed,
            507 => SyncDriverErrorKind.QuotaExceeded,
            _ => SyncDriverErrorKind.ServiceError,
        };
        // Never include server error bodies: they can echo credentials or data.
        throw new SyncDriverException(kind, $"The WebDAV service returned HTTP {(int)response.StatusCode}.");
    }

    private async Task<List<XElement>> ReadResourcesAsync(HttpResponseMessage response, CancellationToken ct)
    {
        EnsureSuccess(response);
        if ((int)response.StatusCode != 207)
            throw ProtocolError("WebDAV PROPFIND did not return Multi-Status.");
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 16 * 1024 * 1024,
            });
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
            if (document.Root?.Name != Dav + "multistatus")
                throw ProtocolError("WebDAV returned an invalid Multi-Status document.");
            return document.Root.Elements(Dav + "response").ToList();
        }
        catch (XmlException ex)
        {
            throw new SyncDriverException(SyncDriverErrorKind.ServiceError, "WebDAV returned invalid XML.", ex);
        }
    }

    private static List<XElement> SuccessfulProperties(XElement resource)
    {
        var properties = new List<XElement>();
        foreach (var group in resource.Elements(Dav + "propstat"))
        {
            var status = ((string?)group.Element(Dav + "status"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (status is { Length: >= 2 } && status[1] == "200" && group.Element(Dav + "prop") is { } prop)
                properties.Add(prop);
        }
        if (properties.Count == 0)
            throw ProtocolError("WebDAV did not return readable resource properties.");
        return properties;
    }

    private static string StrongEtag(string? value) =>
        EntityTagHeaderValue.TryParse(value, out var etag) && !etag.IsWeak && etag.Tag != "*"
            ? etag.ToString() : string.Empty;

    private static SyncDriverException ProtocolError(string message) =>
        new(SyncDriverErrorKind.ServiceError, message);
}
