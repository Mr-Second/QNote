using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using QNote.Data;
using QNote.Models;
using QNote.Sync;

namespace QNote.Tests;

public sealed class WebDavDriverTests
{
    [Theory]
    [InlineData("ftp://example.com/QNote/")]
    [InlineData("https://user:password@example.com/QNote/")]
    [InlineData("http://user:password@nas.local:5005/QNote/")]
    [InlineData("https://example.com/QNote/?token=x")]
    [InlineData("http://nas.local:5005/QNote/?token=x")]
    [InlineData("https://example.com/QNote/#fragment")]
    [InlineData("http://nas.local:5005/QNote/#fragment")]
    [InlineData("relative/path")]
    public void RejectsUnsafeRoot(string root) => Assert.Throws<ArgumentException>(() =>
        WebDavDriver.NormalizeFolderUrl(root));

    [Theory]
    [InlineData("https://dav.example.com/QNote", "https://dav.example.com/QNote/")]
    [InlineData(" HTTP://NAS.local:5005/dav/QNote ", "http://nas.local:5005/dav/QNote/")]
    [InlineData("http://192.168.1.20:5005/dav/", "http://192.168.1.20:5005/dav/")]
    [InlineData("http://nas-box:8080/QNote", "http://nas-box:8080/QNote/")]
    [InlineData("https://nas-box:5006/QNote", "https://nas-box:5006/QNote/")]
    [InlineData("http://[fd00::1]:5005/QNote", "http://[fd00::1]:5005/QNote/")]
    public void HttpAndHttpsRoots_NormalizeWithoutRestrictingNasHostnames(string input, string expected) =>
        Assert.Equal(expected, WebDavDriver.NormalizeFolderUrl(input).AbsoluteUri);

    [Fact]
    public async Task Listing_UsesDepthOne_AndOnlyReadsSuccessfulProperties()
    {
        using var server = new WebDavTestServer();
        server.ResponseOverride = request => request.Method.Method == "PROPFIND"
            ? Xml("""
                <d:multistatus xmlns:d="DAV:">
                  <d:response><d:href>/sync/notes/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                  <d:response><d:href>https://dav.example.com/sync/notes/file%20name.qnote</d:href>
                    <d:propstat><d:prop><d:getetag>"wrong"</d:getetag></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat>
                    <d:propstat><d:prop><d:resourcetype/><d:getetag>"right"</d:getetag><d:getcontentlength>42</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
                  </d:response>
                </d:multistatus>
                """) : null;
        using var driver = Driver(server);

        var listing = await driver.ListAsync("notes");

        var file = Assert.Single(listing.Files);
        Assert.Equal("notes/file name.qnote", file.Path);
        Assert.Equal("\"right\"", file.Etag);
        Assert.Equal(42, file.Size);
        Assert.False(listing.IsTruncated);
        var request = Assert.Single(server.Requests);
        Assert.Equal("1", request.Depth);
        Assert.StartsWith("Basic ", request.Authorization);
    }

    [Theory]
    [InlineData("https://other.example.com/sync/notes/file.qnote")]
    [InlineData("/sync/other/file.qnote")]
    [InlineData("/sync/notes/sub/file.qnote")]
    [InlineData("/sync/notes/a%2Fb.qnote")]
    public async Task Listing_RejectsResourcesOutsideItsImmediateFolder(string href)
    {
        using var server = new WebDavTestServer();
        server.ResponseOverride = _ => Xml($"""
            <d:multistatus xmlns:d="DAV:"><d:response><d:href>{href}</d:href>
            <d:propstat><d:prop><d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
            </d:response></d:multistatus>
            """);
        using var driver = Driver(server);

        var error = await Assert.ThrowsAsync<SyncDriverException>(() => driver.ListAsync("notes"));
        Assert.Equal(SyncDriverErrorKind.ServiceError, error.Kind);
    }

    [Theory]
    [InlineData("http", "https")]
    [InlineData("https", "http")]
    public async Task Listing_CannotSwitchScheme(string scheme, string otherScheme)
    {
        using var server = new WebDavTestServer();
        server.ResponseOverride = _ => Xml($"""
            <d:multistatus xmlns:d="DAV:"><d:response>
              <d:href>{otherScheme}://dav.example.com/sync/notes/file.qnote</d:href>
              <d:propstat><d:prop><d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
            </d:response></d:multistatus>
            """);
        using var driver = Driver(server, scheme: scheme);

        await Assert.ThrowsAsync<SyncDriverException>(() => driver.ListAsync("notes"));
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("http", "https")]
    [InlineData("https", "http")]
    public async Task Redirects_AreNotFollowedAcrossSchemes(string scheme, string otherScheme)
    {
        using var server = new WebDavTestServer();
        server.ResponseOverride = _ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri($"{otherScheme}://dav.example.com/sync/manifest.json") },
        };
        using var driver = Driver(server, scheme: scheme);

        var error = await Assert.ThrowsAsync<SyncDriverException>(() => driver.ReadFileAsync("manifest.json"));

        Assert.Equal(SyncDriverErrorKind.ServiceError, error.Kind);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Listing_RejectsDtd()
    {
        using var server = new WebDavTestServer();
        server.ResponseOverride = _ => Xml("<!DOCTYPE d [<!ENTITY x SYSTEM 'file:///secret'>]><d:multistatus xmlns:d='DAV:'>&x;</d:multistatus>");
        using var driver = Driver(server);
        await Assert.ThrowsAsync<SyncDriverException>(() => driver.ListAsync("notes"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<d:response><d:href>/sync/notes/</d:href><d:status>HTTP/1.1 403 Forbidden</d:status></d:response>")]
    public async Task Listing_CannotTreatUnreadableOrMissingCollectionAsEmpty(string resource)
    {
        using var server = new WebDavTestServer();
        server.ResponseOverride = _ => Xml($"<d:multistatus xmlns:d='DAV:'>{resource}</d:multistatus>");
        using var driver = Driver(server);

        await Assert.ThrowsAsync<SyncDriverException>(() => driver.ListAsync("notes"));
        Assert.DoesNotContain(server.Requests, r => r.Method is "PUT" or "DELETE" or "MKCOL");
    }

    [Theory]
    [InlineData("dav.jianguoyun.com", true)]
    [InlineData("dav.example.com", false)]
    public async Task Listing_NutstoreCapCountsCollections(string host, bool expectedTruncated)
    {
        using var server = new WebDavTestServer();
        server.Folders.Add("/sync/notes/");
        for (var i = 0; i < 749; i++)
            server.Folders.Add($"/sync/notes/{i}/");
        using var driver = Driver(server, host: host);

        var listing = await driver.ListAsync("notes");

        Assert.Empty(listing.Files);
        Assert.Equal(expectedTruncated, listing.IsTruncated);
    }

    [Fact]
    public async Task FreshFolder_CreatesAndConfirmsParents_BeforePut()
    {
        using var server = new WebDavTestServer();
        using var driver = Driver(server);

        await driver.WriteFileAsync("notes/file.qnote", [1, 2, 3]);

        Assert.Equal(new[] { "/sync/", "/sync/notes/" },
            server.Requests.Where(r => r.Method == "MKCOL").Select(r => r.Path));
        Assert.All(server.Requests.Where(r => r.Method == "PROPFIND"), r => Assert.Equal("0", r.Depth));
        Assert.Equal(new byte[] { 1, 2, 3 }, server.Files["/sync/notes/file.qnote"]);
        Assert.Equal("PUT", server.Requests[^1].Method);
    }

    [Fact]
    public async Task MissingParent409_DoesNotContinueToPut()
    {
        using var server = new WebDavTestServer();
        using var driver = Driver(server, folder: "missing/sync");

        await Assert.ThrowsAsync<SyncDriverException>(() => driver.WriteFileAsync("notes/file.qnote", [1]));

        Assert.DoesNotContain(server.Requests, r => r.Method == "PUT");
        Assert.DoesNotContain("/missing/sync/", server.Folders);
    }

    [Fact]
    public async Task ExistingNonCollection_IsNotAcceptedAsParent()
    {
        using var server = new WebDavTestServer();
        server.Files["/sync/"] = [1];
        using var driver = Driver(server);
        await Assert.ThrowsAsync<SyncDriverException>(() => driver.WriteFileAsync("manifest.json", [1]));
        Assert.DoesNotContain(server.Requests, r => r.Method == "PUT");
    }

    [Theory]
    [InlineData("\"strong\"", "\"strong\"")]
    [InlineData("W/\"weak\"", "")]
    [InlineData("unquoted", "")]
    [InlineData("", "")]
    public async Task WeakMissingOrInvalidEtags_DoNotBecomeFastPathTokens(string etag, string expected)
    {
        using var server = new WebDavTestServer { ReturnedEtag = etag };
        server.Folders.Add("/sync/notes/");
        server.Files["/sync/notes/file.qnote"] = [1];
        using var driver = Driver(server);

        var file = Assert.Single((await driver.ListAsync("notes")).Files);
        Assert.Equal(expected, file.Etag);
        var write = await driver.WriteFileAsync("notes/file.qnote", [2], etag);
        Assert.Equal(expected, write.Etag);
        Assert.Equal(expected.Length == 0 ? null : expected, server.Requests[^1].IfMatch);
    }

    [Fact]
    public async Task StaleStrongEtag_ReturnsPreconditionFailure()
    {
        using var server = new WebDavTestServer();
        server.Folders.Add("/sync/notes/");
        server.Files["/sync/notes/file.qnote"] = [1];
        using var driver = Driver(server);
        var error = await Assert.ThrowsAsync<SyncDriverException>(() =>
            driver.WriteFileAsync("notes/file.qnote", [2], "\"stale\""));
        Assert.Equal(SyncDriverErrorKind.PreconditionFailed, error.Kind);
        Assert.Equal(new byte[] { 1 }, server.Files["/sync/notes/file.qnote"]);
    }

    [Fact]
    public async Task UnsupportedRange_FallsBackToFullRead_AndMissingReadsAreExpected()
    {
        using var server = new WebDavTestServer();
        server.Files["/sync/notes/file.qnote"] = [1, 2, 3, 4];
        using var driver = Driver(server);

        Assert.False(driver.SupportsRangeRead);
        Assert.Equal(new byte[] { 2, 3 }, await driver.ReadRangeAsync("notes/file.qnote", 1, 2));
        Assert.Null(await driver.ReadFileAsync("missing"));
        Assert.Empty((await driver.ListAsync("notes")).Files);
        await driver.DeleteFileAsync("missing");
    }

    [Theory]
    [InlineData(401, SyncDriverErrorKind.AuthRequired)]
    [InlineData(403, SyncDriverErrorKind.AuthRequired)]
    [InlineData(507, SyncDriverErrorKind.QuotaExceeded)]
    [InlineData(302, SyncDriverErrorKind.ServiceError)]
    public async Task StatusErrors_AreClassified_WithoutEchoingServerBodies(int status, SyncDriverErrorKind kind)
    {
        using var server = new WebDavTestServer();
        server.ResponseOverride = _ => new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("secret-password"),
            Headers = { Location = new Uri("https://other.example.com/") },
        };
        using var driver = Driver(server);

        var error = await Assert.ThrowsAsync<SyncDriverException>(() => driver.ReadFileAsync("manifest.json"));

        Assert.Equal(kind, error.Kind);
        Assert.DoesNotContain("secret-password", error.Message);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task BudgetAndRetryAfter_PauseWithoutBusyRetrying()
    {
        using var server = new WebDavTestServer();
        server.ResponseOverride = _ => new HttpResponseMessage((HttpStatusCode)429)
        {
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2)) },
        };
        var gate = new ThrottleGate(600, TimeSpan.FromMinutes(30));
        using var driver = Driver(server, gate);

        await Assert.ThrowsAsync<SyncDriverException>(() => driver.ReadFileAsync("manifest.json"));
        Assert.True(gate.IsPaused);
        await Assert.ThrowsAsync<SyncDriverException>(() => driver.ReadFileAsync("manifest.json"));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task CallerCancellation_RemainsCancellation()
    {
        using var server = new WebDavTestServer();
        using var driver = Driver(server);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.ReadFileAsync("manifest.json", cancellation.Token));
    }

    [Theory]
    [InlineData(false, "https")]
    [InlineData(true, "https")]
    [InlineData(false, "http")]
    [InlineData(true, "http")]
    public async Task TwoDevices_ConvergeThroughWebDav_ThenPerformZeroWrites(bool weakEtags, string scheme)
    {
        using var first = new TestDatabase();
        using var second = new TestDatabase();
        using var server = new WebDavTestServer();
        if (weakEtags) server.ReturnedEtag = "W/\"weak\"";
        var host = scheme == "http" ? "nas.local:5005" : "dav.example.com";
        server.ResponseOverride = request =>
        {
            Assert.Equal(scheme, request.RequestUri?.Scheme);
            Assert.Equal(host, request.RequestUri?.Authority);
            return null;
        };
        using var firstDriver = Driver(server, host: host, scheme: scheme);
        using var secondDriver = Driver(server, host: host, scheme: scheme);
        var firstEngine = Engine(first, firstDriver);
        var secondEngine = Engine(second, secondDriver);
        var note = await first.NewNoteService().CreateAsync();
        await first.NewNoteService().UpdateAsync(note with { Title = "title", Content = "# shared" });

        Assert.True((await firstEngine.RunAsync()).Ok);
        Assert.True((await secondEngine.RunAsync()).Ok);
        Assert.Equal("# shared", Assert.Single(await second.NewNoteService().GetAllAsync()).Content);
        server.Requests.Clear();
        Assert.True((await firstEngine.RunAsync()).Ok);
        Assert.True((await secondEngine.RunAsync()).Ok);
        Assert.DoesNotContain(server.Requests, r => r.Method is "PUT" or "DELETE" or "MKCOL");

        await first.NewNoteService().DeleteAsync(note.Id);
        Assert.True((await firstEngine.RunAsync()).Ok);
        Assert.True((await secondEngine.RunAsync()).Ok);
        Assert.Empty(await second.NewNoteService().GetAllAsync());
        Assert.Contains(server.Requests, r => r.Method == "DELETE");
        server.Requests.Clear();
        Assert.True((await firstEngine.RunAsync()).Ok);
        Assert.True((await secondEngine.RunAsync()).Ok);
        Assert.DoesNotContain(server.Requests, r => r.Method is "PUT" or "DELETE" or "MKCOL");
    }

    private static SyncEngine Engine(TestDatabase db, ISyncDriver driver) => new(
        db.NewRepository(), new SyncStateStore(db.Factory), driver, db.Paths, NullLogger<SyncEngine>.Instance);

    private static WebDavDriver Driver(WebDavTestServer server, ThrottleGate? gate = null,
        string host = "dav.example.com", string folder = "sync", string scheme = "https") => new(
            $"{scheme}://{host}/{folder}/", "test-user", "secret-password",
            gate ?? new ThrottleGate(10000, TimeSpan.FromMinutes(30)), server);

    private static HttpResponseMessage Xml(string text) => new((HttpStatusCode)207)
    { Content = new StringContent(text, Encoding.UTF8, "application/xml") };
}

/// <summary>An in-memory HTTP WebDAV server, shared by two independent driver instances.</summary>
internal sealed class WebDavTestServer : HttpMessageHandler
{
    private static readonly XNamespace Dav = "DAV:";
    private int _revision;
    public HashSet<string> Folders { get; } = new(StringComparer.Ordinal) { "/" };
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Etags { get; } = new(StringComparer.Ordinal);
    public List<(string Method, string Path, string? Depth, string? Authorization, string? IfMatch)> Requests { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage?>? ResponseOverride { get; set; }
    public string? ReturnedEtag { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = request.RequestUri!.AbsolutePath;
        var method = request.Method.Method;
        var depth = request.Headers.TryGetValues("Depth", out var values) ? values.Single() : null;
        var ifMatch = request.Headers.IfMatch.FirstOrDefault()?.ToString();
        Requests.Add((method, path, depth, request.Headers.Authorization?.ToString(), ifMatch));
        if (ResponseOverride?.Invoke(request) is { } response)
            return response;
        if (method == "PROPFIND")
        {
            if (!Folders.Contains(path) && !Files.ContainsKey(path))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var members = new List<string> { path };
            if (depth == "1")
                members.AddRange(Folders.Concat(Files.Keys).Where(p => p != path && p.StartsWith(path, StringComparison.Ordinal)
                    && !p[path.Length..].TrimEnd('/').Contains('/')));
            var document = new XDocument(new XElement(Dav + "multistatus", members.Select(p =>
                new XElement(Dav + "response", new XElement(Dav + "href", p),
                    new XElement(Dav + "propstat", new XElement(Dav + "prop",
                        new XElement(Dav + "resourcetype", Folders.Contains(p) ? new XElement(Dav + "collection") : null),
                        new XElement(Dav + "getetag", Etag(p)),
                        new XElement(Dav + "getcontentlength", Files.GetValueOrDefault(p)?.Length ?? 0)),
                        new XElement(Dav + "status", "HTTP/1.1 200 OK"))))));
            return new HttpResponseMessage((HttpStatusCode)207)
            { Content = new StringContent(document.ToString(), Encoding.UTF8, "application/xml") };
        }
        if (method == "GET")
            return Files.TryGetValue(path, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        if (method == "MKCOL")
        {
            if (Folders.Contains(path)) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            var parent = new Uri(request.RequestUri!, "../").AbsolutePath;
            if (!Folders.Contains(parent)) return new HttpResponseMessage(HttpStatusCode.Conflict);
            Folders.Add(path);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
        if (method == "PUT")
        {
            if (!Folders.Contains(path[..(path.LastIndexOf('/') + 1)]))
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            if (ifMatch is not null && ifMatch != Etag(path))
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            Files[path] = await request.Content!.ReadAsByteArrayAsync(ct);
            Etags[path] = $"\"e{++_revision}\"";
            var result = new HttpResponseMessage(HttpStatusCode.Created);
            if (!string.IsNullOrEmpty(Etag(path))) result.Headers.TryAddWithoutValidation("ETag", Etag(path));
            return result;
        }
        if (method == "DELETE")
            return new HttpResponseMessage(Files.Remove(path) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
        return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
    }

    private string Etag(string path) => ReturnedEtag ?? Etags.GetValueOrDefault(path, "\"initial\"");
}
