using GetText.Plugins;

namespace GetText;

/// <summary>読み取りの画面の 1 回分を、拡張機能に渡す形 (OcrFrameEventArgs) にする (Windows 版・Mac 版で共有)。</summary>
public static class PluginFrames
{
    /// <param name="doc">読み取った 1 回分 (蓄積・まとまりの前)。行の座標は読み取った画像の中のピクセル。</param>
    /// <param name="region">読み取った範囲 (画面の物理ピクセル)。</param>
    /// <param name="toScreen">画像のピクセル → 画面の物理ピクセルの倍率 (Mac は画像を拡大して読むので 1 でない)。</param>
    /// <param name="image">読み取った画像 (BGRA・上から)。拡張機能が求めたときだけ複製して渡す。</param>
    /// <param name="source">読み取りの範囲の下のアプリの名前と窓の題名 (分からなければ null)。</param>
    public static OcrFrameEventArgs From(OcrDocument doc, (int X, int Y, int Width, int Height) region, double toScreen = 1,
        (byte[] Bgra, int Width, int Height)? image = null, string? translation = null, DateTimeOffset? time = null,
        (string App, string Title)? source = null)
    {
        var lines = doc.Lines
            .Select(l => new OcrTextLine(l.Text,
                region.X + l.Left * toScreen, region.Y + l.Top * toScreen, region.X + l.Right * toScreen, region.Y + l.Bottom * toScreen))
            .ToList();
        var copy = image is { } i && i.Width > 0 && i.Height > 0 && i.Bgra.Length == i.Width * i.Height * 4 ? image : null;
        return new OcrFrameEventArgs
        {
            Time = time ?? DateTimeOffset.Now,
            Lines = lines,
            Text = string.Join("\n", lines.Select(l => l.Text)),
            Translation = translation,
            Region = region,
            SourceApplication = source?.App,
            SourceWindowTitle = source?.Title,
            GetImage = copy is { } c ? () => ((byte[])c.Bgra.Clone(), c.Width, c.Height) : null,
        };
    }
}
