namespace GetText.Plugins.TranslationOverlay;

/// <summary>どの行を訳して重ねるか (日本語の行は訳さない) と、訳の覚え (同じ行を何度も訳さない)。</summary>
public static class OverlayText
{
    /// <summary>
    /// 外国語の行か: かな (ひらがな・カタカナ) を含む行は日本語とみなして訳さない。
    /// ラテン文字・ハングル・かなの無い漢字 (中国語) が文字の半分以上なら外国語。数字・記号だけの行は訳さない。
    /// </summary>
    public static bool IsForeign(string text)
    {
        int letters = 0, foreign = 0;
        foreach (var c in text)
        {
            if (c is >= '぀' and <= 'ヿ') return false; // かな
            if (!char.IsLetter(c)) continue;
            letters++;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 'À' and <= 'ɏ' // ラテン文字
                or >= 'Ͱ' and <= 'Ͽ' or >= 'Ѐ' and <= 'ӿ'           // ギリシャ・キリル
                or >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ'           // ハングル
                or >= '一' and <= '鿿')                                          // 漢字 (かなが無ければ中国語など)
                foreign++;
        }
        return letters >= 2 && foreign * 2 >= letters;
    }
}

/// <summary>訳の覚え (古いものから捨てる)。</summary>
public sealed class TranslationCache(int capacity = 1000)
{
    private readonly Dictionary<string, string> _map = [];
    private readonly Queue<string> _order = new();
    private readonly object _lock = new();

    public bool TryGet(string text, out string translation)
    {
        lock (_lock) return _map.TryGetValue(text, out translation!);
    }

    public void Add(string text, string translation)
    {
        lock (_lock)
        {
            if (_map.ContainsKey(text)) return;
            _map[text] = translation;
            _order.Enqueue(text);
            while (_order.Count > capacity) _map.Remove(_order.Dequeue());
        }
    }

    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }
}
