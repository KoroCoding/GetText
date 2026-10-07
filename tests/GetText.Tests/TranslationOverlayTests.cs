using GetText.Plugins.TranslationOverlay;

namespace GetText.Tests;

/// <summary>訳を重ねる: 訳す行の見分けと、訳の覚え。</summary>
public class TranslationOverlayTests
{
    [Theory]
    [InlineData("Hello world", true)]
    [InlineData("Price: $1,200 (tax included)", true)]
    [InlineData("안녕하세요", true)]
    [InlineData("欢迎使用本软件", true)]      // かなの無い漢字 (中国語)
    [InlineData("こんにちは世界", false)]
    [InlineData("GetText の使い方", false)]   // かなを含む
    [InlineData("12:30 - 13:00", false)]      // 数字・記号だけ
    [InlineData("A", false)]                  // 短すぎる
    [InlineData("", false)]
    public void DetectsForeignLines(string text, bool foreign) => Assert.Equal(foreign, OverlayText.IsForeign(text));

    [Fact]
    public void CacheKeepsRecentTranslations()
    {
        var cache = new TranslationCache(capacity: 2);
        cache.Add("a", "あ");
        cache.Add("b", "び");
        cache.Add("a", "上書きしない");
        Assert.True(cache.TryGet("a", out var a) && a == "あ");
        cache.Add("c", "し");
        Assert.False(cache.TryGet("a", out _)); // 古いものから捨てる
        Assert.True(cache.TryGet("c", out _));
        Assert.Equal(2, cache.Count);
    }
}
