using System.IO;
using System.Text;
using System.Text.Json;
using GetText.Plugins;
using GetText.Plugins.DocumentOcr;
using GetText.Plugins.Presentation;

namespace GetText.Tests;

/// <summary>文書の文字の読み取り: 書き出し (TXT・Markdown・JSON) と、Windows の PDF・画像の読み込み (本物の WinRT)。</summary>
public class DocumentOcrTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gettext-dococr-" + Guid.NewGuid().ToString("N"));

    public DocumentOcrTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static DocumentPage Page(int n, params string[] lines) =>
        new(n, 100, 50, new OcrResult(lines.Select((l, i) => new OcrResultLine(l, 1, i * 10, 90, i * 10 + 8, 0.9)).ToList(), "test"));

    [Fact]
    public void TextSeparatesPagesOnlyWhenThereAreSeveral()
    {
        Assert.Equal("一行目\n二行目\n", DocumentExport.Text([Page(1, "一行目", "二行目")]));
        Assert.Equal("--- 1 ページ ---\nA\n\n--- 2 ページ ---\nB\n", DocumentExport.Text([Page(1, "A"), Page(2, "B")]));
    }

    [Fact]
    public void MarkdownEscapesFormattingAndMarksEmptyPages()
    {
        var md = DocumentExport.Markdown("見積#1.pdf", [Page(1, "# 見出しではない", "- 箇条書きではない", "1. 番号ではない", "a*b_c"), Page(2)]);
        Assert.Contains("# 見積\\#1.pdf\n", md);
        Assert.Contains("\\# 見出しではない  \n", md);
        Assert.Contains("\\- 箇条書きではない  \n", md);
        Assert.Contains("1\\. 番号ではない  \n", md);
        Assert.Contains("a\\*b\\_c  \n", md);
        Assert.Contains("## 2 ページ\n\n(文字は見つかりませんでした)", md);
    }

    [Fact]
    public void JsonHasPagesLinesAndBoxes()
    {
        var json = DocumentExport.Json("a.pdf", [Page(1, "こんにちは", "world")], new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.FromHours(9)));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("a.pdf", root.GetProperty("source").GetString());
        var page = root.GetProperty("pages")[0];
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal("こんにちは\nworld", page.GetProperty("text").GetString());
        Assert.Equal(4, page.GetProperty("lines")[1].GetProperty("box").GetArrayLength());
        Assert.Contains("こんにちは", json); // (エスケープせずに読める形で書く)
    }

    [Fact]
    public void UniquePathAddsNumber()
    {
        Assert.Equal(Path.Combine(_dir, "a.txt"), DocumentExport.UniquePath(_dir, "a", "txt"));
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "");
        File.WriteAllText(Path.Combine(_dir, "a (2).txt"), "");
        Assert.Equal(Path.Combine(_dir, "a (3).txt"), DocumentExport.UniquePath(_dir, "a", "txt"));
    }

    [Fact]
    public void SupportedFiltersByExtension()
    {
        var docs = new WindowsDocumentService();
        var files = new[] { "a.PDF", "b.png", "c.JPG", "d.txt", "e.docx", "f" };
        Assert.Equal(["a.PDF", "b.png", "c.JPG"], DocumentOcrPlugin.Supported(files, docs));
    }

    // ───────── Windows の読み込み (本物) ─────────

    /// <summary>黒い四角を描いた 1 ページの PDF (幅 200pt・高さ 100pt。rotate を付けると回転)。</summary>
    private string MakePdf(int rotate = 0)
    {
        var content = "0 0 0 rg 20 20 60 60 re f";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Rotate {rotate} /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(_dir, $"page{rotate}.pdf");
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        return path;
    }

    private static byte Gray(CapturedImage image, int x, int y) => image.Bgra[(y * image.Width + x) * 4 + 1];

    [Fact]
    public async Task RendersPdfPageAtRequestedDpi()
    {
        var docs = new WindowsDocumentService();
        var pdf = MakePdf();
        Assert.Equal(1, await docs.GetPdfPageCountAsync(pdf, CancellationToken.None));
        var image = await docs.RenderPdfPageAsync(pdf, 0, 144, 4000, CancellationToken.None);
        // 200pt × 100pt を 144 dpi で: 400 × 200 ピクセル
        Assert.InRange(image.Width, 399, 401);
        Assert.InRange(image.Height, 199, 201);
        Assert.True(Gray(image, 100, 100) < 60, "四角の中は黒い");
        Assert.True(Gray(image, 300, 100) > 200, "外は白い");
    }

    [Fact]
    public async Task RotatedPdfPageSwapsSidesAndRespectsMaxSize()
    {
        var docs = new WindowsDocumentService();
        var image = await docs.RenderPdfPageAsync(MakePdf(90), 0, 144, 4000, CancellationToken.None);
        Assert.True(image.Height > image.Width, $"{image.Width}x{image.Height}");
        var small = await docs.RenderPdfPageAsync(MakePdf(), 0, 300, 100, CancellationToken.None);
        Assert.True(Math.Max(small.Width, small.Height) <= 101, $"{small.Width}x{small.Height}");
    }

    [Fact]
    public async Task BrokenPdfIsAFriendlyError()
    {
        var path = Path.Combine(_dir, "broken.pdf");
        File.WriteAllText(path, "これは PDF ではありません");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsDocumentService().GetPdfPageCountAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task LoadsPngAndShrinksLargeImages()
    {
        int w = 60, h = 30;
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte v = x < w / 2 ? (byte)0 : (byte)255;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = v;
                bgra[i + 3] = 255;
            }
        var path = Path.Combine(_dir, "half.png");
        File.WriteAllBytes(path, PngEncoder.Encode(bgra, w, h));
        var docs = new WindowsDocumentService();
        var image = await docs.LoadImageAsync(path, 4000, CancellationToken.None);
        Assert.Equal((60, 30), (image.Width, image.Height));
        Assert.True(Gray(image, 5, 15) < 30);
        Assert.True(Gray(image, 55, 15) > 220);
        var small = await docs.LoadImageAsync(path, 20, CancellationToken.None);
        Assert.Equal((20, 10), (small.Width, small.Height));
    }
}
