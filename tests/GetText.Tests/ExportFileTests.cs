using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace GetText.Tests;

public class ExportFileTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static XDocument DocumentXml(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx));
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("_rels/.rels"));
        Assert.NotNull(zip.GetEntry("word/styles.xml"));
        using var stream = zip.GetEntry("word/document.xml")!.Open();
        return XDocument.Load(stream);
    }

    [Fact]
    public void Word_見出しと箇条書きと太字の段落になる()
    {
        var md = "# 議事録\n\n- 日時: 2026年10月3日\n\n## 要約\n\n### 決定事項\n- 来週までに共有する\n\n## 発言記録\n\n**[14:00:05] 田中**: では始めます。\n> 訳: Let's begin.\n";
        var xml = DocumentXml(DocxWriter.FromMarkdown(md));
        var paragraphs = xml.Descendants(W + "p").ToList();
        string Style(XElement p) => p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
        string Text(XElement p) => string.Concat(p.Descendants(W + "t").Select(t => t.Value));

        Assert.Equal("Title", Style(paragraphs[0]));
        Assert.Equal("議事録", Text(paragraphs[0]));
        Assert.Equal("・日時: 2026年10月3日", Text(paragraphs[1]));
        Assert.Contains(paragraphs, p => Style(p) == "Heading1" && Text(p) == "要約");
        Assert.Contains(paragraphs, p => Style(p) == "Heading2" && Text(p) == "決定事項");
        var speech = paragraphs.Single(p => Text(p).Contains("では始めます"));
        var runs = speech.Elements(W + "r").ToList();
        Assert.NotNull(runs[0].Element(W + "rPr")?.Element(W + "b")); // 「[14:00:05] 田中」は太字
        Assert.Equal("[14:00:05] 田中", runs[0].Element(W + "t")!.Value);
        Assert.Null(runs[1].Element(W + "rPr"));
        Assert.Contains(paragraphs, p => Style(p) == "Quote" && Text(p) == "訳: Let's begin.");
    }

    [Fact]
    public void Word_記号はそのまま文字になり_閉じていない太字は文字として残る()
    {
        var xml = DocumentXml(DocxWriter.FromMarkdown("A < B & C > D **太字の途中"));
        Assert.Equal("A < B & C > D **太字の途中", string.Concat(xml.Descendants(W + "t").Select(t => t.Value)));
    }

    [Fact]
    public void Word_議事録の書き出しから作れる()
    {
        var doc = new MinutesDocument { Source = "Zoom" };
        doc.Add(new DateTime(2026, 10, 3, 14, 0, 0), new TranscriptSegment("win", 1, 3, 1, "本日の会議を始めます。"));
        var xml = DocumentXml(DocxWriter.FromMarkdown(doc.ToMarkdown()));
        Assert.Contains("本日の会議を始めます。", string.Concat(xml.Descendants(W + "t").Select(t => t.Value)));
    }

    [Fact]
    public void 安全な保存_上書きしても一時ファイルが残らない()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gettext_atomic_" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "sub", "議事録.md");
            AtomicFile.WriteAllText(path, "一回目");
            AtomicFile.WriteAllText(path, "二回目");
            Assert.Equal("二回目", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
