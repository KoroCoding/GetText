namespace GetText;

/// <summary>見つかった所 (表示の文字の位置)。</summary>
public readonly record struct TextMatch(int Start, int Length);

/// <summary>見つかった所の、画面の上の位置 (読み取った画像のピクセル)。</summary>
public readonly record struct MatchBox(double Left, double Top, double Right, double Bottom, int MatchIndex);

/// <summary>
/// 読み取った文字の検索 (Ctrl+F)。大文字・小文字、全角・半角、ひらがな・カタカナの違いは無視する。
/// 見つかった所を、読み取り枠の上の位置 (行の範囲の中を文字数で割った位置) にもする。
/// </summary>
public static class TextSearch
{
    public static List<TextMatch> Find(string text, string query)
    {
        var result = new List<TextMatch>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query)) return result;
        string t = Fold(text), q = Fold(query.Trim());
        if (q.Length == 0) return result;
        for (int i = t.IndexOf(q, StringComparison.Ordinal); i >= 0; i = t.IndexOf(q, i + q.Length, StringComparison.Ordinal))
            result.Add(new TextMatch(i, q.Length));
        return result;
    }

    /// <summary>1 文字ずつ比べる形にする (文字数は変えない: 見つけた位置がそのまま元の文字の位置になる)。</summary>
    public static string Fold(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c is >= '！' and <= '～') c = (char)(c - 0xFEE0); // 全角の英数字・記号 → 半角
            else if (c == '　') c = ' ';
            else if (c is >= 'ァ' and <= 'ヶ') c = (char)(c - 0x60); // カタカナ → ひらがな
            chars[i] = char.ToLowerInvariant(c);
        }
        return new string(chars);
    }

    /// <summary>
    /// 表示の文字 (shown。文字変換した後のもの) の中の見つかった所を、読み取った画像の上の位置にする。
    /// 表示の行と文書の行は改行の位置で対応させる。行の中は、OCR エンジンが返した文字・単語の位置 (Spans) を使い、
    /// 空白を除いた文字の順で一致した部分に重なるものを合わせた範囲にする (AI OCR は文字ごと、Windows OCR は単語ごとの精度)。
    /// 位置が無いエンジンや、表示の文字と数が合わない行は、一致した文字の位置を推測せず、行全体を返す。
    /// 見出し (【1】) の行は画面に無いので除く。
    /// </summary>
    public static List<MatchBox> ToBoxes(OcrDocument doc, string shown, IReadOnlyList<TextMatch> matches)
    {
        var boxes = new List<MatchBox>();
        if (matches.Count == 0) return boxes;
        var lines = doc.Lines.ToList();
        // 表示の各行の始まりの位置と、対応する文書の行
        var spans = new List<(int Start, string Row, OcrLineInfo Line)>();
        int pos = 0, index = 0;
        foreach (var part in shown.Split('\n'))
        {
            string row = part.TrimEnd('\r');
            if (row.Length > 0 && index < lines.Count) spans.Add((pos, row, lines[index++]));
            pos += part.Length + 1;
        }
        for (int m = 0; m < matches.Count; m++)
        {
            var match = matches[m];
            int end = match.Start + match.Length;
            foreach (var (start, row, line) in spans)
            {
                int from = Math.Max(match.Start, start), to = Math.Min(end, start + row.Length);
                if (to <= from || line.IsHeading) continue;
                if (OcrSpans.IfMatching(line.Spans, row) is { } parts && GlyphRange(row, from - start, to - start) is var (g0, g1) && g1 > g0)
                {
                    // 一致した文字に重なる文字・単語の位置を合わせる
                    double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
                    int g = 0;
                    foreach (var part in parts)
                    {
                        int next = g + part.Glyphs;
                        if (next > g0 && g < g1)
                        {
                            l = Math.Min(l, part.Left); t = Math.Min(t, part.Top);
                            r = Math.Max(r, part.Right); b = Math.Max(b, part.Bottom);
                        }
                        g = next;
                    }
                    if (r > l && b > t)
                    {
                        boxes.Add(new MatchBox(l, t, r, b, m));
                        continue;
                    }
                }
                else if (GlyphRange(row, from - start, to - start) is var (h0, h1) && h1 <= h0)
                    continue; // 空白だけに一致
                boxes.Add(new MatchBox(line.Left, line.Top, line.Right, line.Bottom, m)); // 位置が分からない: 行全体
            }
        }
        return boxes;
    }

    /// <summary>行の中の文字の範囲 [from, to) を、空白を除いた文字の番号の範囲にする。</summary>
    private static (int, int) GlyphRange(string row, int from, int to)
    {
        int g = 0, g0 = -1, g1 = 0;
        for (int i = 0; i < row.Length; i++)
        {
            bool pair = char.IsHighSurrogate(row[i]) && i + 1 < row.Length && char.IsLowSurrogate(row[i + 1]);
            if (!char.IsWhiteSpace(row[i]))
            {
                if (i >= from && i < to)
                {
                    if (g0 < 0) g0 = g;
                    g1 = g + 1;
                }
                g++;
            }
            if (pair) i++;
        }
        return g0 < 0 ? (0, 0) : (g0, g1);
    }
}