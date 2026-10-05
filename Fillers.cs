using System.Text;

namespace GetText;

/// <summary>
/// 相づち・言いよどみだけの発言 (「はい」「なるほど」「えーと」など) の判定。
/// 議事録では内容のない発言として、画面と書き出しから除けるようにする (日本語と英語)。
/// </summary>
public static class Fillers
{
    // 伸ばす音 (ー〜) と末尾の「っ」を除いた形で比べる (「えーっと」「うーん」「へー」なども同じとみなす)
    private static readonly HashSet<string> Words =
    [
        "はい", "ええ", "え", "うん", "ん", "あ", "ああ", "お", "おお", "はあ", "ほう", "ほお", "へ", "へえ", "ふん", "ふうん",
        "うむ", "ふむ", "ね", "ねえ", "えと", "えっと", "あの", "その", "まあ", "ま", "なんか",
        "なるほど", "なるほどね", "なるほどです", "そう", "そうね", "そうそう", "そうですね", "そうですか", "そうです",
        "そうなんですね", "そうなんですか", "そうなんだ", "そうなんです", "ですね", "確かに", "たしかに", "ほんとに", "本当に",
        "yeah", "yes", "yep", "uh", "um", "hmm", "mm", "ok", "okay", "right", "sure", "oh", "ah", "mhm", "wow",
        "uhhuh", "mmhmm", "isee", "gotit", "really", "exactly", "ohisee", "okayokay", "yeahyeah", "allright", "alright",
    ];

    // 続けて繰り返されることが多い語 (「はいはい」「うんうんうん」)
    private static readonly string[] Repeatable = ["はい", "うん", "ええ", "そう", "へえ", "ああ"];

    private const string Separators = "、。，．,.!！?？…・　 \t「」『』()（）";

    /// <summary>発言が相づち・言いよどみだけでできているか。</summary>
    public static bool IsFiller(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.ToLowerInvariant().Split(Separators.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        // 「I see」「Got it」「Uh-huh」のように区切りを含む相づち
        if (Words.Contains(Normalize(string.Concat(parts)))) return true;
        int length = 0;
        foreach (var raw in parts)
        {
            var word = Normalize(raw);
            length += word.Length;
            if (word.Length == 0) continue;
            if (!Words.Contains(word) && !IsRepetition(word)) return false;
        }
        // 相づちが長く続くのは不自然なので、長いものは内容のある発言として残す
        return length <= 16;
    }

    private static string Normalize(string word)
    {
        var sb = new StringBuilder(word.Length);
        foreach (char c in word)
            if (c is not ('ー' or '〜' or '～' or '-' or '~')) sb.Append(c);
        while (sb.Length > 1 && sb[^1] is 'っ' or 'ッ') sb.Length--;
        return sb.ToString();
    }

    private static bool IsRepetition(string word)
    {
        foreach (var unit in Repeatable)
        {
            if (word.Length <= unit.Length || word.Length % unit.Length != 0) continue;
            bool all = true;
            for (int i = 0; i < word.Length && all; i += unit.Length)
                all = string.CompareOrdinal(word, i, unit, 0, unit.Length) == 0;
            if (all) return true;
        }
        return false;
    }
}
