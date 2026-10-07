namespace GetText.Tests;

/// <summary>検索で見つかった所の、画面の上の位置 (OCR エンジンが返した文字・単語の位置を使う)。</summary>
public class SearchGeometryTests
{
    // 1 文字ずつの位置 (AI OCR と同じ形。空白は含まない)。字の幅はばらばら (比例字体)
    private static OcrLineData CharLine(string text, double top, params double[] widths)
    {
        var spans = new List<OcrSpan>();
        double x = 10;
        int w = 0;
        foreach (char c in text)
        {
            if (c == ' ') { x += 6; continue; }
            double width = widths.Length > 0 ? widths[w++ % widths.Length] : 12;
            spans.Add(new OcrSpan(c.ToString(), x, top, x + width, top + 20));
            x += width;
        }
        return new OcrLineData([text], spans[0].Left, top, spans[^1].Right, top + 20, spans);
    }

    private static List<MatchBox> Find(IEnumerable<OcrLineData> lines, string query, bool joinCjk = true)
    {
        var doc = OcrDocument.From(lines, joinCjk, correct: false);
        var shown = doc.ToDisplayText();
        return TextSearch.ToBoxes(doc, shown, TextSearch.Find(shown, query));
    }

    [Fact]
    public void CharacterPositionsAreUsedForProportionalText()
    {
        // 「i」は細く「W」は太い: 文字数で割った位置ではなく、実際の文字の位置になる
        var line = CharLine("iiiiWWWW", 0, 4, 4, 4, 4, 20, 20, 20, 20);
        var boxes = Find([line], "WW"); // (重ならないように 2 か所)
        Assert.Equal(2, boxes.Count);
        Assert.Equal(26, boxes[0].Left, 3);  // 10 + 4×4
        Assert.Equal(66, boxes[0].Right, 3); // 26 + 20×2
        Assert.Equal(106, boxes[1].Right, 3);
    }

    [Fact]
    public void MixedJapaneseEnglishAndPunctuation()
    {
        var line = CharLine("日本語とEnglishの行、句読点。", 0, 24, 24, 24, 24, 14, 12, 12, 6, 6, 10, 12, 24, 24, 12, 24, 24, 24, 12);
        var box = Assert.Single(Find([line], "English"));
        Assert.Equal(line.Spans![4].Left, box.Left, 3);
        Assert.Equal(line.Spans![10].Right, box.Right, 3);
        // 全角・半角の違いは無視して見つけ、位置は全角の字の上
        var full = Assert.Single(Find([line], "ｅｎｇ"));
        Assert.Equal(line.Spans![4].Left, full.Left, 3);
        Assert.Equal(line.Spans![6].Right, full.Right, 3);
    }

    [Fact]
    public void SpacesInTextDoNotShiftPositions()
    {
        // 空白は位置を持たない (OCR は空白の枠を返さない)。表示の空白を飛ばして対応させる
        var line = CharLine("Hello brave world", 5);
        var box = Assert.Single(Find([line], "world"));
        Assert.Equal(line.Spans![10].Left, box.Left, 3);
        Assert.Equal(line.Spans![14].Right, box.Right, 3);
        Assert.Equal(5, box.Top, 3);
        // 空白だけに一致したときは印を付けない
        Assert.Empty(Find([line], " "));
    }

    [Fact]
    public void WordPositionsGiveWordGranularity()
    {
        // Windows OCR は単語ごとの位置: 単語の途中に一致したら、その単語全体 (文字の位置は推測しない)
        var words = new[] { new OcrSpan("Quarterly", 10, 0, 100, 20), new OcrSpan("Report", 110, 0, 170, 20) };
        var line = new OcrLineData(["Quarterly", "Report"], 10, 0, 170, 20, words);
        var box = Assert.Single(Find([line], "port"));
        Assert.Equal(110, box.Left, 3);
        Assert.Equal(170, box.Right, 3);
        var both = Assert.Single(Find([line], "ly Re"));
        Assert.Equal(10, both.Left, 3);
        Assert.Equal(170, both.Right, 3);
    }

    [Fact]
    public void JapaneseWordsJoinedWithoutSpaces()
    {
        // Windows OCR は日本語を 1 文字ずつの単語で返す (和文の空白を詰めて表示しても位置はそろう)
        var chars = "売上は前年".Select((c, i) => new OcrSpan(c.ToString(), 10 + i * 20, 0, 28 + i * 20, 20)).ToArray();
        var line = new OcrLineData(chars.Select(c => c.Text).ToArray(), 10, 0, 108, 20, chars);
        var box = Assert.Single(Find([line], "前年"));
        Assert.Equal(70, box.Left, 3);
        Assert.Equal(108, box.Right, 3);
    }

    [Fact]
    public void MismatchedSpansFallBackToWholeLine()
    {
        // 文字の補正などで、位置の数と表示の文字の数が合わない: 行全体
        var spans = new[] { new OcrSpan("ab", 10, 0, 30, 20) };
        var line = new OcrLineData(["abc"], 10, 0, 50, 20, spans);
        var box = Assert.Single(Find([line], "c"));
        Assert.Equal(10, box.Left, 3);
        Assert.Equal(50, box.Right, 3);
        // 位置を持たない行 (Mac の文字認識など) も行全体
        var plain = new OcrLineData(["Plain line"], 0, 40, 200, 60);
        var p = Assert.Single(Find([plain], "line"));
        Assert.Equal(0, p.Left, 3);
        Assert.Equal(200, p.Right, 3);
    }

    [Fact]
    public void SurrogatePairsAndEmojiCountAsOneGlyph()
    {
        string text = "𠮷野家😀OK";
        var spans = new List<OcrSpan>();
        double x = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            spans.Add(new OcrSpan(rune.ToString(), x, 0, x + 20, 20));
            x += 20;
        }
        var line = new OcrLineData([text], 0, 0, x, 20, spans);
        Assert.Equal(6, OcrSpans.CountGlyphs(text));
        var box = Assert.Single(Find([line], "OK"));
        Assert.Equal(80, box.Left, 3);
        Assert.Equal(120, box.Right, 3);
    }

    [Fact]
    public void MatchAcrossLinesGivesOneBoxPerLine()
    {
        var a = CharLine("first line ends", 0);
        var b = CharLine("ends again", 25);
        var boxes = Find([a, b], "ends");
        Assert.Equal(2, boxes.Count);
        Assert.Equal(0, boxes[0].Top, 3);
        Assert.Equal(25, boxes[1].Top, 3);
        Assert.Equal(1, boxes[1].MatchIndex);
    }

    [Fact]
    public void AiOcrRowMergeKeepsSpans()
    {
        // 表のセルのように分かれて検出された断片をまとめても、文字の位置はそのまま使える
        var left = CharLine("関東", 0);
        var right = new OcrLineData(["：直流"], 200, 0, 236, 20,
            [new OcrSpan("：", 200, 0, 212, 20), new OcrSpan("直", 212, 0, 224, 20), new OcrSpan("流", 224, 0, 236, 20)]);
        var merged = Assert.Single(AiOcr.MergeRows([left, right]));
        Assert.NotNull(merged.Spans);
        var box = Assert.Single(Find([merged], "直流"));
        Assert.Equal(212, box.Left, 3);
        Assert.Equal(236, box.Right, 3);
    }
}
