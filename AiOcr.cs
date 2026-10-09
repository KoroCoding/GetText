using System.IO;
using System.Text.Json.Nodes;

namespace GetText;

/// <summary>
/// 機械学習の OCR (offline\ocr_server.py、RapidOCR + PP-OCRv6)。
/// 日本語・英語・中国語などの文字と記号を Windows OCR より大幅に正確に読める。
/// </summary>
public sealed class AiOcr : IDisposable
{
    private readonly PythonWorker _worker = new("ocr_server.py", TimeSpan.FromSeconds(60));

    public static bool IsInstalled =>
        File.Exists(PythonWorker.PythonPath)
        && File.Exists(Path.Combine(PythonWorker.SitePackages, "rapidocr", "__init__.py"));

    /// <summary>"gpu" または "cpu" (起動前は null)。</summary>
    public string? Device => _worker.ReadyInfo?["device"]?.GetValue<string>();

    private long _lastUsedTicks = DateTime.UtcNow.Ticks;
    private int _inUse;

    /// <summary>
    /// 最後に使ってからの時間 (読み取りの途中なら 0)。読み取りの画面・Quick OCR・拡張機能・開発者向けの API のどれから使っても数える
    /// (使っている間に、読み取りの画面の「しばらく使っていない」で止めないように)。
    /// </summary>
    public TimeSpan IdleFor => Volatile.Read(ref _inUse) > 0 ? TimeSpan.Zero
        : DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);

    private void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);

    public Task EnsureStartedAsync()
    {
        Touch();
        if (!IsInstalled)
            return Task.FromException(new InvalidOperationException(
                "AI OCR が未セットアップです (設定の「セットアップを実行」で入れてください)"));
        return _worker.EnsureStartedAsync();
    }

    /// <summary>高精度の認識モデル (小さい・ぼやけた文字の読み直し用) が使えるか (起動後にわかる)。</summary>
    public bool AccurateAvailable => _worker.ReadyInfo?["accurate"]?.GetValue<bool>() == true;

    /// <summary>直前の読み取りの確からしさの平均 (0〜1。文字数で重み付け)。行が無ければ 1。</summary>
    public double LastConfidence { get; private set; } = 1;

    /// <summary>
    /// BGRA 画像を認識し、行ごとの結果を返す (座標は入力画像のピクセル)。
    /// accurate なら高精度の認識モデルで読む (遅い。画面が止まったときの読み直し用)。
    /// </summary>
    public async Task<List<OcrLineData>> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken ct, bool accurate = false)
    {
        Interlocked.Increment(ref _inUse);
        try
        {
            return await RecognizeCoreAsync(bgra, width, height, ct, accurate);
        }
        finally
        {
            Touch();
            Interlocked.Decrement(ref _inUse);
        }
    }

    private async Task<List<OcrLineData>> RecognizeCoreAsync(byte[] bgra, int width, int height, CancellationToken ct, bool accurate)
    {
        await EnsureStartedAsync();
        // chars: 1 文字ずつの位置も返してもらう (検索の印を一致した文字の上に付けるため)
        var request = new JsonObject { ["w"] = width, ["h"] = height, ["chars"] = true };
        if (accurate) request["accurate"] = true;
        var response = await _worker.RequestAsync(request, bgra, ct);

        var lines = new List<OcrLineData>();
        double weighted = 0, chars = 0;
        foreach (var item in response["lines"]!.AsArray())
        {
            var text = item!["text"]!.GetValue<string>();
            double score = item["score"]?.GetValue<double>() ?? 1;
            weighted += score * text.Length;
            chars += text.Length;
            var box = item["box"]!.AsArray().Select(p => (X: p![0]!.GetValue<double>(), Y: p[1]!.GetValue<double>())).ToList();
            List<OcrSpan>? spans = null;
            if (item["chars"] is JsonArray charBoxes)
            {
                spans = [];
                foreach (var c in charBoxes)
                {
                    var a = c!.AsArray();
                    spans.Add(new OcrSpan(a[0]!.GetValue<string>(), a[2]!.GetValue<double>(), a[3]!.GetValue<double>(),
                        a[4]!.GetValue<double>(), a[5]!.GetValue<double>(), a[1]!.GetValue<double>()));
                }
            }
            // (文字の位置が行の文字とそろわなければ使わない: OcrSpans.IfMatching)
            lines.Add(new OcrLineData([text], box.Min(p => p.X), box.Min(p => p.Y), box.Max(p => p.X), box.Max(p => p.Y),
                OcrSpans.IfMatching(spans, text), score));
        }
        LastConfidence = chars > 0 ? weighted / chars : 1;
        return MergeRows(lines);
    }

    /// <summary>
    /// 表のセルのように同じ高さに分かれて検出された断片を 1 行にまとめる (例: 「関東」+「：直流」)。
    /// 段組みの文章を混ぜないよう、少なくとも片方が短い断片のときだけまとめる。
    /// </summary>
    internal static List<OcrLineData> MergeRows(List<OcrLineData> lines)
    {
        var rows = new List<OcrLineData>();
        foreach (var line in lines)
        {
            // (間にほかの行がある断片はまとめない: 高さのそろった 1 つ飛ばしの枠 (A の枠と C の枠) の文字を 1 行にしない)
            int i = rows.FindLastIndex(r => SameRow(r, line) && !lines.Any(x => Between(x, r, line)));
            if (i < 0)
            {
                rows.Add(line);
                continue;
            }
            var (a, b) = rows[i].Left <= line.Left ? (rows[i], line) : (line, rows[i]);
            double height = Math.Max(a.Bottom - a.Top, b.Bottom - b.Top);
            string left = string.Concat(a.Words), right = string.Concat(b.Words);
            // 間が大きく空いていれば空白で区切る
            string text = b.Left - a.Right > height * 1.5 ? left + " " + right : TextScript.Join(left, right);
            // 文字の位置はつなげる (左の断片 → 右の断片。間の空白は数えないので、そのままそろう)
            var spans = a.Spans != null && b.Spans != null ? a.Spans.Concat(b.Spans).ToArray() : null;
            double? confidence = a.Confidence is { } ca && b.Confidence is { } cb ? Math.Min(ca, cb) : null;
            rows[i] = new OcrLineData([text], a.Left, Math.Min(a.Top, b.Top), b.Right, Math.Max(a.Bottom, b.Bottom),
                OcrSpans.IfMatching(spans, text), confidence);
        }
        return rows;
    }

    private static bool SameRow(OcrLineData a, OcrLineData b)
    {
        double ha = a.Bottom - a.Top, hb = b.Bottom - b.Top;
        double overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (overlap < Math.Min(ha, hb) * 0.6) return false;
        bool separated = b.Left >= a.Right - 2 || b.Right <= a.Left + 2;
        bool shortPiece = a.Right - a.Left < ha * 6 || b.Right - b.Left < hb * 6;
        return separated && shortPiece;
    }

    // x が a と b の間 (左右の間の空き) にあり、高さが a か b と重なるか
    private static bool Between(OcrLineData x, OcrLineData a, OcrLineData b)
    {
        if (ReferenceEquals(x, a) || ReferenceEquals(x, b)) return false;
        var (l, r) = a.Left <= b.Left ? (a, b) : (b, a);
        if (x.Left < l.Right - 2 || x.Right > r.Left + 2) return false;
        double top = Math.Min(a.Top, b.Top), bottom = Math.Max(a.Bottom, b.Bottom);
        return Math.Min(bottom, x.Bottom) - Math.Max(top, x.Top) > 0;
    }

    public void Dispose() => _worker.Dispose();

    /// <summary>補助プロセスを止めてメモリ (GPU のメモリも) を空ける。次に読み取るときに起動し直す。</summary>
    public void Release() => _worker.Release();

    /// <summary>補助プロセスを起動している・起動の途中か。</summary>
    public bool IsStarted => _worker.IsStarted;
}
