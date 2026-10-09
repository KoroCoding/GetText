namespace GetText.Tests;

/// <summary>
/// 訳を画面に重ねる (読み取りの画面の 表示 → 訳を画面に重ねる): 訳の単位の範囲・重ねる四角・文字の大きさ。
/// 訳は読み取りの画面で訳した文をそのまま使う (重ねるために訳し直さない)。
/// </summary>
public class TranslationOverlayTests
{
    private static OcrLineData Line(string text, double left, double top, double right, double bottom) =>
        new(text.Split(' '), left, top, right, bottom);

    private static List<(TranslationUnit Unit, ScriptKind Kind)> Units(OcrDocument doc) =>
        doc.TranslationUnits().SelectMany(p => p).Select(u => (u, TextScript.Classify(u.Text, doc.HasKana))).ToList();

    [Fact]
    public void WrappedSentenceBecomesOneUnitCoveringItsLines()
    {
        // 折り返した 1 文 (2 行) と、離れた段落の 1 行
        var doc = OcrDocument.From(
        [
            Line("The weekly meeting starts at ten in the large", 10, 10, 410, 30),
            Line("conference room on the third floor.", 10, 34, 300, 54),
            Line("Bring your slides.", 10, 100, 160, 120),
        ], joinCjk: true, correct: false);
        var units = doc.TranslationUnits().SelectMany(p => p).ToList();
        Assert.Equal(2, units.Count);
        Assert.Equal("The weekly meeting starts at ten in the large conference room on the third floor.", units[0].Text);
        Assert.Equal((10.0, 10.0, 410.0, 54.0, 2), (units[0].Left, units[0].Top, units[0].Right, units[0].Bottom, units[0].Lines));
        Assert.Equal((10.0, 100.0, 160.0, 120.0, 1), (units[1].Left, units[1].Top, units[1].Right, units[1].Bottom, units[1].Lines));
        // 文の単位は前と同じ (訳の覚えと日本語訳の欄がそのまま使える)
        Assert.Equal(units.Select(u => u.Text), doc.ToTranslationUnits().SelectMany(p => p));
    }

    [Fact]
    public void LabelsUseExistingTranslationsAtScreenPositions()
    {
        var doc = OcrDocument.From(
        [
            Line("Hello world", 10, 20, 110, 40),
            Line("会議は 10 時からです", 10, 60, 210, 80),
            Line("Not translated yet", 10, 100, 190, 120),
        ], joinCjk: true, correct: false);
        var translations = new Dictionary<string, string> { ["Hello world"] = " こんにちは、世界 " };
        var labels = TranslationOverlay.Labels(Units(doc), t => translations.GetValueOrDefault(t), origin: (1000, 500));

        // 日本語の行には重ねない・まだ訳が無い文も重ねない (訳し直さない)。位置は読み取った範囲の左上を足した画面の位置
        var label = Assert.Single(labels);
        Assert.Equal(("こんにちは、世界", 1010.0, 520.0, 1110.0, 540.0, 1), (label.Text, label.Left, label.Top, label.Right, label.Bottom, label.Lines));
    }

    [Fact]
    public void LabelsScaleImagePixelsToScreen()
    {
        // Mac は画像を拡大して読む: 画像のピクセル → 画面のピクセル
        var doc = OcrDocument.From([Line("Hello world", 20, 40, 220, 80)], joinCjk: true, correct: false);
        var label = Assert.Single(TranslationOverlay.Labels(Units(doc), _ => "こんにちは", origin: (100, 50), toScreen: 0.5));
        Assert.Equal((110.0, 70.0, 210.0, 90.0), (label.Left, label.Top, label.Right, label.Bottom));
    }

    [Fact]
    public void GroupHeadingsAreNotOverlaid()
    {
        // 枠・まとまりごとの見出し【1】は画面に無い (範囲が無い)
        var doc = new OcrDocument();
        doc.Paragraphs.Add([new OcrLineInfo("【1】", 10, 10, 10, 10, IsHeading: true), new OcrLineInfo("Hello world", 10, 10, 110, 30)]);
        var labels = TranslationOverlay.Labels(Units(doc), t => "訳:" + t, origin: (0, 0));
        Assert.Equal("訳:Hello world", Assert.Single(labels).Text);
    }

    [Fact]
    public void FontShrinksUntilTheTranslationFits()
    {
        // 測る関数: 1 文字の幅 = 文字の大きさ、1 行の高さ = 文字の大きさ × 1.3 で折り返す
        static double Measure(string text, double size, double width)
        {
            int perLine = Math.Max(1, (int)(width / size));
            int lines = (text.Length + perLine - 1) / perLine;
            return lines * size * 1.3;
        }

        // 短い訳: 元の 1 行の高さの 7 割のまま
        Assert.Equal(14.4, TranslationOverlay.FitFontSize(200, 20, 1, (s, w) => Measure("こんにちは", s, w)), 3);
        // 2 行分の四角: 1 行の高さで決める
        Assert.Equal(14.4, TranslationOverlay.FitFontSize(400, 40, 2, (s, w) => Measure("短い訳", s, w)), 3);
        // 長い訳: 収まるまで小さくする
        var text = new string('あ', 60);
        double size = TranslationOverlay.FitFontSize(200, 20, 1, (s, w) => Measure(text, s, w));
        Assert.True(size < 14.4);
        Assert.True(Measure(text, size, 200) <= 20 || size == TranslationOverlay.MinFontSize);
        // 収まらなくても読めない大きさにはしない
        Assert.Equal(TranslationOverlay.MinFontSize, TranslationOverlay.FitFontSize(20, 10, 1, (s, w) => Measure(new string('あ', 500), s, w)));
    }

    [Fact]
    public void OverlayIsOffByDefault()
    {
        var settings = new AppSettings();
        settings.Normalize();
        Assert.False(settings.TranslationOverlay);
    }
}
