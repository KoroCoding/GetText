using GetText.Plugins;

namespace GetText;

/// <summary>
/// 訳を画面に重ねる (読み取りの画面の 表示 → 訳を画面に重ねる)。読み取りの画面がもう訳した文 (同じ翻訳・同じ覚え) を、
/// その文の行が画面で占める範囲に重ねる。重ねるために訳し直さない (翻訳の設定・PC 内 / オンラインの印も読み取りの画面と同じ)。
/// 重ねる窓はクリックを下に通し、読み取りの取り込みに写らない。Windows 版・Mac 版で共有。
/// </summary>
public static class TranslationOverlay
{
    /// <summary>重ねる四角 (画面の物理ピクセル)。外国語の単位で、訳が出来ているものだけ。</summary>
    /// <param name="units">翻訳の単位・範囲 (読み取った画像のピクセル) と種類。</param>
    /// <param name="translated">単位の文の訳 (まだ無ければ null)。</param>
    /// <param name="origin">読み取った範囲の左上 (画面の物理ピクセル)。</param>
    /// <param name="toScreen">読み取った画像のピクセル → 画面の物理ピクセルの倍率。</param>
    public static List<OverlayLabel> Labels(IEnumerable<(TranslationUnit Unit, ScriptKind Kind)> units, Func<string, string?> translated,
        (double X, double Y) origin, double toScreen = 1)
    {
        var labels = new List<OverlayLabel>();
        foreach (var (unit, kind) in units)
        {
            if (kind != ScriptKind.Foreign || !unit.HasArea) continue;
            if (translated(unit.Text)?.Trim() is not { Length: > 0 } ja) continue;
            labels.Add(new OverlayLabel(
                origin.X + unit.Left * toScreen, origin.Y + unit.Top * toScreen,
                origin.X + unit.Right * toScreen, origin.Y + unit.Bottom * toScreen, ja) { Lines = unit.Lines });
        }
        return labels;
    }

    /// <summary>
    /// 四角に収める文字の大きさ (DIP)。元の 1 行の高さの 7 割から始め、折り返して高さに収まるまで小さくする
    /// (はみ出して下の文字を隠さない)。measure は (文字の大きさ, 幅) → 折り返したときの高さ。
    /// </summary>
    public static double FitFontSize(double width, double height, int lines, Func<double, double, double> measure)
    {
        double size = Math.Clamp(height / Math.Max(1, lines) * 0.72, MinFontSize, MaxFontSize);
        while (size > MinFontSize && measure(size, width) > height) size = Math.Max(MinFontSize, size * 0.9);
        return size;
    }

    public const double MinFontSize = 8, MaxFontSize = 48;
}
