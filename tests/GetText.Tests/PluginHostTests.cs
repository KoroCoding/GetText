namespace GetText.Tests;

/// <summary>拡張機能に渡すもの (読み取りの結果・開いてよい URL)。</summary>
public class PluginHostTests
{
    [Theory]
    [InlineData("https://example.com/a?b=1", true)]
    [InlineData("http://example.com", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://user:pass@example.com", false)]
    public void OnlyWebUrlsCanBeOpened(string url, bool allowed) =>
        Assert.Equal(allowed, PluginUrls.IsAllowed(new Uri(url)));

    [Fact]
    public void FrameLinesAreInScreenCoordinates()
    {
        var doc = OcrDocument.From([new OcrLineData(["Hello"], 10, 20, 60, 40)], joinCjk: true, correct: false);
        var frame = PluginFrames.From(doc, (100, 200, 80, 50), toScreen: 0.5);

        var line = Assert.Single(frame.Lines);
        Assert.Equal("Hello", line.Text);
        Assert.Equal((105.0, 210.0, 130.0, 220.0), (line.Left, line.Top, line.Right, line.Bottom));
        Assert.Equal("Hello", frame.Text);
        Assert.Null(frame.GetImage); // 画像を渡さなければ取り出せない
    }

    [Fact]
    public void FrameImageIsCopiedOnlyWhenSizeMatches()
    {
        var doc = OcrDocument.From([], joinCjk: true, correct: false);
        var pixels = new byte[2 * 3 * 4];
        var frame = PluginFrames.From(doc, (0, 0, 2, 3), image: (pixels, 2, 3));
        var image = frame.GetImage!()!.Value;
        Assert.NotSame(pixels, image.Bgra); // 拡張機能が書き換えても GetText の画像は変わらない
        Assert.Equal((2, 3), (image.Width, image.Height));

        Assert.Null(PluginFrames.From(doc, (0, 0, 2, 3), image: (new byte[5], 2, 3)).GetImage);
    }
}
