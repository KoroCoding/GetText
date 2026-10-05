using System.Text;

namespace GetText;

/// <summary>
/// Windows OCR の日本語でよく出る取り違えを、前後の文字の種類から補正する。
/// 例: カタカナに挟まれた「一」→「ー」、小さいカタカナの前の「こ」→「ニ」、
///     ひらがなに挟まれた単独の「ヒ」→「と」、かなの後ろの文末「0」→「。」
/// </summary>
public static class JapaneseCorrector
{
    // カタカナの文脈で、形の似たひらがな・漢字・記号をカタカナにする
    private static readonly Dictionary<char, char> ToKatakana = new()
    {
        ['一'] = 'ー', ['-'] = 'ー', ['－'] = 'ー', ['—'] = 'ー', ['―'] = 'ー', ['ｰ'] = 'ー',
        ['口'] = 'ロ', ['力'] = 'カ', ['工'] = 'エ', ['夕'] = 'タ', ['卜'] = 'ト', ['八'] = 'ハ', ['二'] = 'ニ',
        ['こ'] = 'ニ', ['へ'] = 'ヘ', ['べ'] = 'ベ', ['ぺ'] = 'ペ', ['り'] = 'リ', ['か'] = 'カ', ['が'] = 'ガ', ['も'] = 'モ',
    };

    // ひらがなに挟まれた単独のカタカナを、形の似たひらがなにする
    private static readonly Dictionary<char, char> ToHiragana = new()
    {
        ['ヘ'] = 'へ', ['ベ'] = 'べ', ['ペ'] = 'ぺ', ['リ'] = 'り', ['カ'] = 'か', ['ガ'] = 'が',
        ['ニ'] = 'こ', ['ヒ'] = 'と', ['モ'] = 'も',
    };

    private static bool IsHiragana(char c) => c >= 'ぁ' && c <= 'ゟ';
    private static bool IsKatakanaLetter(char c) => c >= 'ァ' && c <= 'ヺ';
    private static bool IsKatakanaContext(char c) => IsKatakanaLetter(c) || c == 'ー';
    private static bool IsSmallKatakana(char c) => "ァィゥェォャュョッヮヵヶ".Contains(c);
    private static bool IsKanji(char c) => TextScript.IsHan(c);
    private static bool IsKana(char c) => IsHiragana(c) || IsKatakanaContext(c);

    public static string Correct(string line)
    {
        if (line.Length == 0) return line;
        var chars = line.ToCharArray();

        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c == ' ') continue;
            char prev = Neighbor(chars, i, -1), next = Neighbor(chars, i, +1);

            if (ToKatakana.TryGetValue(c, out var kata))
            {
                bool fix;
                if (c is '一' or '-' or '－' or '—' or '―' or 'ｰ')
                    fix = IsKatakanaLetter(prev) && !IsKanji(next) && !char.IsDigit(next); // 長音はカタカナの直後
                else if (IsHiragana(c))
                    fix = IsSmallKatakana(next) || (IsKatakanaContext(prev) && IsKatakanaContext(next))
                          || (IsKatakanaLetter(prev) && next == 'ー');
                else // 漢字
                    fix = (IsKatakanaLetter(prev) || IsKatakanaLetter(next)) && !IsKanji(prev) && !IsKanji(next)
                          && (IsKatakanaContext(prev) || IsKatakanaContext(next));
                if (fix) chars[i] = kata;
            }
            else if (ToHiragana.TryGetValue(c, out var hira)
                     && (IsHiragana(prev) || IsKanji(prev)) && (IsHiragana(next) || IsKanji(next))
                     && (IsHiragana(prev) || IsHiragana(next)))
            {
                // かな・漢字に挟まれた単独のカタカナ(例: 「かヒんヒ見当」)
                chars[i] = hira;
            }
        }

        // 文末の句点が 0 / O と読まれる
        int last = chars.Length - 1;
        while (last >= 0 && chars[last] == ' ') last--;
        if (last >= 1 && chars[last] is '0' or 'O' or 'o' or '〇' or '°' or 'ο')
        {
            char before = Neighbor(chars, last, -1);
            // 漢数字の後の「〇」は数字 (「三〇」= 30) なので句点にしない
            bool numeral = chars[last] == '〇' && "〇一二三四五六七八九十百千万".Contains(before);
            if (!numeral && (IsKana(before) || IsKanji(before))) chars[last] = '。';
        }

        return new string(chars);
    }

    private static char Neighbor(char[] chars, int i, int dir)
    {
        for (int j = i + dir; j >= 0 && j < chars.Length; j += dir)
            if (chars[j] != ' ') return chars[j];
        return '\0';
    }
}
