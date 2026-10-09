namespace GetText.Tests;

/// <summary>枠ごとのまとまりで読む (OcrLayout) と、読み取った文字の検索 (TextSearch)。</summary>
public class OcrLayoutTests
{
    // 白い画像に枠線を描く (BGRA)
    private sealed class Canvas(int width, int height)
    {
        public readonly byte[] Pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        public int Width => width;
        public int Height => height;

        public void Rect(int left, int top, int right, int bottom, byte b = 90, byte g = 90, byte r = 90, int thickness = 2)
        {
            for (int t = 0; t < thickness; t++)
            {
                for (int x = left; x <= right; x++) { Set(x, top + t, b, g, r); Set(x, bottom - t, b, g, r); }
                for (int y = top; y <= bottom; y++) { Set(left + t, y, b, g, r); Set(right - t, y, b, g, r); }
            }
        }

        public void Fill(int left, int top, int right, int bottom, byte v)
        {
            for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                Set(x, y, v, v, v);
        }

        private void Set(int x, int y, byte b, byte g, byte r)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            int i = (y * width + x) * 4;
            Pixels[i] = b; Pixels[i + 1] = g; Pixels[i + 2] = r; Pixels[i + 3] = 255;
        }
    }

    private static OcrLineData Line(string text, double left, double top, double width = 80, double height = 16) =>
        new([text], left, top, left + width, top + height);

    // 名簿のような 2 行 × 3 列の枠 (枠の間はすき間)。行は OCR と同じく上から順に並ぶ
    private static (Canvas Canvas, List<OcrLineData> Lines) Roster()
    {
        var canvas = new Canvas(640, 420);
        var lines = new List<OcrLineData>();
        string[] heads = ["A", "B", "C", "D", "E", "F"];
        for (int row = 0; row < 2; row++)
        for (int col = 0; col < 3; col++)
            canvas.Rect(20 + col * 205, 20 + row * 195, 20 + col * 205 + 190, 20 + row * 195 + 180);
        // 上から順 (枠をまたいで同じ高さの行が並ぶ)
        for (int row = 0; row < 2; row++)
        for (int i = 0; i < 4; i++)
        for (int col = 0; col < 3; col++)
            lines.Add(Line($"{heads[row * 3 + col]}-{i + 1} 名前", 34 + col * 205, 34 + row * 195 + i * 30));
        return (canvas, lines);
    }

    [Fact]
    public void FramedRosterIsGroupedPerBox()
    {
        var (canvas, lines) = Roster();
        var segments = OcrLayout.FindSegments(canvas.Pixels, canvas.Width, canvas.Height, lines);
        Assert.True(segments.Count(s => s.Horizontal) >= 12 && segments.Count(s => !s.Horizontal) >= 12, $"枠線 {segments.Count} 本");
        var grouping = OcrLayout.Group(lines, segments);
        Assert.True(grouping.HasGroups);
        Assert.True(grouping.FromFrames);
        Assert.Equal(6, grouping.Groups.Count);
        // 行 (上から) → 列 (左から) の順。各枠の中は A-1, A-2, … の順
        Assert.Equal(["A", "B", "C", "D", "E", "F"], grouping.Groups.Select(g => g.Lines[0].Compact[..1]));
        Assert.All(grouping.Groups, g => Assert.Equal(4, g.Lines.Count));
        Assert.Equal(["A-1 名前", "A-2 名前", "A-3 名前", "A-4 名前"], grouping.Groups[0].Lines.Select(l => l.Compact));

        var doc = OcrLayout.ToDocument(grouping, joinCjk: true, correct: false);
        var text = doc.ToDisplayText();
        Assert.StartsWith("【1】\r\nA-1 名前\r\nA-2 名前", text);
        Assert.Contains("【6】\r\nF-1 名前", text);
        Assert.Equal(6, doc.Lines.Count(l => l.IsHeading));
    }

    [Fact]
    public void TableWithSharedBordersIsGroupedPerCell()
    {
        // 罫線を共有する表 (2 × 2)
        var canvas = new Canvas(400, 200);
        canvas.Rect(10, 10, 390, 190);
        canvas.Fill(10, 99, 390, 100, 60);
        canvas.Fill(199, 10, 200, 190, 60);
        var lines = new List<OcrLineData>
        {
            Line("左上", 30, 30), Line("右上", 220, 30), Line("左上の2", 30, 55), Line("右上の2", 220, 55),
            Line("左下", 30, 120), Line("右下", 220, 120),
        };
        var grouping = OcrLayout.Group(lines, OcrLayout.FindSegments(canvas.Pixels, canvas.Width, canvas.Height, lines));
        Assert.Equal(4, grouping.Groups.Count);
        Assert.Equal(["左上", "右上", "左下", "右下"], grouping.Groups.Select(g => g.Lines[0].Compact));
        Assert.Equal(2, grouping.Groups[0].Lines.Count);
    }

    [Fact]
    public void PlainTextHasNoGroups()
    {
        // 枠の無い 1 段の文章 (段落が 2 つ) は分けない
        var canvas = new Canvas(400, 300);
        var lines = new List<OcrLineData>
        {
            Line("最初の段落の一行目です", 20, 20, 300), Line("最初の段落の二行目です", 20, 42, 280),
            Line("次の段落です", 20, 110, 200), Line("次の段落の二行目", 20, 132, 220),
        };
        var grouping = OcrLayout.Group(lines, OcrLayout.FindSegments(canvas.Pixels, canvas.Width, canvas.Height, lines));
        Assert.False(grouping.HasGroups);
        // 文字の下線 (行の幅くらいの線) も枠とは見なさない
        canvas.Fill(20, 60, 300, 61, 0);
        grouping = OcrLayout.Group(lines, OcrLayout.FindSegments(canvas.Pixels, canvas.Width, canvas.Height, lines));
        Assert.False(grouping.HasGroups);
    }

    [Fact]
    public void ColumnsWithoutFramesAreGrouped()
    {
        // 枠は無いが、離れて横に並んだ 2 つの列
        var lines = new List<OcrLineData>();
        for (int i = 0; i < 4; i++)
        {
            lines.Add(Line($"左{i}", 20, 20 + i * 22));
            lines.Add(Line($"右{i}", 300, 20 + i * 22));
        }
        var grouping = OcrLayout.Group(lines, []);
        Assert.True(grouping.HasGroups);
        Assert.False(grouping.FromFrames);
        Assert.Equal(["左0", "右0"], grouping.Groups.Select(g => g.Lines[0].Compact));
        Assert.All(grouping.Groups, g => Assert.Equal(4, g.Lines.Count));
    }

    // 1 文字ずつの位置つきの行 (AI OCR と同じ形: 文字列は 1 つ、空白は位置を持たない)。parts は (文字, 左端)
    private static OcrLineData Chars(double top, params (string Text, double Left)[] parts)
    {
        var spans = new List<OcrSpan>();
        foreach (var (text, left) in parts)
        {
            double x = left;
            foreach (var c in text)
            {
                if (c == ' ') { x += 6; continue; }
                spans.Add(new OcrSpan(c.ToString(), x, top, x + 10, top + 16));
                x += 11;
            }
        }
        return new OcrLineData([string.Join(" ", parts.Select(p => p.Text))], spans.Min(s => s.Left), top, spans.Max(s => s.Right), top + 16, spans);
    }

    // 名簿の画像 (1 行 × 3 列の枠。枠どうしは少し離れ、それぞれに枠線がある: 関西電力の名簿のスライドと同じ形)
    private static Canvas ThreeBoxes()
    {
        var canvas = new Canvas(660, 240);
        for (int col = 0; col < 3; col++) canvas.Rect(20 + col * 210, 20, 20 + col * 210 + 200, 220);
        return canvas;
    }

    [Fact]
    public void RowsMergedAcrossBoxesAreSplitPerBox()
    {
        // OCR が同じ高さに並んだ隣の枠の文字を 1 行にまとめた (A と B は同じ高さ、C は別の行)
        var canvas = ThreeBoxes();
        var lines = new List<OcrLineData>();
        for (int i = 0; i < 6; i++)
        {
            lines.Add(Chars(40 + i * 28, ($"A-{i + 1}-イトウ コウダイ", 32), ($"B-{i + 1}-オザワ リョウ", 242)));
            lines.Add(Chars(46 + i * 28, ($"C-{i + 1}-オギノ ショウキ", 452)));
        }
        var segments = OcrLayout.FindSegments(canvas.Pixels, canvas.Width, canvas.Height, lines);
        // 1 行にまとまった文字が枠線の上を通っても、縦の枠線を「文字の中の線」として捨てない
        Assert.True(segments.Count(s => !s.Horizontal) >= 6, $"縦の枠線 {segments.Count(s => !s.Horizontal)} 本");
        var grouping = OcrLayout.Group(lines, segments);
        Assert.True(grouping.FromFrames);
        Assert.Equal(3, grouping.Groups.Count);
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"A-{i}-イトウ コウダイ"), grouping.Groups[0].Lines.Select(l => l.Compact));
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"B-{i}-オザワ リョウ"), grouping.Groups[1].Lines.Select(l => l.Compact));
        Assert.All(grouping.Groups[2].Lines, l => Assert.StartsWith("C-", l.Compact));
        var text = OcrLayout.ToDocument(grouping, joinCjk: true, correct: false).ToDisplayText();
        Assert.StartsWith("【1】\r\nA-1-イトウ コウダイ\r\nA-2-イトウ コウダイ", text);
        Assert.Contains("【2】\r\nB-1-オザワ リョウ", text);
        // 分けた行にも文字の位置が残る (検索の印)
        Assert.All(grouping.Groups.SelectMany(g => g.Lines), l => Assert.NotNull(l.Spans));
    }

    [Fact]
    public void WindowsOcrWordsMergedAcrossBoxesAreSplit()
    {
        // Windows OCR の形 (単語ごとの文字列と位置)。1 行に 2 つの枠の単語が入った
        var canvas = ThreeBoxes();
        var lines = new List<OcrLineData>();
        for (int i = 0; i < 4; i++)
        {
            double top = 40 + i * 28;
            string[] words = [$"A-{i + 1}", "山田", $"B-{i + 1}", "佐藤"];
            double[] lefts = [32, 80, 242, 290];
            var spans = words.Select((w, k) => new OcrSpan(w, lefts[k], top, lefts[k] + 40, top + 16)).ToArray();
            lines.Add(new OcrLineData(words, 32, top, 330, top + 16, spans));
        }
        var grouping = OcrLayout.Group(lines, OcrLayout.FindSegments(canvas.Pixels, canvas.Width, canvas.Height, lines));
        Assert.Equal(2, grouping.Groups.Count);
        Assert.Equal(["A-1", "山田"], grouping.Groups[0].Lines[0].Words);
        Assert.Equal(["B-1", "佐藤"], grouping.Groups[1].Lines[0].Words);
    }

    [Fact]
    public void WideGapInsideALineSplitsColumnsWithoutFrames()
    {
        // 枠が無くても、行の中が大きく空いていれば (行の高さの 3 倍より広い) 別の列
        var lines = new List<OcrLineData>();
        for (int i = 0; i < 4; i++) lines.Add(Chars(20 + i * 22, ($"左の列{i}", 20), ($"右の列{i}", 300)));
        var grouping = OcrLayout.Group(lines, []);
        Assert.True(grouping.HasGroups);
        Assert.Equal(["左の列0", "右の列0"], grouping.Groups.Select(g => g.Lines[0].Compact));
        Assert.All(grouping.Groups, g => Assert.Equal(4, g.Lines.Count));
        // ふつうの単語の間の空き (行の高さくらい) では分けない
        Assert.Single(OcrLayout.SplitAcross([Chars(0, ("Hello", 0), ("world", 70))], []));
    }

    [Fact]
    public void LinesInAGroupAreReadTopToBottom()
    {
        // OCR が枠の中の行を下から返しても (Windows OCR で見られた)、上から順に並べる
        var canvas = ThreeBoxes();
        var lines = new List<OcrLineData>();
        for (int i = 3; i >= 0; i--)
        {
            lines.Add(Line($"Q-{i + 1} 名前", 32, 40 + i * 28));
            lines.Add(Line($"R-{i + 1} 名前", 242, 40 + i * 28));
        }
        var grouping = OcrLayout.Group(lines, OcrLayout.FindSegments(canvas.Pixels, canvas.Width, canvas.Height, lines));
        Assert.Equal(["Q-1 名前", "Q-2 名前", "Q-3 名前", "Q-4 名前"], grouping.Groups[0].Lines.Select(l => l.Compact));
        Assert.Equal(["R-1 名前", "R-2 名前", "R-3 名前", "R-4 名前"], grouping.Groups[1].Lines.Select(l => l.Compact));
    }

    [Fact]
    public void LinesWithoutPositionsAreKept()
    {
        // 文字の位置が無い行は、どこで分ければよいか分からないのでそのまま
        var canvas = ThreeBoxes();
        var lines = new List<OcrLineData> { Line("A-1 山田 B-1 佐藤", 32, 40, 300), Line("A-2 鈴木", 32, 70) };
        var split = OcrLayout.SplitAcross(lines, OcrLayout.FindSegments(canvas.Pixels, canvas.Width, canvas.Height, lines));
        Assert.Equal(2, split.Count);
    }

    [Fact]
    public void FilledBlocksAreNotFrames()
    {
        // 塗りつぶした帯 (見出しの背景など) は線ではない
        var canvas = new Canvas(400, 300);
        canvas.Fill(0, 0, 399, 40, 40);
        var lines = new List<OcrLineData> { Line("見出し", 20, 12), Line("本文", 20, 80) };
        var segments = OcrLayout.FindSegments(canvas.Pixels, canvas.Width, canvas.Height, lines);
        Assert.DoesNotContain(segments, s => s.Horizontal && s.Pos > 2 && s.Pos < 38);
    }

    [Fact]
    public void SearchIgnoresCaseWidthAndKana()
    {
        Assert.Equal([new TextMatch(0, 3), new TextMatch(8, 3)], TextSearch.Find("Abc def abc", "ABC"));
        Assert.Single(TextSearch.Find("ＧｅｔＴｅｘｔ です", "gettext"));
        Assert.Single(TextSearch.Find("カタカナ", "かたかな"));
        Assert.Empty(TextSearch.Find("abc", "  "));
        Assert.Empty(TextSearch.Find("", "a"));
        // 重ならないように数える
        Assert.Equal(2, TextSearch.Find("aaaa", "aa").Count);
    }

    [Fact]
    public void SearchMatchesMapToScreenBoxes()
    {
        var doc = OcrDocument.From([Line("Hello World", 0, 0, 110, 20), Line("World peace", 0, 30, 110, 20)], joinCjk: true, correct: false);
        var shown = doc.ToDisplayText();
        var matches = TextSearch.Find(shown, "world");
        Assert.Equal(2, matches.Count);
        var boxes = TextSearch.ToBoxes(doc, shown, matches);
        Assert.Equal(2, boxes.Count);
        // 文字の位置が無い行は、一致した文字の位置を推測せず行全体 (文字数で割った位置は、字の幅が違うとずれるため使わない)
        Assert.Equal(0, boxes[0].Left, 3);
        Assert.Equal(110, boxes[0].Right, 3);
        Assert.Equal(0, boxes[0].Top);
        Assert.Equal(0, boxes[1].Left, 3);
        Assert.Equal(30, boxes[1].Top);
        Assert.Equal(1, boxes[1].MatchIndex);
    }

    [Fact]
    public void SearchSkipsGroupHeadings()
    {
        var (canvas, lines) = Roster();
        var grouping = OcrLayout.Group(lines, OcrLayout.FindSegments(canvas.Pixels, canvas.Width, canvas.Height, lines));
        var doc = OcrLayout.ToDocument(grouping, joinCjk: true, correct: false);
        var shown = doc.ToDisplayText();
        var matches = TextSearch.Find(shown, "1");
        var boxes = TextSearch.ToBoxes(doc, shown, matches);
        // 「【1】」の見出しは画面に無いので、枠の上の印は付けない (A-1 などの「1」だけ)
        Assert.True(matches.Count > boxes.Count);
        Assert.All(boxes, b => Assert.True(b.Top >= 30));
    }
}
