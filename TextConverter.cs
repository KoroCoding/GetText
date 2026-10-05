using System.Runtime.InteropServices;
using System.Text;

namespace GetText;

public enum ConvertMode
{
    None,
    HalfWidthAlnum,
    FullWidth,
    Hiragana,
    Katakana,
    JoinLines,
}

/// <summary>表示テキストの文字変換(すべてローカルで即時に行う)。</summary>
public static class TextConverter
{
    public static readonly (string Label, ConvertMode Mode)[] Modes =
    [
        ("なし", ConvertMode.None),
        ("英数字を半角に", ConvertMode.HalfWidthAlnum),
        ("全角に", ConvertMode.FullWidth),
        ("ひらがなに", ConvertMode.Hiragana),
        ("カタカナに", ConvertMode.Katakana),
        ("改行をつなげる", ConvertMode.JoinLines),
    ];

    private const uint LCMAP_HIRAGANA = 0x00100000;
    private const uint LCMAP_KATAKANA = 0x00200000;
    private const uint LCMAP_FULLWIDTH = 0x00800000;

    public static string Apply(string text, ConvertMode mode) => mode switch
    {
        _ when text.Length == 0 => text,
        ConvertMode.HalfWidthAlnum => ToHalfWidthAlnum(text),
        ConvertMode.FullWidth => LcMap(text, LCMAP_FULLWIDTH),
        ConvertMode.Hiragana => LcMap(WidenHalfKana(text), LCMAP_HIRAGANA),
        ConvertMode.Katakana => LcMap(WidenHalfKana(text), LCMAP_KATAKANA),
        ConvertMode.JoinLines => JoinLines(text),
        _ => text,
    };

    // 全角英数記号(！～～)と全角スペースだけを半角にする。カタカナはそのまま
    private static string ToHalfWidthAlnum(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c >= '！' && c <= '～') sb.Append((char)(c - 0xFEE0));
            else if (c == '　') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // 半角カナ(ｱｲｳ)だけを全角カナにする。英数字は変えない
    private static string WidenHalfKana(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && text[i] >= '｡' && text[i] <= 'ﾟ') i++;
            if (i > start)
            {
                sb.Append(LcMap(text[start..i], LCMAP_FULLWIDTH));
                continue;
            }
            sb.Append(text[i++]);
        }
        return sb.ToString();
    }

    // 段落内の改行を取り除き、段落の区切り(空行)だけを残す
    private static string JoinLines(string text)
    {
        var paragraphs = text.Replace("\r\n", "\n").Split("\n\n");
        return string.Join("\r\n\r\n", paragraphs.Select(p =>
            p.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Aggregate("", TextScript.Join)));
    }

    /// <summary>
    /// LCMapStringEx の代わり (Windows 以外): 全角にする・ひらがなにする・カタカナにする。
    /// 全角にするときは、英数記号・空白・半角カナ (濁点・半濁点は前の文字と合わせる) を全角にする。
    /// </summary>
    internal static string ManagedMap(string text, uint flags)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((flags & LCMAP_FULLWIDTH) != 0)
            {
                if (c >= '!' && c <= '~' && c != '\\') { sb.Append((char)(c + 0xFEE0)); continue; } // 「\」は Windows でも変えない
                if (c == ' ') { sb.Append('　'); continue; }
                if (c >= '｡' && c <= 'ﾟ')
                {
                    char next = i + 1 < text.Length ? text[i + 1] : '\0';
                    var (wide, used) = WidenKana(c, next);
                    sb.Append(wide);
                    i += used - 1;
                    continue;
                }
            }
            if ((flags & LCMAP_HIRAGANA) != 0 && c >= 'ァ' && c <= 'ヶ') { sb.Append((char)(c - 0x60)); continue; }
            if ((flags & LCMAP_HIRAGANA) != 0 && c == 'ヽ') { sb.Append('ゝ'); continue; }
            if ((flags & LCMAP_HIRAGANA) != 0 && c == 'ヾ') { sb.Append('ゞ'); continue; }
            if ((flags & LCMAP_KATAKANA) != 0 && c >= 'ぁ' && c <= 'ゖ') { sb.Append((char)(c + 0x60)); continue; }
            if ((flags & LCMAP_KATAKANA) != 0 && c == 'ゝ') { sb.Append('ヽ'); continue; }
            if ((flags & LCMAP_KATAKANA) != 0 && c == 'ゞ') { sb.Append('ヾ'); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // 半角カナ 1 文字 (と続く濁点・半濁点) を全角にする。used は使った文字数
    private static (char Wide, int Used) WidenKana(char c, char next)
    {
        const string half = "｡｢｣､･ｦｧｨｩｪｫｬｭｮｯｰｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝﾞﾟ";
        // 濁点・半濁点は、前の文字と合わせられないときは結合用の濁点 (U+3099・U+309A) にする (Windows と同じ)
        const string full = "。「」、・ヲァィゥェォャュョッーアイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワン\u3099\u309A";
        int k = half.IndexOf(c);
        if (k < 0) return (c, 1);
        char w = full[k];
        if (next == 'ﾞ')
        {
            if (w == 'ウ') return ('ヴ', 2);
            if (w == 'ワ') return ('ヷ', 2);
            if (w == 'ヲ') return ('ヺ', 2);
            if ("カキクケコサシスセソタチツテトハヒフヘホ".IndexOf(w) >= 0) return ((char)(w + 1), 2);
        }
        if (next == 'ﾟ' && "ハヒフヘホ".IndexOf(w) >= 0) return ((char)(w + 2), 2);
        return (w, 1);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int LCMapStringEx(string locale, uint flags, string src, int srcLen,
        [Out] char[]? dest, int destLen, IntPtr version, IntPtr reserved, IntPtr sortHandle);

    private static string LcMap(string text, uint flags)
    {
        if (!OperatingSystem.IsWindows()) return ManagedMap(text, flags);
        int len = LCMapStringEx("ja-JP", flags, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (len <= 0) return text;
        var buffer = new char[len];
        len = LCMapStringEx("ja-JP", flags, text, text.Length, buffer, len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return len > 0 ? new string(buffer, 0, len) : text;
    }
}
