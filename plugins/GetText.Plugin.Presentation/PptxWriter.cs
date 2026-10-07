using System.IO.Compression;
using System.Security;
using System.Text;

namespace GetText.Plugins.Presentation;

/// <summary>PPTX の 1 枚 (PNG の画像)。</summary>
public sealed record PptxSlide(byte[] Png, int Width, int Height);

/// <summary>
/// 画像を 1 枚ずつ貼ったスライドの PPTX を書く (最小限の OOXML。COM・Office は使わない)。
/// スライドの縦横の比は 1 枚目の画像に合わせ、各画像は比を保ったまま中央に置く。
/// </summary>
public static class PptxWriter
{
    private const long SlideWidth = 12192000; // 13.333 インチ (EMU)
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static void Write(Stream output, IReadOnlyList<PptxSlide> slides, string title)
    {
        if (slides.Count == 0) throw new ArgumentException("スライドがありません");
        long slideHeight = Math.Clamp(SlideWidth * slides[0].Height / Math.Max(1, slides[0].Width), 914400, 51206400);
        slideHeight -= slideHeight % 12700; // ポイント単位にそろえる

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        void Add(string path, string xml)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using var s = entry.Open();
            var bytes = new UTF8Encoding(false).GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" + xml);
            s.Write(bytes);
        }

        var overrides = new StringBuilder();
        void Override(string part, string type) => overrides.Append($"<Override PartName=\"/{part}\" ContentType=\"{type}\"/>");
        Override("ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml");
        Override("ppt/slideMasters/slideMaster1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml");
        Override("ppt/slideLayouts/slideLayout1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml");
        Override("ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml");
        Override("docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml");
        Override("docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml");
        for (int i = 1; i <= slides.Count; i++)
            Override($"ppt/slides/slide{i}.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml");
        Add("[Content_Types].xml",
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
            overrides + "</Types>");

        Add("_rels/.rels",
            $"<Relationships xmlns=\"{Pkg}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"ppt/presentation.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            $"<Relationship Id=\"rId3\" Type=\"{Rel}/extended-properties\" Target=\"docProps/app.xml\"/>" +
            "</Relationships>");

        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        Add("docProps/core.xml",
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
            "xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            $"<dc:title>{Esc(title)}</dc:title><dc:creator>GetText</dc:creator>" +
            $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:created><dcterms:modified xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:modified>" +
            "</cp:coreProperties>");
        Add("docProps/app.xml",
            "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\">" +
            $"<Application>GetText</Application><Slides>{slides.Count}</Slides></Properties>");

        // プレゼンテーション
        var sldIds = new StringBuilder();
        var presRels = new StringBuilder($"<Relationship Id=\"rId1\" Type=\"{Rel}/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>" +
                                         $"<Relationship Id=\"rId2\" Type=\"{Rel}/theme\" Target=\"theme/theme1.xml\"/>");
        int nextRel = 3;
        for (int i = 1; i <= slides.Count; i++, nextRel++)
        {
            sldIds.Append($"<p:sldId id=\"{255 + i}\" r:id=\"rId{nextRel}\"/>");
            presRels.Append($"<Relationship Id=\"rId{nextRel}\" Type=\"{Rel}/slide\" Target=\"slides/slide{i}.xml\"/>");
        }
        Add("ppt/presentation.xml",
            $"<p:presentation xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\" saveSubsetFonts=\"1\">" +
            "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>" +
            $"<p:sldIdLst>{sldIds}</p:sldIdLst>" +
            $"<p:sldSz cx=\"{SlideWidth}\" cy=\"{slideHeight}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>" +
            "<p:defaultTextStyle><a:defPPr><a:defRPr lang=\"ja-JP\"/></a:defPPr></p:defaultTextStyle>" +
            "</p:presentation>");
        Add("ppt/_rels/presentation.xml.rels", $"<Relationships xmlns=\"{Pkg}\">{presRels}</Relationships>");

        // マスター・レイアウト・テーマ (白紙)
        Add("ppt/slideMasters/slideMaster1.xml",
            $"<p:sldMaster xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\">" +
            "<p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg>" + EmptyTree + "</p:cSld>" +
            ColorMap +
            "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>" +
            "<p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles>" +
            "</p:sldMaster>");
        Add("ppt/slideMasters/_rels/slideMaster1.xml.rels",
            $"<Relationships xmlns=\"{Pkg}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{Rel}/theme\" Target=\"../theme/theme1.xml\"/>" +
            "</Relationships>");
        Add("ppt/slideLayouts/slideLayout1.xml",
            $"<p:sldLayout xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\" type=\"blank\" preserve=\"1\">" +
            "<p:cSld name=\"Blank\">" + EmptyTree + "</p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        Add("ppt/slideLayouts/_rels/slideLayout1.xml.rels",
            $"<Relationships xmlns=\"{Pkg}\"><Relationship Id=\"rId1\" Type=\"{Rel}/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>");
        Add("ppt/theme/theme1.xml", Theme("GetText"));

        // スライド (画像を比を保って中央に)
        for (int i = 1; i <= slides.Count; i++)
        {
            var s = slides[i - 1];
            var media = zip.CreateEntry($"ppt/media/image{i}.png", CompressionLevel.NoCompression); // PNG はもう縮んでいる
            using (var m = media.Open()) m.Write(s.Png);

            double scale = Math.Min((double)SlideWidth / s.Width, (double)slideHeight / s.Height);
            long cx = (long)(s.Width * scale), cy = (long)(s.Height * scale);
            long x = (SlideWidth - cx) / 2, y = (slideHeight - cy) / 2;
            Add($"ppt/slides/slide{i}.xml",
                $"<p:sld xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"><p:cSld><p:spTree>" +
                "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
                "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>" +
                $"<p:pic><p:nvPicPr><p:cNvPr id=\"2\" name=\"スライド {i}\" descr=\"記録したスライド {i}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>" +
                "<p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
                $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>" +
                "</p:pic></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
            Add($"ppt/slides/_rels/slide{i}.xml.rels",
                $"<Relationships xmlns=\"{Pkg}\">" +
                $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
                $"<Relationship Id=\"rId2\" Type=\"{Rel}/image\" Target=\"../media/image{i}.png\"/>" +
                "</Relationships>");
        }
    }

    private const string EmptyTree =
        "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree>";

    private const string ColorMap =
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" " +
        "accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>";

    private static string Theme(string name)
    {
        static string Fill(string color) => $"<a:solidFill><a:schemeClr val=\"{color}\"/></a:solidFill>";
        string line = "<a:ln w=\"9525\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln>";
        return
            $"<a:theme xmlns:a=\"{A}\" name=\"{Esc(name)}\"><a:themeElements>" +
            "<a:clrScheme name=\"Office\">" +
            "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
            "<a:dk2><a:srgbClr val=\"1F2937\"/></a:dk2><a:lt2><a:srgbClr val=\"F3F4F6\"/></a:lt2>" +
            "<a:accent1><a:srgbClr val=\"0F6CBD\"/></a:accent1><a:accent2><a:srgbClr val=\"E97132\"/></a:accent2>" +
            "<a:accent3><a:srgbClr val=\"196B24\"/></a:accent3><a:accent4><a:srgbClr val=\"0F9ED5\"/></a:accent4>" +
            "<a:accent5><a:srgbClr val=\"A02B93\"/></a:accent5><a:accent6><a:srgbClr val=\"4EA72E\"/></a:accent6>" +
            "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink>" +
            "</a:clrScheme>" +
            "<a:fontScheme name=\"Office\">" +
            "<a:majorFont><a:latin typeface=\"Aptos Display\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
            "<a:minorFont><a:latin typeface=\"Aptos\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont>" +
            "</a:fontScheme>" +
            "<a:fmtScheme name=\"Office\">" +
            $"<a:fillStyleLst>{Fill("phClr")}{Fill("phClr")}{Fill("phClr")}</a:fillStyleLst>" +
            $"<a:lnStyleLst>{line}{line}{line}</a:lnStyleLst>" +
            "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
            $"<a:bgFillStyleLst>{Fill("phClr")}{Fill("phClr")}{Fill("phClr")}</a:bgFillStyleLst>" +
            "</a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
    }

    private static string Esc(string text) => SecurityElement.Escape(new string(text.Where(c => c is '\t' or '\n' or '\r' || c >= ' ').ToArray())) ?? "";
}
