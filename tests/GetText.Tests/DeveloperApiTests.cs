using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using GetText.Plugins;

namespace GetText.Tests;

/// <summary>開発者向けの API: 127.0.0.1 だけ・トークン必須・ブラウザと別の Host を断る・各入口。ログに文字を書かない。</summary>
[Collection("PluginRuntime")]
public class DeveloperApiTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gettext-api-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousLog = PluginLog.Path;
    private readonly DeveloperApiServer _server;
    private readonly HttpClient _http = new();

    private sealed class FakeOcr : IOcrService
    {
        public IReadOnlyList<IOcrProvider> Providers => [];

        public Task<OcrResult> RecognizeAsync(byte[] bgra, int width, int height, string? providerId, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult([new OcrResultLine($"{width}x{height}", 1, 2, 3, 4)], providerId ?? "fake"));
    }

    private sealed class FakeDocuments : IDocumentService
    {
        public IReadOnlyList<string> ImageExtensions => ["png"];
        public Task<CapturedImage> LoadImageAsync(string path, int maxSize, CancellationToken ct) =>
            File.Exists(path) ? Task.FromResult(new CapturedImage(new byte[8 * 4 * 4], 8, 4, DateTimeOffset.Now)) : throw new IOException("ファイルがありません");
        public Task<int> GetPdfPageCountAsync(string path, CancellationToken ct) => Task.FromResult(0);
        public Task<CapturedImage> RenderPdfPageAsync(string path, int pageIndex, double dpi, int maxSize, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeTranslation : ITranslationService
    {
        public bool IsAvailable => true;
        public Task<IReadOnlyList<string>> TranslateToJapaneseAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(texts.Select(t => "訳:" + t).ToList());
    }

    public DeveloperApiTests()
    {
        Directory.CreateDirectory(_dir);
        PluginLog.Path = Path.Combine(_dir, "plugins.log");
        _server = new DeveloperApiServer(new DeveloperApiHost
        {
            ReadingText = () => (true, "秘密の読み取りの文", "秘密の訳"),
            Ocr = new FakeOcr(),
            Documents = new FakeDocuments(),
            Translation = new FakeTranslation(),
        }, Token);
        _server.Start(0);
    }

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
        PluginLog.Path = _previousLog;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? token = Token, string? json = null)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{_server.Port}{path}");
        if (token != null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Send(HttpRequestMessage request)
    {
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    [Fact]
    public async Task RequiresTheToken()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(Request(HttpMethod.Get, "/v1/status", token: null))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(Request(HttpMethod.Get, "/v1/status", token: new string('f', 64)))).Status);
        var (status, body) = await Send(Request(HttpMethod.Get, "/v1/status"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("GetText", body!["app"]!.GetValue<string>());
        Assert.True(body["readingOpen"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RejectsBrowsersAndOtherHosts()
    {
        var fromPage = Request(HttpMethod.Get, "/v1/text");
        fromPage.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(fromPage)).Status);

        var rebinding = Request(HttpMethod.Get, "/v1/text");
        rebinding.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(rebinding)).Status);

        Assert.True(DeveloperApiServer.IsLoopbackHost("localhost:47390"));
        Assert.True(DeveloperApiServer.IsLoopbackHost("[::1]:47390"));
        Assert.False(DeveloperApiServer.IsLoopbackHost("127.0.0.1.evil.example"));
    }

    [Fact]
    public async Task ReturnsReadingTextAndTranslates()
    {
        var (_, text) = await Send(Request(HttpMethod.Get, "/v1/text"));
        Assert.Equal("秘密の読み取りの文", text!["text"]!.GetValue<string>());
        Assert.Equal("秘密の訳", text["translation"]!.GetValue<string>());

        var (status, translated) = await Send(Request(HttpMethod.Post, "/v1/translate", json: """{"texts":["Hello","World"]}"""));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["訳:Hello", "訳:World"], translated!["translations"]!.AsArray().Select(t => t!.GetValue<string>()));
    }

    [Fact]
    public async Task ReadsAnImageFile()
    {
        var file = Path.Combine(_dir, "a.png");
        File.WriteAllBytes(file, [1]);
        var (status, body) = await Send(Request(HttpMethod.Post, "/v1/ocr", json: new JsonObject { ["path"] = file }.ToJsonString()));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("8x4", body!["text"]!.GetValue<string>());
        Assert.Equal(4, body["lines"]![0]!["box"]!.AsArray().Count);

        var (missing, error) = await Send(Request(HttpMethod.Post, "/v1/ocr", json: new JsonObject { ["path"] = Path.Combine(_dir, "none.png") }.ToJsonString()));
        Assert.Equal(HttpStatusCode.BadRequest, missing);
        Assert.Contains("ファイルがありません", error!["error"]!.GetValue<string>());

        // ネットワークの場所は読ませない (Windows がその相手に自動でサインインを試みるため)。画像でないファイルも
        var (unc, uncError) = await Send(Request(HttpMethod.Post, "/v1/ocr", json: new JsonObject { ["path"] = @"\\server\share\a.png" }.ToJsonString()));
        Assert.Equal(HttpStatusCode.BadRequest, unc);
        Assert.Contains("この PC の中の画像", uncError!["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(@"C:\pictures\scan.png", true)]
    [InlineData(@"D:\a\b.JPG", true)]
    [InlineData(@"\\server\share\scan.png", false)]   // ネットワークの場所
    [InlineData(@"\\?\C:\scan.png", false)]           // デバイスの名前
    [InlineData(@"\\.\pipe\scan.png", false)]
    [InlineData("//server/share/scan.png", false)]
    [InlineData("scan.png", false)]                        // フォルダから書いていない
    [InlineData(@"C:\notes\secret.txt", false)]         // 画像でない
    [InlineData("", false)]
    public void OnlyLocalImageFilesCanBeRead(string path, bool allowed) => Assert.Equal(allowed, DeveloperApiServer.IsAllowedImagePath(path));

    [Fact]
    public async Task UnknownPathsAndMethods()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Send(Request(HttpMethod.Get, "/v1/secret"))).Status);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Send(Request(HttpMethod.Get, "/v1/ocr"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Request(HttpMethod.Post, "/v1/translate", json: "{ broken"))).Status);
    }

    [Fact]
    public async Task LogHasNoTextOrToken()
    {
        await Send(Request(HttpMethod.Get, "/v1/text"));
        await Send(Request(HttpMethod.Post, "/v1/translate", json: """{"texts":["Confidential"]}"""));
        var log = File.ReadAllText(PluginLog.Path);
        Assert.Contains("/v1/text", log);
        Assert.DoesNotContain("秘密", log);
        Assert.DoesNotContain("Confidential", log);
        Assert.DoesNotContain(Token, log);
    }

    [Fact]
    public void TokenIsCreatedOnceAndCanBeRenewed()
    {
        var path = Path.Combine(_dir, "api-token");
        var first = DeveloperApiServer.LoadOrCreateToken(path: path);
        Assert.Equal(64, first.Length);
        Assert.Equal(first, DeveloperApiServer.LoadOrCreateToken(path: path));
        Assert.NotEqual(first, DeveloperApiServer.LoadOrCreateToken(renew: true, path: path));
    }

    [Fact]
    public void ListensOnlyOnLoopback()
    {
        Assert.True(_server.IsRunning);
        using var other = new System.Net.Sockets.TcpClient();
        other.Connect(IPAddress.Loopback, _server.Port); // 127.0.0.1 では受ける
        Assert.True(other.Connected);
    }
}
