using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QNote.Sync;

namespace QNote.Tests;

public sealed class OneDriveDriverTests
{
    [Theory]
    [InlineData("notes", HttpStatusCode.Created)]
    [InlineData("categories", HttpStatusCode.Created)]
    [InlineData("tombstones", HttpStatusCode.Conflict)]
    public async Task WriteFile_CreatesTheFolderAsJson_ThenUploadsBinaryContent(
        string folder, HttpStatusCode folderStatus)
    {
        var requests = 0;
        byte[] content = [0, 1, 128, 255];
        using var handler = new Handler(async (request, ct) =>
        {
            requests++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal(1, requests);
                Assert.Equal(OneDriveDriver.DefaultBaseUrl + "?$select=id", request.RequestUri!.AbsoluteUri);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"app-root-id"}""", Encoding.UTF8, "application/json"),
                };
            }
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(2, requests);
                Assert.Equal("https://graph.microsoft.com/v1.0/me/drive/items/app-root-id/children", request.RequestUri!.AbsoluteUri);
                Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(ct));
                Assert.Equal(folder, body.RootElement.GetProperty("name").GetString());
                Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("folder").ValueKind);
                return new HttpResponseMessage(folderStatus);
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.EndsWith($":/{folder}/sample.qnote:/content", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/octet-stream", request.Content?.Headers.ContentType?.MediaType);
            Assert.Equal(content, await request.Content!.ReadAsByteArrayAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"id":"remote-file","eTag":"etag"}""", Encoding.UTF8, "application/json"),
            };
        });
        using var driver = new OneDriveDriver(new Tokens(), NullLogger<OneDriveDriver>.Instance, handler: handler);

        var result = await driver.WriteFileAsync($"{folder}/sample.qnote", content);

        Assert.Equal("remote-file", result.RemoteId);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task NestedFolderCreation_UsesTheActualParentItemId()
    {
        const string items = "https://graph.microsoft.com/v1.0/me/drive/items/";
        var expected = new Queue<(HttpMethod Method, string Url, string Body)>([
            (HttpMethod.Get, OneDriveDriver.DefaultBaseUrl + "?$select=id", """{"id":"app-root-id"}"""),
            (HttpMethod.Post, items + "app-root-id/children", """{"id":"notes-id"}"""),
            (HttpMethod.Get, items + "app-root-id:/notes", """{"id":"notes-id"}"""),
            (HttpMethod.Post, items + "notes-id/children", """{"id":"nested-id"}"""),
            (HttpMethod.Put, OneDriveDriver.DefaultBaseUrl + ":/notes/nested/sample.qnote:/content", """{"id":"file-id"}"""),
        ]);
        using var handler = new Handler((request, _) =>
        {
            var next = expected.Dequeue();
            Assert.Equal(next.Method, request.Method);
            Assert.Equal(next.Url, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(next.Body, Encoding.UTF8, "application/json"),
            });
        });
        using var driver = new OneDriveDriver(new Tokens(), NullLogger<OneDriveDriver>.Instance, handler: handler);

        var result = await driver.WriteFileAsync("notes/nested/sample.qnote", [1, 2, 3]);

        Assert.Equal("file-id", result.RemoteId);
        Assert.Empty(expected);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            send(request, ct);
    }

    private sealed class Tokens : ITokenProvider
    {
        public Task<AuthResult> AcquireSilentAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult("synthetic-bearer", DateTimeOffset.MaxValue, null));

        public Task<AccountInfo?> GetSignedInAccountAsync(CancellationToken ct = default) =>
            Task.FromResult<AccountInfo?>(null);

        public Task<AuthResult> AcquireInteractiveAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task SignOutAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
