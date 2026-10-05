using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace GetText;

public sealed class OcrTuning
{
    /// <summary>グレースケール化・暗い背景の反転を行う。</summary>
    public bool Enhance { get; set; } = true;
    public bool AutoInvert { get; set; } = true;
    /// <summary>明るさの幅を 0〜255 に広げる。</summary>
    public bool Stretch { get; set; } = true;
    /// <summary>画像の周囲に足す余白(px)。</summary>
    public int Padding { get; set; } = 16;
    /// <summary>自動拡大で目標にする 1 行の高さ(px)。</summary>
    public double TargetLineHeight { get; set; } = 40;
    public bool Bicubic { get; set; } = true;
    public double Sharpen { get; set; } = 0;
    public bool Binarize { get; set; } = false;
    public double MaxScale { get; set; } = 4;
    /// <summary>基準倍率に対するこれらの倍率でも読み、行ごとに多数決する。空なら 1 回だけ。</summary>
    public double[] EnsembleFactors { get; set; } = [];
}

public sealed record OcrOutput(IReadOnlyList<OcrLineData> Lines, double Scale, bool Inverted, int Passes, string Language);

/// <summary>
/// 画面キャプチャ(BGRA)を OCR しやすい画像に整えてから Windows OCR にかける。
/// 自動モードでは 1 回目の結果から行の高さを測って最適な倍率で読み直し、
/// 必要なら複数倍率の結果から行ごとに多数決をとる。
/// </summary>
public sealed class OcrPipeline
{
    public OcrTuning Tuning { get; } = new();

    private double _autoScale = 2.0;

    public void ResetAdaptive() => _autoScale = 2.0;

    public async Task<OcrOutput> RecognizeAsync(OcrEngine engine, OcrEngine? latinEngine,
        byte[] bgra, int width, int height, double fixedScale)
    {
        var t = Tuning;
        var gray = ToGray(bgra, width, height);
        bool inverted = false;
        if (t.Enhance)
        {
            if (t.AutoInvert && EstimateBackground(gray) < 128)
            {
                Invert(gray);
                inverted = true;
            }
            if (t.Stretch) StretchContrast(gray);
        }
        byte background = EstimateBackground(gray);
        var source = new Source(gray, width, height, background);

        double maxScale = Math.Max(0.25, Math.Min(t.MaxScale,
            (OcrEngine.MaxImageDimension - 2 * t.Padding) / (double)Math.Max(width, height)));
        double scale = Math.Clamp(fixedScale > 0 ? fixedScale : _autoScale, 0.25, maxScale);

        var lines = await RunAsync(engine, source, scale);
        int passes = 1;

        if (fixedScale <= 0)
        {
            double lineHeight = MedianLineHeight(lines) * scale;
            if (lineHeight > 0)
            {
                double ideal = Math.Clamp(scale * t.TargetLineHeight / lineHeight, 0.5, maxScale);
                _autoScale = ideal;
                // 最適倍率から大きく外れていたらその場で読み直す(小さな差なら次フレームから反映)
                if (Math.Abs(ideal / scale - 1) > 0.3)
                {
                    scale = ideal;
                    lines = await RunAsync(engine, source, scale);
                    passes++;
                }
            }
        }

        // 自動言語: 日本語エンジンの結果がほぼ欧文なら欧文エンジンで読み直す
        if (latinEngine != null && TextScript.IsMostlyLatin(string.Join(" ", lines.Select(l => string.Join(" ", l.Words)))))
        {
            engine = latinEngine;
            lines = await RunAsync(engine, source, scale);
            passes++;
        }

        if (t.EnsembleFactors.Length > 0 && lines.Count > 0)
        {
            var candidates = new List<List<OcrLineData>> { lines };
            foreach (var factor in t.EnsembleFactors)
            {
                double s = Math.Clamp(scale * factor, 0.5, maxScale);
                if (Math.Abs(s - scale) < 0.05) continue;
                candidates.Add(await RunAsync(engine, source, s));
                passes++;
            }
            if (candidates.Count >= 3) lines = Vote(candidates);
        }

        return new OcrOutput(lines, scale, inverted, passes, engine.RecognizerLanguage.LanguageTag);
    }

    private sealed record Source(byte[] Gray, int Width, int Height, byte Background);

    private async Task<List<OcrLineData>> RunAsync(OcrEngine engine, Source src, double scale)
    {
        int pad = Tuning.Padding;
        int dw = Math.Max(1, (int)Math.Round(src.Width * scale));
        int dh = Math.Max(1, (int)Math.Round(src.Height * scale));
        var img = Resize(src.Gray, src.Width, src.Height, dw, dh, pad, src.Background, Tuning.Bicubic);
        int ow = dw + pad * 2, oh = dh + pad * 2;
        if (Tuning.Sharpen > 0) img = Sharpen(img, ow, oh, Tuning.Sharpen);
        if (Tuning.Binarize) Binarize(img);

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            CryptographicBuffer.CreateFromByteArray(img), BitmapPixelFormat.Gray8, ow, oh);
        var result = await engine.RecognizeAsync(bitmap);

        // 元画像の座標に戻す
        double sx = (double)dw / src.Width, sy = (double)dh / src.Height;
        var lines = new List<OcrLineData>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            lines.Add(new OcrLineData(
                line.Words.Select(w => w.Text).ToArray(),
                (line.Words.Min(w => w.BoundingRect.X) - pad) / sx,
                (line.Words.Min(w => w.BoundingRect.Y) - pad) / sy,
                (line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width) - pad) / sx,
                (line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height) - pad) / sy));
        }
        return lines;
    }

    // ───────── 多数決 ─────────

    /// <summary>
    /// 候補ごとに同じ位置の行を対応づけ、他の候補との編集距離の合計が最小の行(=多数派)を採用する。
    /// 基準の候補で抜けた行も、他の 2 候補以上に現れていれば補う。
    /// </summary>
    private static List<OcrLineData> Vote(List<List<OcrLineData>> candidates)
    {
        var used = candidates.Select(c => new bool[c.Count]).ToArray();
        var output = new List<OcrLineData>();
        bool added = false;

        for (int ci = 0; ci < candidates.Count; ci++)
        {
            for (int li = 0; li < candidates[ci].Count; li++)
            {
                if (used[ci][li]) continue;
                var anchor = candidates[ci][li];
                used[ci][li] = true;
                var group = new List<OcrLineData> { anchor };
                for (int cj = ci + 1; cj < candidates.Count; cj++)
                {
                    int best = -1;
                    double bestOverlap = 0.5;
                    for (int lj = 0; lj < candidates[cj].Count; lj++)
                    {
                        if (used[cj][lj]) continue;
                        double overlap = Overlap(anchor, candidates[cj][lj]);
                        if (overlap > bestOverlap) { bestOverlap = overlap; best = lj; }
                    }
                    if (best >= 0)
                    {
                        used[cj][best] = true;
                        group.Add(candidates[cj][best]);
                    }
                }
                // 基準以外の候補にしか出てこない 1 回きりの行はノイズとして捨てる
                if (ci > 0 && group.Count < 2) continue;
                output.Add(Medoid(group, anchor));
                if (ci > 0) added = true;
            }
        }
        // 補った行があるときだけ上から順に並べ直す(段組みの読み順を崩さないため)
        return added ? output.OrderBy(l => l.Top).ToList() : output;
    }

    private static OcrLineData Medoid(List<OcrLineData> group, OcrLineData anchor)
    {
        if (group.Count <= 2) return group[0];
        var texts = group.Select(g => g.Compact).ToArray();
        int best = 0;
        long bestScore = long.MaxValue;
        for (int i = 0; i < group.Count; i++)
        {
            long score = 0;
            for (int j = 0; j < group.Count; j++)
                if (i != j) score += Levenshtein(texts[i], texts[j]);
            if (score < bestScore) { bestScore = score; best = i; }
        }
        var chosen = group[best];
        return chosen with { Left = anchor.Left, Top = anchor.Top, Right = anchor.Right, Bottom = anchor.Bottom };
    }

    private static double Overlap(OcrLineData a, OcrLineData b)
    {
        double v = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        double h = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        if (v <= 0 || h <= 0) return 0;
        double vr = v / Math.Min(a.Bottom - a.Top, b.Bottom - b.Top);
        double hr = h / Math.Min(a.Right - a.Left, b.Right - b.Left);
        return Math.Min(vr, hr);
    }

    internal static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // ───────── 画像処理 ─────────

    private static byte[] ToGray(byte[] bgra, int width, int height)
    {
        var gray = new byte[width * height];
        for (int i = 0, j = 0; i < gray.Length; i++, j += 4)
            gray[i] = (byte)((bgra[j] * 29 + bgra[j + 1] * 150 + bgra[j + 2] * 77) >> 8);
        return gray;
    }

    /// <summary>最も多い明るさ(=背景)を返す。</summary>
    private static byte EstimateBackground(byte[] gray)
    {
        Span<int> hist = stackalloc int[32];
        foreach (var v in gray) hist[v >> 3]++;
        int best = 0;
        for (int i = 1; i < 32; i++) if (hist[i] > hist[best]) best = i;
        return (byte)(best * 8 + 4);
    }

    private static void Invert(byte[] gray)
    {
        for (int i = 0; i < gray.Length; i++) gray[i] = (byte)(255 - gray[i]);
    }

    private static void StretchContrast(byte[] gray)
    {
        Span<int> hist = stackalloc int[256];
        foreach (var v in gray) hist[v]++;
        int cut = gray.Length / 200; // 上下 0.5% を外れ値として無視
        int lo = 0, hi = 255, acc = 0;
        for (; lo < 255; lo++) { acc += hist[lo]; if (acc > cut) break; }
        acc = 0;
        for (; hi > 0; hi--) { acc += hist[hi]; if (acc > cut) break; }
        if (hi - lo < 32) return;

        Span<byte> map = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
            map[i] = (byte)Math.Clamp((i - lo) * 255 / (hi - lo), 0, 255);
        for (int i = 0; i < gray.Length; i++) gray[i] = map[gray[i]];
    }

    private static byte[] Resize(byte[] src, int sw, int sh, int dw, int dh, int pad, byte padValue, bool bicubic)
    {
        int ow = dw + pad * 2, oh = dh + pad * 2;
        var dst = new byte[ow * oh];
        Array.Fill(dst, padValue);

        int taps = bicubic ? 4 : 2;
        var (xi, xw) = Weights(sw, dw, taps);
        var (yi, yw) = Weights(sh, dh, taps);

        // 横方向 → 縦方向 の分離フィルタ
        var tmp = new float[sh * dw];
        Parallel.For(0, sh, y =>
        {
            int row = y * sw;
            for (int x = 0; x < dw; x++)
            {
                float s = 0;
                for (int k = 0; k < taps; k++) s += src[row + xi[x * taps + k]] * xw[x * taps + k];
                tmp[y * dw + x] = s;
            }
        });
        Parallel.For(0, dh, y =>
        {
            int o = (y + pad) * ow + pad;
            for (int x = 0; x < dw; x++)
            {
                float s = 0;
                for (int k = 0; k < taps; k++) s += tmp[yi[y * taps + k] * dw + x] * yw[y * taps + k];
                dst[o + x] = (byte)Math.Clamp((int)(s + 0.5f), 0, 255);
            }
        });
        return dst;
    }

    private static (int[] idx, float[] w) Weights(int srcLen, int dstLen, int taps)
    {
        var idx = new int[dstLen * taps];
        var w = new float[dstLen * taps];
        double ratio = (double)srcLen / dstLen;
        for (int d = 0; d < dstLen; d++)
        {
            double f = (d + 0.5) * ratio - 0.5;
            int b = (int)Math.Floor(f);
            double t = f - b;
            if (taps == 2)
            {
                idx[d * 2] = Math.Clamp(b, 0, srcLen - 1);
                idx[d * 2 + 1] = Math.Clamp(b + 1, 0, srcLen - 1);
                w[d * 2] = (float)(1 - t);
                w[d * 2 + 1] = (float)t;
            }
            else
            {
                // Catmull-Rom
                double t2 = t * t, t3 = t2 * t;
                double w0 = -0.5 * t3 + t2 - 0.5 * t;
                double w1 = 1.5 * t3 - 2.5 * t2 + 1;
                double w2 = -1.5 * t3 + 2 * t2 + 0.5 * t;
                double w3 = 0.5 * t3 - 0.5 * t2;
                for (int k = 0; k < 4; k++) idx[d * 4 + k] = Math.Clamp(b - 1 + k, 0, srcLen - 1);
                w[d * 4] = (float)w0; w[d * 4 + 1] = (float)w1; w[d * 4 + 2] = (float)w2; w[d * 4 + 3] = (float)w3;
            }
        }
        return (idx, w);
    }

    private static byte[] Sharpen(byte[] img, int w, int h, double amount)
    {
        var dst = (byte[])img.Clone();
        Parallel.For(1, h - 1, y =>
        {
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                int blur = (img[i - w - 1] + img[i - w] + img[i - w + 1] + img[i - 1] + img[i] + img[i + 1]
                            + img[i + w - 1] + img[i + w] + img[i + w + 1]) / 9;
                dst[i] = (byte)Math.Clamp((int)(img[i] + amount * (img[i] - blur)), 0, 255);
            }
        });
        return dst;
    }

    private static void Binarize(byte[] img)
    {
        Span<int> hist = stackalloc int[256];
        foreach (var v in img) hist[v]++;
        long total = img.Length, sum = 0;
        for (int i = 0; i < 256; i++) sum += (long)i * hist[i];
        long sumB = 0, wB = 0;
        double best = -1;
        int threshold = 128;
        for (int i = 0; i < 256; i++)
        {
            wB += hist[i];
            if (wB == 0) continue;
            long wF = total - wB;
            if (wF == 0) break;
            sumB += (long)i * hist[i];
            double mB = (double)sumB / wB, mF = (double)(sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; threshold = i; }
        }
        for (int i = 0; i < img.Length; i++) img[i] = img[i] > threshold ? (byte)255 : (byte)0;
    }

    /// <summary>行の高さの中央値(元画像のピクセル)。</summary>
    private static double MedianLineHeight(List<OcrLineData> lines)
    {
        if (lines.Count == 0) return 0;
        var heights = lines.Select(l => l.Bottom - l.Top).OrderBy(h => h).ToList();
        return heights[heights.Count / 2];
    }
}
