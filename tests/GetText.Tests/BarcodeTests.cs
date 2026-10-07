using GetText.Plugins.Barcode;
using ZXing;
using ZXing.Common;

namespace GetText.Tests;

/// <summary>QR コード・バーコード: 作ったコードを画像にして読めるか、URL を開いてよいか。</summary>
public class BarcodeTests
{
    /// <summary>コードを白地の BGRA の画像にする (周りに余白、scale 倍)。</summary>
    private static (byte[] Bgra, int Width, int Height) Render(BarcodeFormat format, string text, int width, int height, int margin = 20)
    {
        var matrix = new MultiFormatWriter().encode(text, format, width, height,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0, [EncodeHintType.CHARACTER_SET] = "UTF-8" });
        int w = matrix.Width + margin * 2, h = matrix.Height + margin * 2;
        var bgra = new byte[w * h * 4];
        Array.Fill(bgra, (byte)255);
        for (int y = 0; y < matrix.Height; y++)
            for (int x = 0; x < matrix.Width; x++)
                if (matrix[x, y])
                {
                    int i = ((y + margin) * w + x + margin) * 4;
                    bgra[i] = bgra[i + 1] = bgra[i + 2] = 0;
                }
        return (bgra, w, h);
    }

    [Fact]
    public void ReadsQrCodeWithJapanese()
    {
        var (bgra, w, h) = Render(BarcodeFormat.QR_CODE, "こんにちは GetText", 200, 200);
        var hit = Assert.Single(BarcodeScanner.Scan(bgra, w, h));
        Assert.Equal("こんにちは GetText", hit.Text);
        Assert.Equal("QR コード", hit.FormatLabel);
        Assert.Null(hit.Url);
    }

    [Fact]
    public void ReadsEan13()
    {
        var (bgra, w, h) = Render(BarcodeFormat.EAN_13, "4901234567894", 300, 100);
        var hit = Assert.Single(BarcodeScanner.Scan(bgra, w, h));
        Assert.Equal("4901234567894", hit.Text);
        Assert.Equal("JAN / EAN", hit.FormatLabel);
    }

    [Fact]
    public void BlankImageHasNoCodes()
    {
        var blank = new byte[100 * 100 * 4];
        Array.Fill(blank, (byte)255);
        Assert.Empty(BarcodeScanner.Scan(blank, 100, 100));
        Assert.Empty(BarcodeScanner.Scan(new byte[16], 2, 2));
    }

    [Theory]
    [InlineData("https://example.com/path?q=1", true)]
    [InlineData("http://example.com", true)]
    [InlineData("  https://example.com  ", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("https://user:pw@example.com", false)]
    [InlineData("WIFI:S:home;T:WPA;P:secret;;", false)]
    [InlineData("ただの文", false)]
    public void OnlyWebUrlsCanBeOpened(string text, bool openable) =>
        Assert.Equal(openable, new BarcodeHit(text, "QR_CODE").Url != null);
}
