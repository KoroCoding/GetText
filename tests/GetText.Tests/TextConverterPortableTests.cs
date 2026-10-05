using System.Reflection;

namespace GetText.Tests;

/// <summary>Mac 版で使う文字変換 (LCMapStringEx の代わり) が、Windows と同じ結果になることを確かめる。</summary>
public class TextConverterPortableTests
{
    private static string Windows(string text, uint flags) =>
        (string)typeof(TextConverter).GetMethod("LcMap", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [text, flags])!;

    private const uint Hiragana = 0x00100000, Katakana = 0x00200000, FullWidth = 0x00800000;

    public static IEnumerable<object[]> Cases()
    {
        // 英数記号・空白・ひらがな・カタカナ・半角カナ (濁点・半濁点つき)・漢字・記号
        var all = string.Concat(Enumerable.Range(0x20, 0x5F).Select(c => (char)c))
                  + string.Concat(Enumerable.Range(0x3041, 0x56).Select(c => (char)c))
                  + string.Concat(Enumerable.Range(0x30A1, 0x5A).Select(c => (char)c))
                  + string.Concat(Enumerable.Range(0xFF61, 0x3F).Select(c => (char)c))
                  + "ｶﾞｷﾞｸﾞｹﾞｺﾞｻﾞｼﾞｽﾞｾﾞｿﾞﾀﾞﾁﾞﾂﾞﾃﾞﾄﾞﾊﾞﾋﾞﾌﾞﾍﾞﾎﾞﾊﾟﾋﾟﾌﾟﾍﾟﾎﾟｳﾞﾜﾞｱﾞ 漢字。、「」ゝゞヽヾー〜";
        foreach (var flags in new[] { FullWidth, Hiragana, Katakana })
            foreach (var chunk in all.Chunk(16))
                yield return [new string(chunk), flags];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Windows_と同じ結果になる(string text, uint flags)
    {
        var expected = Windows(text, flags);
        var actual = TextConverter.ManagedMap(text, flags);
        Assert.True(expected == actual,
            $"{flags:X}: 「{text}」 Windows「{expected}」 ({string.Join(" ", expected.Select(c => ((int)c).ToString("X4")))}) / 代わり「{actual}」 ({string.Join(" ", actual.Select(c => ((int)c).ToString("X4")))})");
    }
}
