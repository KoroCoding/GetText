namespace GetText;

/// <summary>画像の中の枠線 (横線なら Pos が y で From〜To が x、縦線なら Pos が x で From〜To が y。単位はピクセル)。</summary>
public sealed record LayoutSegment(bool Horizontal, double Pos, double From, double To)
{
    public double Length => To - From;
}

/// <summary>まとまり (枠の中の行、または離れて並んだ行のかたまり)。</summary>
public sealed record OcrGroup(IReadOnlyList<OcrLineData> Lines, double Left, double Top, double Right, double Bottom, bool Framed)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

/// <summary>まとまりに分けた結果。まとまりが 2 つ以上なければ、分けずに普通に表示する。</summary>
public sealed record OcrGrouping(IReadOnlyList<OcrGroup> Groups, bool FromFrames)
{
    public bool HasGroups => Groups.Count >= 2;
    public static readonly OcrGrouping None = new([], false);
}

/// <summary>
/// 枠で区切られた表・名簿など (例: A-1〜A-6 の枠、B-1〜B-7 の枠、…) を、上から順の 1 本の文ではなく枠ごとのまとまりで読む。
/// 1. 画像から枠線 (長い細い横線・縦線) を探し (FindSegments)、各行を囲む枠に入れる
/// 2. 枠が無ければ、離れて横に並んだ行のかたまり (段組み・列) をまとまりにする
/// 3. まとまりは、行 (上から) → 列 (左から) の順に並べる
/// 枠も段組みも無ければまとまりは無し (普通の表示のまま)。
/// </summary>
public static class OcrLayout
{
    /// <summary>まとまりの見出しの行 (「【1】」)。</summary>
    public static string Heading(int number) => $"【{number}】";

    public static OcrGrouping Group(IReadOnlyList<OcrLineData> lines, IReadOnlyList<LayoutSegment> segments)
    {
        if (lines.Count < 2) return OcrGrouping.None;

        // 1. 枠に入れる
        var framed = new Dictionary<(int, int, int, int), List<OcrLineData>>();
        var boxes = new Dictionary<(int, int, int, int), (double L, double T, double R, double B)>();
        var loose = new List<OcrLineData>();
        foreach (var line in lines)
        {
            if (EnclosingBox(line, segments) is { } box)
            {
                var key = ((int)Math.Round(box.L / 4), (int)Math.Round(box.T / 4), (int)Math.Round(box.R / 4), (int)Math.Round(box.B / 4));
                if (!framed.TryGetValue(key, out var list)) framed[key] = list = [];
                list.Add(line);
                boxes[key] = box;
            }
            else loose.Add(line);
        }
        var groups = new List<OcrGroup>();
        bool fromFrames = framed.Count >= 2;
        if (fromFrames)
        {
            foreach (var (key, list) in framed)
            {
                var b = boxes[key];
                groups.Add(new OcrGroup(list, b.L, b.T, b.R, b.B, Framed: true));
            }
            // 枠の外の行は、離れ具合でまとまりにする
            groups.AddRange(Clusters(loose));
        }
        else
        {
            // 2. 枠が無い: 横に並んだかたまりがあるときだけ分ける (1 段の文章の段落は分けない)
            var clusters = Clusters(lines);
            if (!SideBySide(clusters)) return OcrGrouping.None;
            groups = clusters;
        }
        if (groups.Count < 2) return OcrGrouping.None;
        return new OcrGrouping(ReadingOrder(groups), fromFrames);
    }

    /// <summary>まとまりごとに見出し (【1】…) を付けた文書にする。</summary>
    public static OcrDocument ToDocument(OcrGrouping grouping, bool joinCjk, bool correct)
    {
        var doc = new OcrDocument();
        int n = 0;
        foreach (var group in grouping.Groups)
        {
            var part = OcrDocument.From(group.Lines, joinCjk, correct);
            if (part.IsEmpty) continue;
            double left = Left(group), top = Top(group);
            var heading = new OcrLineInfo(Heading(++n), left, top, left, top, IsHeading: true);
            part.Paragraphs[0].Insert(0, heading);
            doc.Paragraphs.AddRange(part.Paragraphs);
        }
        return doc;
    }

    // 行を囲む枠 (上下左右の線)。上下だけ (横線で区切った表の行)・左右だけ (縦線で区切った列) でもよい
    private static (double L, double T, double R, double B)? EnclosingBox(OcrLineData line, IReadOnlyList<LayoutSegment> segments)
    {
        double h = Math.Max(1, line.Bottom - line.Top);
        double cy = (line.Top + line.Bottom) / 2;
        double width = Math.Max(1, line.Right - line.Left);
        double? top = null, bottom = null, left = null, right = null;
        foreach (var s in segments)
        {
            if (s.Horizontal)
            {
                // 行の横の広がりの大部分にかかる線だけ (行の下線・小さな飾りは除く)
                double cover = Math.Min(s.To, line.Right) - Math.Max(s.From, line.Left);
                if (cover < width * 0.6) continue;
                if (s.Pos <= line.Top + h * 0.3) top = Math.Max(top ?? double.MinValue, s.Pos);
                else if (s.Pos >= line.Bottom - h * 0.3) bottom = Math.Min(bottom ?? double.MaxValue, s.Pos);
            }
            else
            {
                if (s.From > cy || s.To < cy) continue;
                if (s.Pos <= line.Left + h * 0.3) left = Math.Max(left ?? double.MinValue, s.Pos);
                else if (s.Pos >= line.Right - h * 0.3) right = Math.Min(right ?? double.MaxValue, s.Pos);
            }
        }
        if (top is double t && bottom is double b)
        {
            if (left is double l && right is double r) return (l, t, r, b);
            return (double.NegativeInfinity, t, double.PositiveInfinity, b);
        }
        if (left is double l2 && right is double r2) return (l2, double.NegativeInfinity, r2, double.PositiveInfinity);
        return null;
    }

    // 近い行どうしをつないだかたまり (上下に続く行・同じ高さで近くに並ぶ行)
    internal static List<OcrGroup> Clusters(IReadOnlyList<OcrLineData> lines)
    {
        int n = lines.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        {
            var a = lines[i];
            var b = lines[j];
            double h = Math.Max(a.Bottom - a.Top, b.Bottom - b.Top);
            double hOverlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
            double vOverlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
            double vGap = Math.Max(a.Top, b.Top) - Math.Min(a.Bottom, b.Bottom);
            double hGap = Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right);
            bool stacked = hOverlap > 0 && vGap <= h * 1.0;
            bool sameRowClose = vOverlap > 0 && hGap <= h * 1.2;
            if (stacked || sameRowClose) parent[Find(i)] = Find(j);
        }
        return Enumerable.Range(0, n)
            .GroupBy(Find)
            .Select(g =>
            {
                var members = g.OrderBy(i => i).Select(i => lines[i]).ToList();
                return new OcrGroup(members, members.Min(l => l.Left), members.Min(l => l.Top), members.Max(l => l.Right), members.Max(l => l.Bottom), Framed: false);
            })
            .ToList();
    }

    // 横に並んだかたまりがあるか (段組み・列。上下に積んだ段落だけなら false)
    private static bool SideBySide(List<OcrGroup> groups)
    {
        for (int i = 0; i < groups.Count; i++)
        for (int j = i + 1; j < groups.Count; j++)
        {
            var a = groups[i];
            var b = groups[j];
            double vOverlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
            bool apart = a.Right <= b.Left || b.Right <= a.Left;
            if (apart && vOverlap > Math.Min(a.Height, b.Height) * 0.5) return true;
        }
        return false;
    }

    /// <summary>行 (上から) → 列 (左から) の順に並べる。</summary>
    internal static List<OcrGroup> ReadingOrder(List<OcrGroup> groups)
    {
        var rows = new List<List<OcrGroup>>();
        foreach (var g in groups.OrderBy(g => Top(g)))
        {
            var row = rows.FirstOrDefault(r =>
            {
                double top = r.Min(Top), bottom = r.Max(Bottom);
                double overlap = Math.Min(bottom, Bottom(g)) - Math.Max(top, Top(g));
                return overlap >= Math.Min(Bottom(g) - Top(g), r.Min(x => Bottom(x) - Top(x))) * 0.5;
            });
            if (row == null) rows.Add(row = []);
            row.Add(g);
        }
        return rows.OrderBy(r => r.Min(Top)).SelectMany(r => r.OrderBy(Left)).ToList();
    }

    // 上下・左右が開いた枠 (横線だけ・縦線だけ) は、中の行の位置で並べる
    private static double Top(OcrGroup g) => double.IsFinite(g.Top) ? g.Top : g.Lines.Min(l => l.Top);
    private static double Bottom(OcrGroup g) => double.IsFinite(g.Bottom) ? g.Bottom : g.Lines.Max(l => l.Bottom);
    private static double Left(OcrGroup g) => double.IsFinite(g.Left) ? g.Left : g.Lines.Min(l => l.Left);

    // ───────── 画像から枠線を探す ─────────

    /// <summary>
    /// BGRA の画像から、枠線らしい長く細い横線・縦線を探す。文字の中の線 (OCR の行の範囲の中) は除く。
    /// 線の色は背景との違いで見るので、黒・灰色・色付きの線のどれでもよい。
    /// </summary>
    public static List<LayoutSegment> FindSegments(byte[] bgra, int width, int height, IReadOnlyList<OcrLineData> lines)
    {
        var result = new List<LayoutSegment>();
        if (width < 8 || height < 8 || bgra.Length < width * height * 4) return result;
        double lineHeight = lines.Count > 0 ? Median(lines.Select(l => l.Bottom - l.Top)) : 16;
        var ink = InkMask(bgra, width, height);
        int minH = (int)Math.Max(24, Math.Max(width * 0.05, lineHeight * 3));
        int minV = (int)Math.Max(20, lineHeight * 2.2);

        result.AddRange(Runs(ink, width, height, horizontal: true, minH));
        result.AddRange(Runs(ink, width, height, horizontal: false, minV));
        // 文字の中の線 (漢字の横棒・罫線のような記号) は除く
        result.RemoveAll(s => lines.Any(l => Inside(s, l)));
        return result;
    }

    private static bool Inside(LayoutSegment s, OcrLineData l)
    {
        double padX = (l.Right - l.Left) * 0.05, padY = (l.Bottom - l.Top) * 0.15;
        double mid = (s.From + s.To) / 2;
        return s.Horizontal
            ? s.Pos > l.Top + padY && s.Pos < l.Bottom - padY && mid > l.Left && mid < l.Right && s.Length <= (l.Right - l.Left) * 1.1
            : s.Pos > l.Left + padX && s.Pos < l.Right - padX && mid > l.Top && mid < l.Bottom;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    // 背景 (一番多い色) と違う画素
    private static bool[] InkMask(byte[] bgra, int width, int height)
    {
        var counts = new Dictionary<int, int>();
        int step = Math.Max(1, width * height / 40000);
        for (int i = 0; i < width * height; i += step)
        {
            int key = ((bgra[i * 4 + 2] >> 4) << 8) | ((bgra[i * 4 + 1] >> 4) << 4) | (bgra[i * 4] >> 4);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        int bg = counts.MaxBy(kv => kv.Value).Key;
        int br = ((bg >> 8) & 0xF) * 16 + 8, bgG = ((bg >> 4) & 0xF) * 16 + 8, bb = (bg & 0xF) * 16 + 8;
        var mask = new bool[width * height];
        for (int i = 0; i < width * height; i++)
        {
            int d = Math.Max(Math.Abs(bgra[i * 4 + 2] - br), Math.Max(Math.Abs(bgra[i * 4 + 1] - bgG), Math.Abs(bgra[i * 4] - bb)));
            mask[i] = d > 40;
        }
        return mask;
    }

    // 長い線の画素の並び。隣の行 (列) に続く同じ線 (太さ 2〜3 px の線) は 1 本にまとめる。太い塗り (ボタンの背景など) は除く
    private static List<LayoutSegment> Runs(bool[] ink, int width, int height, bool horizontal, int minLength)
    {
        int lanes = horizontal ? height : width, length = horizontal ? width : height;
        bool At(int lane, int pos) => horizontal ? ink[lane * width + pos] : ink[pos * width + lane];
        var raw = new List<(int Lane, int From, int To)>();
        for (int lane = 0; lane < lanes; lane++)
        {
            int start = -1, gap = 0;
            for (int pos = 0; pos <= length; pos++)
            {
                bool on = pos < length && At(lane, pos);
                if (on)
                {
                    if (start < 0) start = pos;
                    gap = 0;
                }
                else if (start >= 0 && ++gap > 2)
                {
                    int end = pos - gap;
                    if (end - start + 1 >= minLength) raw.Add((lane, start, end));
                    start = -1;
                    gap = 0;
                }
            }
        }
        var merged = new List<(double LaneSum, int Count, int First, int Last, int From, int To)>();
        foreach (var r in raw)
        {
            int i = merged.FindIndex(m => r.Lane - m.Last <= 1 && Math.Min(m.To, r.To) - Math.Max(m.From, r.From) > Math.Min(m.To - m.From, r.To - r.From) * 0.8);
            if (i < 0) merged.Add((r.Lane, 1, r.Lane, r.Lane, r.From, r.To));
            else
            {
                var m = merged[i];
                merged[i] = (m.LaneSum + r.Lane, m.Count + 1, m.First, r.Lane, Math.Min(m.From, r.From), Math.Max(m.To, r.To));
            }
        }
        return merged
            .Where(m => m.Last - m.First + 1 <= 5) // 6 px 以上の太さは線ではなく塗り
            .Select(m => new LayoutSegment(horizontal, m.LaneSum / m.Count, m.From, m.To))
            .ToList();
    }
}
