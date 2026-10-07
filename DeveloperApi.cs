using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GetText.Plugins;

namespace GetText;

/// <summary>開発者向けの API を、設定に合わせて始める・止める (Windows 版・Mac 版で共有)。</summary>
public static class DeveloperApiControl
{
    private static DeveloperApiServer? _server;

    /// <summary>答えるための入口 (起動したときに渡す)。</summary>
    public static DeveloperApiHost? Host { get; set; }

    /// <summary>始められなかった理由 (番号が使われているなど)。</summary>
    public static string? Error { get; private set; }

    public static int? RunningPort => _server is { IsRunning: true } s ? s.Port : null;

    public static void Apply(AppSettings settings)
    {
        if (!settings.DeveloperApi || Host == null)
        {
            Stop();
            return;
        }
        if (_server is { IsRunning: true } running && running.Port == settings.DeveloperApiPort) return;
        Start(settings.DeveloperApiPort, DeveloperApiServer.LoadOrCreateToken());
    }

    /// <summary>トークンを作り直す (動いていれば新しいトークンで始め直す)。</summary>
    public static string RenewToken(AppSettings settings)
    {
        var token = DeveloperApiServer.LoadOrCreateToken(renew: true);
        if (_server is { IsRunning: true } && Host != null) Start(settings.DeveloperApiPort, token);
        return token;
    }

    private static void Start(int port, string token)
    {
        Stop();
        try
        {
            var server = new DeveloperApiServer(Host!, token);
            server.Start(port);
            _server = server;
            Error = null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            Error = $"{port} 番を使えません ({ex.Message})。番号を変えてください";
            PluginLog.Write("error", null, "開発者向けの API を始められませんでした: " + ex.Message);
        }
    }

    public static void Stop()
    {
        _server?.Stop();
        _server = null;
        Error = null;
    }
}

/// <summary>開発者向けの API で、GetText が答えるための入口 (OS ごとの部分を渡す)。</summary>
public sealed class DeveloperApiHost
{
    /// <summary>読み取りの画面が開いているか・表示している文字・訳。</summary>
    public required Func<(bool Open, string Text, string? Translation)> ReadingText { get; init; }
    public IOcrService? Ocr { get; init; }
    public ITranslationService? Translation { get; init; }
    public IDocumentService? Documents { get; init; }
}

/// <summary>
/// 開発者向けの API (既定でオフ)。127.0.0.1 だけで受け、トークン (Authorization: Bearer) が無い要求には答えない。
/// ブラウザから呼ばれないよう Origin の付いた要求は断り、Host が 127.0.0.1 / localhost 以外 (DNS の付け替え) も断る。
/// 本文は 1 MB まで・10 秒で打ち切る。ログには メソッド・パス・結果だけを書き、文字は書かない。
/// http.sys (管理者の許可) を使わないよう、TcpListener で小さな HTTP/1.1 を受ける。
/// </summary>
public sealed class DeveloperApiServer : IDisposable
{
    public const int DefaultPort = 47390;
    private const int MaxBody = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly DeveloperApiHost _host;
    private readonly string _token;
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private readonly SemaphoreSlim _slots = new(4, 4);

    public DeveloperApiServer(DeveloperApiHost host, string token)
    {
        _host = host;
        _token = token;
    }

    public int Port { get; private set; }

    public bool IsRunning => _listener != null;

    /// <summary>トークンの場所 (この利用者だけが読める場所)。</summary>
    public static string TokenPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "api-token");

    /// <summary>トークンを読む (無ければ作る)。作り直すときは renew。</summary>
    public static string LoadOrCreateToken(bool renew = false, string? path = null)
    {
        path ??= TokenPath;
        if (!renew && File.Exists(path) && File.ReadAllText(path).Trim() is { Length: 64 } existing && existing.All(Uri.IsHexDigit)) return existing;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, token);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return token;
    }

    /// <summary>始める (port が 0 なら空いている番号)。使えなければ SocketException。</summary>
    public void Start(int port)
    {
        if (_listener != null) return;
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _stop = new CancellationTokenSource();
        _ = AcceptLoopAsync(listener, _stop.Token);
        PluginLog.Write("info", null, $"開発者向けの API を始めました (127.0.0.1:{Port})");
    }

    public void Stop()
    {
        _stop?.Cancel();
        _listener?.Stop();
        if (_listener != null) PluginLog.Write("info", null, "開発者向けの API を止めました");
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            if (!await _slots.WaitAsync(0, CancellationToken.None))
            {
                client.Dispose(); // (同時に 4 つまで)
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(client, ct); }
                finally
                {
                    client.Dispose();
                    _slots.Release();
                }
            }, CancellationToken.None);
        }
    }

    internal sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stream = client.GetStream();
        Request? request;
        try
        {
            // 要求そのものは 5 秒で受け取り終える (ゆっくり送り続けて受け口を占めるのを防ぐ)
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            reading.CancelAfter(TimeSpan.FromSeconds(5));
            request = await ReadRequestAsync(stream, reading.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
        {
            return;
        }
        if (request == null) return;
        var (status, body) = await RouteAsync(request, timeout.Token);
        PluginLog.Write(status < 400 ? "info" : "warn", null, $"API {request.Method} {request.Path.Split('?')[0]} → {status}");
        await WriteResponseAsync(stream, status, body, ct);
    }

    /// <summary>要求を確かめて答える (テストでも使う)。</summary>
    internal async Task<(int Status, JsonNode Body)> RouteAsync(Request request, CancellationToken ct)
    {
        // ブラウザ (よそのページ) からは呼ばせない
        if (request.Headers.ContainsKey("origin")) return Error(403, "ブラウザからの要求には答えません");
        if (!request.Headers.TryGetValue("host", out var host) || !IsLoopbackHost(host)) return Error(403, "Host が違います");
        if (!request.Headers.TryGetValue("authorization", out var auth) || !auth.StartsWith("Bearer ", StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(auth[7..].Trim()), Encoding.ASCII.GetBytes(_token)))
            return Error(401, "トークンが違います (設定 → 診断 の「開発者向けの API」でコピーできます)");

        try
        {
            return (request.Method, request.Path.Split('?')[0]) switch
            {
                ("GET", "/v1/status") => (200, Status()),
                ("GET", "/v1/text") => (200, ReadingText()),
                ("GET", "/v1/plugins") => (200, Plugins()),
                ("POST", "/v1/ocr") => (200, await OcrAsync(request, ct)),
                ("POST", "/v1/translate") => (200, await TranslateAsync(request, ct)),
                (_, "/v1/status" or "/v1/text" or "/v1/plugins" or "/v1/ocr" or "/v1/translate") => Error(405, "このメソッドは使えません"),
                _ => Error(404, "ありません (/v1/status・/v1/text・/v1/plugins・/v1/ocr・/v1/translate)"),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or IOException or UnauthorizedAccessException or FormatException)
        {
            return Error(400, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Error(503, "時間切れになりました (10 秒)");
        }
        catch (Exception ex)
        {
            // 要求の境目: 思わぬ問題でも接続を黙って切らず、500 で答えて種類をログに書く (中身・文字は書かない)
            PluginLog.Write("error", null, "API で思わぬ問題: " + ex.GetType().Name);
            return Error(500, "GetText の中で問題が起きました");
        }
    }

    private static (int, JsonNode) Error(int status, string message) => (status, new JsonObject { ["error"] = message });

    internal static bool IsLoopbackHost(string host)
    {
        var name = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        return name is "127.0.0.1" or "localhost" or "[::1]";
    }

    private JsonNode Status()
    {
        var (open, _, _) = _host.ReadingText();
        return new JsonObject
        {
            ["app"] = "GetText",
            ["version"] = PluginCompatibility.HostVersion.ToString(),
            ["pluginApi"] = PluginApi.Version,
            ["platform"] = PluginCompatibility.CurrentPlatform,
            ["readingOpen"] = open,
            ["ocr"] = _host.Ocr != null,
            ["translation"] = _host.Translation?.IsAvailable == true,
        };
    }

    private JsonNode ReadingText()
    {
        var (open, text, translation) = _host.ReadingText();
        return new JsonObject { ["open"] = open, ["text"] = text, ["translation"] = translation };
    }

    private static JsonNode Plugins() => new JsonObject
    {
        ["plugins"] = new JsonArray(PluginRuntime.Plugins.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Record.Id,
            ["version"] = p.Record.Version,
            ["enabled"] = p.Record.Enabled,
            ["loaded"] = p.Loaded,
        }).ToArray()),
    };

    private async Task<JsonNode> OcrAsync(Request request, CancellationToken ct)
    {
        if (_host.Ocr == null || _host.Documents == null) throw new InvalidOperationException("読み取りの準備ができていません");
        var body = JsonNode.Parse(request.Body) ?? throw new FormatException("本文は JSON にしてください");
        var path = body["path"]?.GetValue<string>() ?? throw new FormatException("path (画像のファイル) を指定してください");
        var image = await _host.Documents.LoadImageAsync(path, 4000, ct);
        var result = await _host.Ocr.RecognizeAsync(image.Bgra, image.Width, image.Height, body["provider"]?.GetValue<string>(), ct);
        return new JsonObject
        {
            ["provider"] = result.ProviderId,
            ["width"] = image.Width,
            ["height"] = image.Height,
            ["text"] = result.Text,
            ["lines"] = new JsonArray(result.Lines.Select(l => (JsonNode)new JsonObject
            {
                ["text"] = l.Text,
                ["box"] = new JsonArray(Math.Round(l.Left, 1), Math.Round(l.Top, 1), Math.Round(l.Right, 1), Math.Round(l.Bottom, 1)),
            }).ToArray()),
        };
    }

    private async Task<JsonNode> TranslateAsync(Request request, CancellationToken ct)
    {
        if (_host.Translation == null) throw new InvalidOperationException("翻訳の準備ができていません");
        var body = JsonNode.Parse(request.Body) ?? throw new FormatException("本文は JSON にしてください");
        var texts = body["texts"]?.AsArray().Select(t => t?.GetValue<string>() ?? "").ToList() ?? throw new FormatException("texts (文の配列) を指定してください");
        if (texts.Count > 200) throw new FormatException("一度に訳せるのは 200 文までです");
        var translated = await _host.Translation.TranslateToJapaneseAsync(texts, ct);
        return new JsonObject { ["translations"] = new JsonArray(translated.Select(t => (JsonNode)t).ToArray()) };
    }

    // ───────── HTTP/1.1 (小さく) ─────────

    internal static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var head = new MemoryStream();
        var one = new byte[1];
        // ヘッダーの終わり (\r\n\r\n) まで。16 KB を超えたら断る
        while (true)
        {
            int n = await stream.ReadAsync(one, ct);
            if (n == 0) return null;
            head.WriteByte(one[0]);
            if (head.Length > 16 * 1024) throw new InvalidDataException("ヘッダーが長すぎます");
            var b = head.GetBuffer();
            long len = head.Length;
            if (len >= 4 && b[len - 4] == '\r' && b[len - 3] == '\n' && b[len - 2] == '\r' && b[len - 1] == '\n') break;
        }
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) throw new InvalidDataException("要求の行がありません");
        var first = lines[0].Split(' ');
        if (first.Length < 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new InvalidDataException("HTTP ではありません");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        int length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var v) ? v : 0;
        if (length < 0 || length > MaxBody) throw new InvalidDataException("本文が大きすぎます");
        var body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = await stream.ReadAsync(body.AsMemory(read), ct);
            if (n == 0) throw new InvalidDataException("本文が途中で切れました");
            read += n;
        }
        return new Request(first[0].ToUpperInvariant(), first[1], headers, body);
    }

    private static async Task WriteResponseAsync(Stream stream, int status, JsonNode body, CancellationToken ct)
    {
        var json = Encoding.UTF8.GetBytes(body.ToJsonString(Json));
        var reason = status switch
        {
            200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
            503 => "Service Unavailable", _ => "Internal Server Error",
        };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {json.Length}\r\n" +
                   "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.WriteAsync(json, ct);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // (相手が先に閉じた)
        }
    }
}
