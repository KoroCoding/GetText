namespace GetText;

public enum ScriptKind
{
    /// <summary>文字を含まない(数字・記号のみ)。</summary>
    Neutral,
    Japanese,
    Foreign,
}

/// <summary>文字種の判定ヘルパー。</summary>
public static class TextScript
{
    public static bool IsKana(char c) =>
        (c >= '぀' && c <= 'ヿ' && c != '・' && c != 'ー') ||
        (c >= 'ㇰ' && c <= 'ㇿ') ||
        (c >= 'ｦ' && c <= 'ﾝ');

    public static bool IsHan(char c) =>
        (c >= '一' && c <= '鿿') || (c >= '㐀' && c <= '䶿') || (c >= '豈' && c <= '﫿');

    /// <summary>空白を挟まずにつなげるべき文字(和文・中文など)。</summary>
    public static bool IsCjk(char c) =>
        (c >= '　' && c <= 'ヿ') ||
        (c >= 'ㇰ' && c <= 'ㇿ') ||
        (c >= '㐀' && c <= '䶿') ||
        (c >= '一' && c <= '鿿') ||
        (c >= '豈' && c <= '﫿') ||
        (c >= '＀' && c <= '￯') ||
        (c >= '가' && c <= '힯');

    public static int CountKana(string s) => s.Count(IsKana);

    public static ScriptKind Classify(string s, bool documentHasKana)
    {
        int kana = 0, han = 0, other = 0;
        foreach (var c in s)
        {
            if (IsKana(c)) kana++;
            else if (IsHan(c)) han++;
            else if (char.IsLetter(c)) other++;
        }
        if (kana + han + other == 0) return ScriptKind.Neutral;
        if (kana > 0 && kana + han >= other) return ScriptKind.Japanese;
        // 漢字だけの行は、画面の他の場所にかながあれば日本語、なければ中国語とみなす
        if (kana == 0 && han > 0 && other == 0) return documentHasKana ? ScriptKind.Japanese : ScriptKind.Foreign;
        return ScriptKind.Foreign;
    }

    public static bool IsMostlyLatin(string s)
    {
        int cjk = 0, latin = 0;
        foreach (var c in s)
        {
            if (IsKana(c) || IsHan(c)) cjk++;
            else if (c < 'ɐ' && char.IsLetter(c)) latin++;
        }
        return latin >= 8 && cjk * 20 <= latin;
    }

    /// <summary>2 つの断片を、言語に応じた区切りでつなげる。</summary>
    public static string Join(string a, string b)
    {
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        char last = a[^1], first = b[0];
        // 行末ハイフネーション: "trans-" + "lation" → "translation"
        if (last == '-' && a.Length >= 2 && char.IsLetter(a[^2]) && char.IsLower(first))
            return a[..^1] + b;
        if (IsCjk(last) || IsCjk(first)) return a + b;
        return a + " " + b;
    }
}
