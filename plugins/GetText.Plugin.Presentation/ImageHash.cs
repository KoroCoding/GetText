using System.Numerics;

namespace GetText.Plugins.Presentation;

/// <summary>
/// 画像の指紋 (似ているかを速く比べる)。
/// dHash: 9×8 に縮めた明るさの左右の差 (64 ビット)。コマの間の小さな変化 (動き) を見る。
/// pHash: 32×32 に縮めた明るさの DCT の低い周波数 8×8 (64 ビット)。保存したスライドと同じかを見る (色・拡大・圧縮の違いに強い)。
/// </summary>
public static class ImageHash
{
    /// <summary>BGRA (上から) を w×h の明るさ (0〜255) に縮める (面積の平均)。</summary>
    public static double[] Gray(byte[] bgra, int width, int height, int w, int h)
    {
        var result = new double[w * h];
        for (int y = 0; y < h; y++)
        {
            int y0 = y * height / h, y1 = Math.Max(y0 + 1, (y + 1) * height / h);
            for (int x = 0; x < w; x++)
            {
                int x0 = x * width / w, x1 = Math.Max(x0 + 1, (x + 1) * width / w);
                // 大きな画像でも速いよう、範囲の中を最大 8×8 点だけ見る
                int sy = Math.Max(1, (y1 - y0) / 8), sx = Math.Max(1, (x1 - x0) / 8);
                double sum = 0;
                int n = 0;
                for (int yy = y0; yy < y1; yy += sy)
                {
                    int row = yy * width * 4;
                    for (int xx = x0; xx < x1; xx += sx)
                    {
                        int i = row + xx * 4;
                        sum += 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];
                        n++;
                    }
                }
                result[y * w + x] = n > 0 ? sum / n : 0;
            }
        }
        return result;
    }

    public static ulong DHash(byte[] bgra, int width, int height)
    {
        var g = Gray(bgra, width, height, 9, 8);
        ulong hash = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                if (g[y * 9 + x] > g[y * 9 + x + 1]) hash |= 1UL << (y * 8 + x);
        return hash;
    }

    private static readonly double[,] Cosines = BuildCosines();

    private static double[,] BuildCosines()
    {
        var c = new double[8, 32];
        for (int u = 0; u < 8; u++)
            for (int x = 0; x < 32; x++)
                c[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / 64);
        return c;
    }

    /// <summary>32×32 の明るさ (pHash と、変わった面積の比べに使う)。</summary>
    public static double[] Thumbnail(byte[] bgra, int width, int height) => Gray(bgra, width, height, 32, 32);

    public static ulong PHash(byte[] bgra, int width, int height) => PHash(Thumbnail(bgra, width, height));

    public static ulong PHash(double[] g)
    {
        // 2 次元の DCT のうち、低い周波数の 8×8 だけを計算する
        var rows = new double[8 * 32];
        for (int u = 0; u < 8; u++)
            for (int y = 0; y < 32; y++)
            {
                double s = 0;
                for (int x = 0; x < 32; x++) s += g[y * 32 + x] * Cosines[u, x];
                rows[u * 32 + y] = s;
            }
        var dct = new double[64];
        for (int v = 0; v < 8; v++)
            for (int u = 0; u < 8; u++)
            {
                double s = 0;
                for (int y = 0; y < 32; y++) s += rows[u * 32 + y] * Cosines[v, y];
                dct[v * 8 + u] = s;
            }
        // 直流 (全体の明るさ) を除いた値の中央値より大きいか
        var values = dct.Skip(1).Order().ToArray();
        double median = (values[31] + values[32]) / 2;
        ulong hash = 0;
        for (int i = 1; i < 64; i++)
            if (dct[i] > median) hash |= 1UL << i;
        return hash;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>平均の明るさ (0〜255)。</summary>
    public static double Brightness(double[] thumbnail) => thumbnail.Length == 0 ? 0 : thumbnail.Average();

    /// <summary>
    /// 32×32 の升目のうち、明るさが level より大きく変わった升目の割合 (0〜1)。
    /// 箇条書きが 1 行増えたような、小さいがはっきりした変化を見る (pHash は全体の形を見るので、小さな追加に鈍い)。
    /// カーソル・圧縮の乱れのような点の変化は、升目の平均ではほとんど変わらない。
    /// </summary>
    public static double ChangedArea(double[] a, double[] b, double level = 24)
    {
        if (a.Length != b.Length || a.Length == 0) return 1;
        int changed = 0;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > level) changed++;
        return (double)changed / a.Length;
    }

    /// <summary>
    /// ほぼ一色か (真っ黒・真っ白など)。保護された画面 (取り込みを禁止された窓・DRM の動画) は OS が黒くするので、そのしるし。
    /// 明るさの標準偏差が小さいときに true。
    /// </summary>
    public static bool IsUniform(byte[] bgra, int width, int height, double maxStdDev = 2.0)
    {
        var g = Gray(bgra, width, height, 32, 32);
        double mean = g.Average();
        double variance = g.Sum(v => (v - mean) * (v - mean)) / g.Length;
        return Math.Sqrt(variance) <= maxStdDev;
    }
}
