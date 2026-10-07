using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using GetText.Plugins;

namespace GetText;

/// <summary>Mac の文字認識 (Vision。補助プログラム) の結果を読む。</summary>
public static class VisionLines
{
    public static readonly string[] Languages = ["ja-JP", "en-US", "zh-Hans", "ko-KR"];

    /// <summary>補助プログラムの "lines" を行にする。単語 (日本語は 1 文字) の位置は、数が合うときだけ使う。</summary>
    public static List<OcrLineData> Parse(JsonArray lines) => lines.Select(l =>
    {
        var text = l!["text"]!.GetValue<string>();
        var words = (l["words"] as JsonArray)?.Select(w => w!.AsArray()).Select(a => new OcrSpan(a[0]!.GetValue<string>(),
            a[1]!.GetValue<double>(), a[2]!.GetValue<double>(), a[3]!.GetValue<double>(), a[4]!.GetValue<double>())).ToList();
        return new OcrLineData([text], l["left"]!.GetValue<double>(), l["top"]!.GetValue<double>(),
            l["right"]!.GetValue<double>(), l["bottom"]!.GetValue<double>(), OcrSpans.IfMatching(words, text), l["confidence"]?.GetValue<double>());
    }).ToList();
}

/// <summary>Mac の文字認識 (Vision) を、読み取りの方式の 1 つ (IOcrProvider) として使う (拡張機能・Quick OCR 用)。</summary>
public sealed class VisionOcrProvider : IOcrProvider
{
    public string Id => "vision";
    public LocalizedText Name => "Mac の文字認識 (Vision)";
    public bool IsLocal => true;
    public bool IsAvailable => MacHelper.IsAvailable;

    public async Task<OcrResult> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken)
    {
        JsonObject reply;
        try
        {
            reply = await MacHelper.Instance.RequestAsync("ocr_image", new JsonObject
            {
                ["data"] = Convert.ToBase64String(bgra, 0, width * height * 4),
                ["w"] = width,
                ["h"] = height,
                ["languages"] = new JsonArray(VisionLines.Languages.Select(l => (JsonNode)l).ToArray()),
            }, TimeSpan.FromSeconds(30));
        }
        catch (MacHelperException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return OcrResults.From(VisionLines.Parse(reply["lines"]?.AsArray() ?? []), Id);
    }
}

/// <summary>
/// 画像のファイル・PDF のページを画像にする (Mac)。補助プログラムの ImageIO (写真の向きも合わせる) と PDFKit を使う。外部の部品・通信なし。
/// </summary>
public sealed class MacDocumentService : IDocumentService
{
    public IReadOnlyList<string> ImageExtensions { get; } = ["png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff", "heic", "heif", "webp"];

    public Task<CapturedImage> LoadImageAsync(string path, int maxSize, CancellationToken cancellationToken) =>
        Image("image_load", new JsonObject { ["path"] = Path.GetFullPath(path), ["max"] = maxSize }, cancellationToken);

    public async Task<int> GetPdfPageCountAsync(string path, CancellationToken cancellationToken) =>
        (await Request("pdf_info", new JsonObject { ["path"] = Path.GetFullPath(path) }))["pages"]!.GetValue<int>();

    public Task<CapturedImage> RenderPdfPageAsync(string path, int pageIndex, double dpi, int maxSize, CancellationToken cancellationToken) =>
        Image("pdf_render", new JsonObject { ["path"] = Path.GetFullPath(path), ["page"] = pageIndex, ["dpi"] = dpi, ["max"] = maxSize }, cancellationToken);

    private static async Task<CapturedImage> Image(string cmd, JsonObject args, CancellationToken cancellationToken)
    {
        var reply = await Request(cmd, args);
        cancellationToken.ThrowIfCancellationRequested();
        int width = reply["w"]!.GetValue<int>(), height = reply["h"]!.GetValue<int>();
        var bgra = Convert.FromBase64String(reply["data"]!.GetValue<string>());
        if (bgra.Length != width * height * 4) throw new InvalidOperationException("画像の大きさが合いません");
        return new CapturedImage(bgra, width, height, DateTimeOffset.Now);
    }

    private static async Task<JsonObject> Request(string cmd, JsonObject args)
    {
        if (!MacHelper.IsAvailable) throw new InvalidOperationException("補助プログラムがありません");
        try
        {
            return await MacHelper.Instance.RequestAsync(cmd, args, TimeSpan.FromSeconds(60));
        }
        catch (MacHelperException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }
}

/// <summary>
/// Quick OCR (Mac): 画面の範囲を選び (macOS の範囲の選択 screencapture -i)、その範囲の文字を 1 回だけ読んでクリップボードに入れる。
/// どのアプリを使っていても ⌃⌥Q か、コマンドの一覧から使える。読み取りはこの Mac の中で行い、撮った画像はすぐ消す。
/// </summary>
public static class MacQuickOcr
{
    private static bool _running;

    public static async void Run()
    {
        if (_running || App.DemoMode) return;
        _running = true;
        try
        {
            if (await SelectAndCaptureAsync() is not { } shot) return;
            var ocr = PluginRuntime.GetService<IOcrService>() ?? throw new InvalidOperationException("読み取りの準備ができていません");
            var result = await ocr.RecognizeAsync(shot.Bgra, shot.Width, shot.Height, null, CancellationToken.None);
            var text = result.Text.Trim();
            if (text.Length == 0)
            {
                PluginToast.Show("Quick OCR", new PluginNotification("文字が見つかりませんでした", "範囲を広げるか、文字の上を選んでください。", PluginNotificationKind.Warning));
                return;
            }
            bool copied = PluginRuntime.Ui is { } ui && await ui.CopyTextAsync(text);
            PluginToast.Show("Quick OCR", copied
                ? new PluginNotification($"{result.Lines.Count} 行をコピーしました", text.Length > 80 ? text[..80] + "…" : text, PluginNotificationKind.Success)
                : new PluginNotification("コピーできませんでした", "クリップボードを使えませんでした。", PluginNotificationKind.Warning));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            App.Log("QuickOcr", ex);
            PluginToast.Show("Quick OCR", new PluginNotification("読み取れませんでした", ex.Message, PluginNotificationKind.Error));
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// macOS の範囲の選択 (screencapture -i) で範囲を選んでもらい、その範囲の画像を返す (Esc でやめたら null)。
    /// 撮った画像のファイルは読んだらすぐ消す。拡張機能の SelectScreenRegionAsync もこれを使う。
    /// </summary>
    public static async Task<CapturedImage?> SelectAndCaptureAsync()
    {
        var file = Path.Combine(Path.GetTempPath(), $"gettext-region-{Guid.NewGuid():N}.png");
        try
        {
            var info = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false };
            info.ArgumentList.Add("-i"); // 範囲を選ぶ (Esc でやめる)
            info.ArgumentList.Add("-x"); // 音を鳴らさない
            info.ArgumentList.Add(file);
            using (var process = Process.Start(info) ?? throw new InvalidOperationException("範囲の選択を始められませんでした"))
                await process.WaitForExitAsync();
            if (!File.Exists(file)) return null; // (やめた)
            return await new MacDocumentService().LoadImageAsync(file, 8000, CancellationToken.None);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}