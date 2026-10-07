namespace GetText;

/// <summary>
/// 保護された画面 (取り込みを禁止された窓・DRM の動画など) は、OS が黒く (または一色で) 写す。
/// それを見分けて知らせるだけで、回避はしない (Windows 版・Mac 版で共有)。
/// </summary>
public static class ProtectedContent
{
    public const string Title = "画面が黒く写っています";

    public const string Detail = "保護された画面 (動画の配信・一部のアプリなど) は、OS が黒く写すため読み取れません。GetText はこの保護を回避しません。";

    /// <summary>
    /// 保護された画面らしいか: 暗い一色 (OS は保護された窓・動画を黒く写す)。白い一色は白紙として扱い、保護とはみなさない。
    /// </summary>
    public static bool LooksProtected(byte[] bgra, int width, int height) =>
        Stats(bgra, width, height) is { } s && s.StdDev <= 2.0 && s.Mean < 40;

    /// <summary>ほぼ一色か (明るさのばらつきがとても小さい)。大きな画像でも速いよう、間引いて見る。</summary>
    public static bool IsUniform(byte[] bgra, int width, int height, double maxStdDev = 2.0) =>
        Stats(bgra, width, height) is { } s && s.StdDev <= maxStdDev;

    private static (double Mean, double StdDev)? Stats(byte[] bgra, int width, int height)
    {
        if (width < 1 || height < 1 || bgra.Length < width * height * 4) return null;
        int stepX = Math.Max(1, width / 64), stepY = Math.Max(1, height / 64);
        double sum = 0, sumSq = 0;
        int n = 0;
        for (int y = 0; y < height; y += stepY)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x += stepX)
            {
                int i = row + x * 4;
                double v = 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];
                sum += v;
                sumSq += v * v;
                n++;
            }
        }
        double mean = sum / n;
        return (mean, Math.Sqrt(Math.Max(0, sumSq / n - mean * mean)));
    }
}
