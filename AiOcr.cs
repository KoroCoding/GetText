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

    public Task EnsureStartedAsync()
    {
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
        await EnsureStartedAsync();
        var request = new JsonObject { ["w"] = width, ["h"] = height };
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
            lines.Add(new OcrLineData([text], box.Min(p => p.X), box.Min(p => p.Y), box.Max(p => p.X), box.Max(p => p.Y)));
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
            int i = rows.FindLastIndex(r => SameRow(r, line));
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
            rows[i] = new OcrLineData([text], a.Left, Math.Min(a.Top, b.Top), b.Right, Math.Max(a.Bottom, b.Bottom));
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

    public void Dispose() => _worker.Dispose();

    /// <summary>補助プロセスを止めてメモリ (GPU のメモリも) を空ける。次に読み取るときに起動し直す。</summary>
    public void Release() => _worker.Release();

    /// <summary>補助プロセスを起動している・起動の途中か。</summary>
    public bool IsStarted => _worker.IsStarted;
}
