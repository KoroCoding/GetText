namespace GetText.Tests;

public class JapaneseCorrectorTests
{
    [Theory]
    [InlineData("所でこャーこャー泣いていた", "所でニャーニャー泣いていた")]
    [InlineData("バ一ジョン", "バージョン")]
    [InlineData("コンピュ一タ", "コンピュータ")]
    [InlineData("どこで生れたかヒんヒ見当", "どこで生れたかとんと見当")]
    [InlineData("公開しまし0", "公開しまし。")]
    [InlineData("テスト口グ", "テストログ")]
    public void 典型的な誤認識を直す(string input, string expected) =>
        Assert.Equal(expected, JapaneseCorrector.Correct(input));

    [Theory]
    [InlineData("一つ目")]
    [InlineData("第一回")]
    [InlineData("口座を開く")]
    [InlineData("工場で力を出す")]
    [InlineData("ゲームの一覧")]
    [InlineData("価格は100円")]
    [InlineData("スコアは10")]
    [InlineData("東京へ行く")]
    [InlineData("カレーとライス")]
    [InlineData("2026-09-29")]
    [InlineData("A-B テスト")]
    [InlineData("Hello World 0")]
    public void 正しい文は変えない(string input) =>
        Assert.Equal(input, JapaneseCorrector.Correct(input));
}

public class TextConverterTests
{
    [Theory]
    [InlineData(ConvertMode.HalfWidthAlnum, "ＡＢＣ１２３　テスト", "ABC123 テスト")]
    [InlineData(ConvertMode.FullWidth, "abc ﾃｽﾄ", "ａｂｃ　テスト")]
    [InlineData(ConvertMode.Hiragana, "テスト ﾃｽﾄ", "てすと てすと")]
    [InlineData(ConvertMode.Katakana, "ひらがな", "ヒラガナ")]
    [InlineData(ConvertMode.JoinLines, "line one\r\nline two\r\n\r\n次の段落", "line one line two\r\n\r\n次の段落")]
    [InlineData(ConvertMode.None, "そのまま", "そのまま")]
    public void 変換する(ConvertMode mode, string input, string expected) =>
        Assert.Equal(expected, TextConverter.Apply(input, mode));
}

public class TextScriptTests
{
    [Theory]
    [InlineData("The quick brown fox", false, ScriptKind.Foreign)]
    [InlineData("日本語の文です", false, ScriptKind.Japanese)]
    [InlineData("技術統合", true, ScriptKind.Japanese)]
    [InlineData("机器翻译", false, ScriptKind.Foreign)]
    [InlineData("12,345 100%", false, ScriptKind.Neutral)]
    public void 言語を判定する(string text, bool documentHasKana, ScriptKind expected) =>
        Assert.Equal(expected, TextScript.Classify(text, documentHasKana));

    [Theory]
    [InlineData("trans-", "lation", "translation")]
    [InlineData("日本", "語", "日本語")]
    [InlineData("Hello", "world", "Hello world")]
    [InlineData("Windows", "の設定", "Windowsの設定")]
    public void 言語に応じてつなげる(string a, string b, string expected) =>
        Assert.Equal(expected, TextScript.Join(a, b));
}

public class OcrDocumentTests
{
    [Fact]
    public void 折り返した文はつなげ_短い行はつなげない()
    {
        var doc = OcrDocument.From(
        [
            new OcrLineData(["This", "is", "a", "long", "sentence", "that", "wraps"], 0, 0, 400, 20),
            new OcrLineData(["onto", "the", "next", "line."], 0, 24, 200, 44),
            new OcrLineData(["File"], 0, 80, 40, 100),
            new OcrLineData(["Edit"], 0, 104, 40, 124),
        ], joinCjk: true, correct: false);

        var units = doc.ToTranslationUnits();
        Assert.Equal(2, units.Count); // 行間が空いているので 2 段落
        Assert.Equal(["This is a long sentence that wraps onto the next line."], units[0]);
        Assert.Equal(["File", "Edit"], units[1]);
    }

    [Fact]
    public void 和文の文字間の空白を詰める()
    {
        var doc = OcrDocument.From([new OcrLineData(["日", "本", "語", "の", "OCR", "test"], 0, 0, 200, 20)], true, false);
        Assert.Equal("日本語のOCR test", doc.ToDisplayText());
    }

    [Fact]
    public void Flatten_と_FromFlat_は元に戻る()
    {
        var doc = Doc("一行目", "二行目", null, "三行目");
        Assert.Equal(doc.ToDisplayText(), OcrDocument.FromFlat(doc.Flatten()).ToDisplayText());
    }

    /// <summary>文字列の並びから文書を作る。null は段落の区切り。</summary>
    internal static OcrDocument Doc(params string?[] lines)
    {
        var items = new List<OcrLineInfo?>();
        double y = 10;
        foreach (var line in lines)
        {
            if (line == null) { items.Add(null); continue; }
            items.Add(new OcrLineInfo(line, 0, y, line.Length * 20, y + 20));
            y += 30;
        }
        return OcrDocument.FromFlat(items);
    }
}

public class MergeRowsTests
{
    [Fact]
    public void 表のセルを1行にまとめる()
    {
        var rows = AiOcr.MergeRows(
        [
            new OcrLineData(["関東"], 10, 0, 50, 20),
            new OcrLineData(["：直流"], 100, 1, 160, 21),
            new OcrLineData(["東北：交流"], 10, 30, 160, 50),
        ]);
        Assert.Equal(2, rows.Count);
        Assert.Equal("関東 ：直流", string.Concat(rows[0].Words)); // 間が空いているので空白で区切る
        Assert.Equal("東北：交流", string.Concat(rows[1].Words));
    }

    [Fact]
    public void 近い断片は空白なしでつなげる()
    {
        var rows = AiOcr.MergeRows([new OcrLineData(["関東"], 10, 0, 50, 20), new OcrLineData(["：直流"], 55, 0, 110, 20)]);
        Assert.Equal("関東：直流", string.Concat(Assert.Single(rows).Words));
    }

    [Fact]
    public void 段組みの長い行はまとめない()
    {
        var rows = AiOcr.MergeRows(
        [
            new OcrLineData(["左の段の長い文章がここに続いています"], 0, 0, 400, 20),
            new OcrLineData(["右の段の長い文章もここに続いています"], 450, 0, 850, 20),
        ]);
        Assert.Equal(2, rows.Count);
    }
}

public class TextAccumulatorTests
{
    private const double Height = 1000; // 端の行が除かれないよう十分な高さ

    private static string Text(TextAccumulator acc) => acc.ToDocument().ToDisplayText().Replace("\r\n", "|");

    [Fact]
    public void 下へスクロールすると続きだけを追記する()
    {
        var acc = new TextAccumulator();
        acc.Add(OcrDocumentTests.Doc("一行目です", "二行目です", "三行目です"), Height);
        int added = acc.Add(OcrDocumentTests.Doc("二行目です", "三行目です", "四行目です", "五行目です"), Height);
        Assert.Equal(2, added);
        Assert.Equal("一行目です|二行目です|三行目です|四行目です|五行目です", Text(acc));
    }

    [Fact]
    public void 上へスクロールすると前に足す()
    {
        var acc = new TextAccumulator();
        acc.Add(OcrDocumentTests.Doc("三行目です", "四行目です", "五行目です"), Height);
        acc.Add(OcrDocumentTests.Doc("一行目です", "二行目です", "三行目です", "四行目です"), Height);
        Assert.Equal("一行目です|二行目です|三行目です|四行目です|五行目です", Text(acc));
    }

    [Fact]
    public void 同じ画面や途中の画面では重複しない()
    {
        var acc = new TextAccumulator();
        acc.Add(OcrDocumentTests.Doc("一行目です", "二行目です", "三行目です", "四行目です"), Height);
        Assert.Equal(0, acc.Add(OcrDocumentTests.Doc("一行目です", "二行目です", "三行目です", "四行目です"), Height));
        Assert.Equal(0, acc.Add(OcrDocumentTests.Doc("二行目です", "三行目です"), Height));
        Assert.Equal(4, acc.LineCount);
    }

    [Fact]
    public void OCRの小さな揺れは同じ行とみなす()
    {
        var acc = new TextAccumulator();
        acc.Add(OcrDocumentTests.Doc("光学式文字認識はテキストの画像を変換します", "二行目の文章があります"), Height);
        acc.Add(OcrDocumentTests.Doc("光学式文字認識はテキストの画像を変換しま", "二行目の文章があります", "三行目です"), Height);
        Assert.Equal(3, acc.LineCount);
    }

    [Fact]
    public void 番号だけ違う箇条書きでも正しい位置に合わせる()
    {
        var acc = new TextAccumulator();
        acc.Add(OcrDocumentTests.Doc("項目1: 説明文です", "項目2: 説明文です", "項目3: 説明文です", "項目4: 説明文です"), Height);
        int added = acc.Add(OcrDocumentTests.Doc("項目3: 説明文です", "項目4: 説明文です", "項目5: 説明文です", "項目6: 説明文です"), Height);
        Assert.Equal(2, added);
        Assert.Equal("項目1: 説明文です|項目2: 説明文です|項目3: 説明文です|項目4: 説明文です|項目5: 説明文です|項目6: 説明文です", Text(acc));
    }

    [Fact]
    public void 重なりがなければ段落を分けて後ろに足す()
    {
        var acc = new TextAccumulator();
        acc.Add(OcrDocumentTests.Doc("最初の場所の一行目", "最初の場所の二行目"), Height);
        acc.Add(OcrDocumentTests.Doc("別の場所の一行目", "別の場所の二行目"), Height);
        Assert.Equal("最初の場所の一行目|最初の場所の二行目||別の場所の一行目|別の場所の二行目", Text(acc));
    }

    [Fact]
    public void 上下の端で切れた行は取り込まない()
    {
        var acc = new TextAccumulator();
        var doc = OcrDocument.FromFlat(
        [
            new OcrLineInfo("切れた上端", 0, 0, 100, 18),
            new OcrLineInfo("完全に見える行", 0, 30, 100, 50),
            new OcrLineInfo("切れた下端", 0, 82, 100, 100),
        ]);
        acc.Add(doc, captureHeight: 100);
        Assert.Equal("完全に見える行", Text(acc));
    }
}

public class ScreenCaptureTests
{
    private static byte[] Image(int changedPixels, int size = 200 * 200)
    {
        var pixels = new byte[size * 4];
        for (int i = 0; i < changedPixels; i++) pixels[i * 4] = 255;
        return pixels;
    }

    [Fact]
    public void 同じ画面は変化なし() => Assert.False(ScreenCapture.HasChanged(Image(0), Image(0)));

    [Fact]
    public void カーソルの点滅程度は無視する() => Assert.False(ScreenCapture.HasChanged(Image(0), Image(30)));

    [Fact]
    public void 文字が変われば変化あり() => Assert.True(ScreenCapture.HasChanged(Image(0), Image(200)));

    [Fact]
    public void サイズが変われば変化あり() => Assert.True(ScreenCapture.HasChanged(Image(0), Image(0, 100 * 100)));
}

public class TranslatorTests
{
    [Fact]
    public void キャッシュのキーは空白の違いを無視する() =>
        Assert.Equal(Translator.Key("Hello   world\n again "), Translator.Key("Hello world again"));
}
