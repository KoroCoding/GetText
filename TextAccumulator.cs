namespace GetText;

/// <summary>
/// 蓄積モード: スクロールしながら読み取った画面を重複なしでつなげ、長い文書を丸ごと集める。
/// 前回までの内容と今回の画面が重なる位置を探し、下へスクロールしたなら続きを追記、
/// 上へスクロールしたなら前に足す。
/// </summary>
public sealed class TextAccumulator
{
    private const int SearchWindow = 400; // 末尾から探す行数 (長い文書でも一定の速さにする)

    private readonly List<OcrLineInfo?> _items = []; // null は段落の区切り

    public int LineCount => _items.Count(i => i != null);

    public void Clear() => _items.Clear();

    public OcrDocument ToDocument() => OcrDocument.FromFlat(_items);

    /// <summary>今回の画面の内容を取り込む。追加した行数を返す。</summary>
    /// <param name="captureHeight">キャプチャ範囲の高さ。上下の端で切れた行は読み間違いが多いので除く。</param>
    public int Add(OcrDocument doc, double captureHeight)
    {
        var current = TrimEdges(doc.Flatten(), captureHeight);
        if (current.Count == 0) return 0;
        if (_items.Count == 0)
        {
            _items.AddRange(current);
            return Lines(current);
        }

        // 重なる位置の候補を全部調べ、完全に一致する行が最も多い位置を選ぶ
        // (番号だけ違う箇条書きなどで、1 文字違いの行に誤って合わせないため)
        int minOverlap = Math.Min(2, Lines(current));
        (int Exact, int Overlap, bool Down, int Offset) best = (0, 0, true, 0);

        int start = Math.Max(0, _items.Count - Math.Max(SearchWindow, current.Count));
        for (int offset = start; offset < _items.Count; offset++)
        {
            // 下へスクロール: 蓄積の offset 以降と今回の先頭が重なる
            int overlap = Math.Min(_items.Count - offset, current.Count);
            int exact = Match(_items, offset, current, 0, overlap);
            if (overlap >= minOverlap && Better(exact, overlap, best)) best = (exact, overlap, true, offset);
        }
        for (int overlap = Math.Min(current.Count, _items.Count); overlap >= minOverlap; overlap--)
        {
            // 上へスクロール: 今回の末尾と蓄積の先頭が重なる
            int exact = Match(current, current.Count - overlap, _items, 0, overlap);
            if (Better(exact, overlap, best)) best = (exact, overlap, false, 0);
        }

        List<OcrLineInfo?> added;
        if (best.Exact == 0)
        {
            // 重なりがない (別の場所へ移動した): 段落を区切って後ろに足す
            _items.Add(null);
            added = current;
            _items.AddRange(added);
        }
        else if (best.Down)
        {
            added = current.Skip(best.Overlap).ToList();
            _items.AddRange(added);
        }
        else
        {
            added = current.Take(current.Count - best.Overlap).ToList();
            _items.InsertRange(0, added);
        }
        return Lines(added);
    }

    private static bool Better(int exact, int overlap, (int Exact, int Overlap, bool, int) best) =>
        exact > best.Exact || (exact == best.Exact && exact > 0 && overlap > best.Overlap);

    private static int Lines(List<OcrLineInfo?> items) => items.Count(i => i != null);

    private static List<OcrLineInfo?> TrimEdges(List<OcrLineInfo?> items, double height)
    {
        const double margin = 2;
        var list = items.ToList();
        if (list.Count > 1 && list[0] is { } first && first.Top <= margin) list.RemoveAt(0);
        if (list.Count > 1 && list[^1] is { } last && last.Bottom >= height - margin) list.RemoveAt(list.Count - 1);
        // 前後に残った段落区切りは不要
        while (list.Count > 0 && list[0] == null) list.RemoveAt(0);
        while (list.Count > 0 && list[^1] == null) list.RemoveAt(list.Count - 1);
        return list;
    }

    /// <summary>
    /// a[ai..] と b[bi..] の count 個を比べ、重なりとして成り立てば完全に一致した行数を、成り立たなければ 0 を返す。
    /// OCR の揺れで 4 行に 1 行くらい少し違うのは許す。
    /// </summary>
    private static int Match(List<OcrLineInfo?> a, int ai, List<OcrLineInfo?> b, int bi, int count)
    {
        int lines = 0, exact = 0, misses = 0;
        for (int k = 0; k < count; k++)
        {
            var x = a[ai + k];
            var y = b[bi + k];
            if (x == null || y == null)
            {
                if (x != y) return 0; // 段落区切りの位置は一致しているはず
                continue;
            }
            lines++;
            var kx = Normalize(x.Text);
            var ky = Normalize(y.Text);
            if (kx == ky) exact++;
            else if (!Similar(kx, ky)) misses++;
            if (misses * 4 > count) return 0;
        }
        return lines > 0 && misses * 4 <= lines ? exact : 0;
    }

    private static string Normalize(string text) => Translator.Key(text).Replace(" ", "");

    /// <summary>OCR の揺れ程度の違いか。短い行は 1 文字違いでも別の行とみなす。</summary>
    private static bool Similar(string x, string y)
    {
        int longer = Math.Max(x.Length, y.Length);
        int allowed = longer * 15 / 100; // 7 文字未満は 0 (完全一致のみ)
        return allowed > 0 && OcrPipeline.Levenshtein(x, y) <= allowed;
    }
}
