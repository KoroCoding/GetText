using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace GetText;

/// <summary>
/// 議事録の Markdown (見出し・箇条書き・太字・引用) から Word 文書 (.docx) を作る。
/// Word を持っていない PC でも作れるよう、Office のライブラリは使わずに中身の XML を直接書く。
/// </summary>
public static class DocxWriter
{
    private const string Font = "Yu Gothic";

    public static void Save(string path, string markdown) => AtomicFile.WriteAllBytes(path, FromMarkdown(markdown));

    public static byte[] FromMarkdown(string markdown)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", ContentTypes);
            Add(zip, "_rels/.rels", Rels);
            Add(zip, "word/_rels/document.xml.rels", DocumentRels); // 本文から見出しなどの書式 (styles.xml) を参照する
            Add(zip, "word/styles.xml", Styles);
            Add(zip, "word/document.xml", Document(markdown));
        }
        return output.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string xml)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(xml);
    }

    private static string Document(string markdown)
    {
        var body = new StringBuilder();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("# ")) body.Append(Paragraph("Title", line[2..]));
            else if (line.StartsWith("## ")) body.Append(Paragraph("Heading1", line[3..]));
            else if (line.StartsWith("### ")) body.Append(Paragraph("Heading2", line[4..]));
            else if (line.StartsWith("- ") || line.StartsWith("* ")) body.Append(Paragraph("Bullet", "・" + line[2..].TrimStart()));
            else if (line.StartsWith("  - ") || line.StartsWith("  * ")) body.Append(Paragraph("Bullet2", "・" + line[4..].TrimStart()));
            else if (line.StartsWith("> ")) body.Append(Paragraph("Quote", line[2..]));
            else body.Append(Paragraph(null, line));
        }
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
               "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
               body +
               "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>" +
               "<w:pgMar w:top=\"1418\" w:right=\"1304\" w:bottom=\"1418\" w:left=\"1304\" w:header=\"851\" w:footer=\"992\" w:gutter=\"0\"/></w:sectPr>" +
               "</w:body></w:document>";
    }

    /// <summary>1 段落。**太字** の部分は太字にする。</summary>
    private static string Paragraph(string? style, string text)
    {
        var sb = new StringBuilder("<w:p>");
        if (style != null) sb.Append($"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>");
        var parts = text.Split("**").ToList();
        if (parts.Count % 2 == 0)
        {
            // 閉じていない ** は、そのまま文字として出す
            parts[^2] += "**" + parts[^1];
            parts.RemoveAt(parts.Count - 1);
        }
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Length == 0) continue;
            sb.Append("<w:r>");
            if (i % 2 == 1) sb.Append("<w:rPr><w:b/></w:rPr>");
            sb.Append("<w:t xml:space=\"preserve\">").Append(SecurityElement.Escape(XmlSafe(parts[i]))).Append("</w:t></w:r>");
        }
        return sb.Append("</w:p>").ToString();
    }

    // XML に書けない文字 (ほかのアプリから貼った制御文字など) を除く (残すと Word が開けない)
    private static string XmlSafe(string s) =>
        s.Any(c => !System.Xml.XmlConvert.IsXmlChar(c)) ? new string(s.Where((c, i) =>
            System.Xml.XmlConvert.IsXmlChar(c) || (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            || (char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(s[i - 1]))).ToArray()) : s;

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
        "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
        "</Types>";

    private const string Rels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
        "</Relationships>";

    private const string DocumentRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    // 文字の大きさ (sz) は半ポイント単位、間隔 (spacing) は 1/20 ポイント単位
    private static readonly string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
        "<w:docDefaults><w:rPrDefault><w:rPr>" +
        $"<w:rFonts w:ascii=\"{Font}\" w:hAnsi=\"{Font}\" w:eastAsia=\"{Font}\" w:cs=\"{Font}\"/>" +
        "<w:sz w:val=\"21\"/><w:szCs w:val=\"21\"/><w:lang w:val=\"ja-JP\" w:eastAsia=\"ja-JP\"/></w:rPr></w:rPrDefault>" +
        "<w:pPrDefault><w:pPr><w:spacing w:after=\"100\" w:line=\"300\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
        Heading("Title", "Title", 36, 240, "1F3864") +
        Heading("Heading1", "heading 1", 28, 200, "1F3864") +
        Heading("Heading2", "heading 2", 24, 160, "2F5496") +
        "<w:style w:type=\"paragraph\" w:styleId=\"Bullet\"><w:name w:val=\"Bullet\"/><w:basedOn w:val=\"Normal\"/>" +
        "<w:pPr><w:spacing w:after=\"40\"/><w:ind w:left=\"360\" w:hanging=\"240\"/></w:pPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Bullet2\"><w:name w:val=\"Bullet 2\"/><w:basedOn w:val=\"Normal\"/>" +
        "<w:pPr><w:spacing w:after=\"40\"/><w:ind w:left=\"720\" w:hanging=\"240\"/></w:pPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Quote\"><w:name w:val=\"Quote\"/><w:basedOn w:val=\"Normal\"/>" +
        "<w:pPr><w:ind w:left=\"480\"/></w:pPr><w:rPr><w:color w:val=\"595959\"/><w:sz w:val=\"19\"/></w:rPr></w:style>" +
        "</w:styles>";

    private static string Heading(string id, string name, int size, int before, string color) =>
        $"<w:style w:type=\"paragraph\" w:styleId=\"{id}\"><w:name w:val=\"{name}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/>" +
        $"<w:pPr><w:keepNext/><w:spacing w:before=\"{before}\" w:after=\"120\"/><w:outlineLvl w:val=\"{(id == "Title" ? 0 : id == "Heading1" ? 0 : 1)}\"/></w:pPr>" +
        $"<w:rPr><w:b/><w:color w:val=\"{color}\"/><w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/></w:rPr></w:style>";
}
