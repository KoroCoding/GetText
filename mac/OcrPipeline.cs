namespace GetText;

/// <summary>Mac 版の文字の読み取りの補助 (Windows 版の OcrPipeline のうち、共有のコードが使う部分)。</summary>
internal static class OcrPipeline
{
    /// <summary>2 つの文字列の編集距離 (蓄積モードで重なりを探すのに使う)。</summary>
    internal static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
