using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using GetText.Plugins.Presentation;

namespace GetText.Tests;

/// <summary>スライドの記録: 画像の指紋・止まった新しいスライドの判定・PNG・PPTX の形。</summary>
public class PresentationCaptureTests
{
    /// <summary>見本のスライド: 白地に、seed で決まる位置の黒い帯 (文字の行のような)。</summary>
    private static byte[] Slide(int seed, int width = 320, int height = 180, int extraLines = 0, int noise = 0)
    {
        var bgra = new byte[width * height * 4];
        Array.Fill(bgra, (byte)255);
        var random = new Random(seed);
        int lines = 5 + extraLines;
        for (int l = 0; l < lines; l++)
        {
            int top = 20 + l * 25, left = 20 + random.Next(40), len = 80 + random.Next(180);
            if (l >= 5) { left = 20; len = 200; } // 足した行
            for (int y = top; y < top + 10 && y < height; y++)
                for (int x = left; x < Math.Min(width, left + len); x++)
                {
                    int i = (y * width + x) * 4;
                    bgra[i] = bgra[i + 1] = bgra[i + 2] = 20;
                }
        }
        if (noise > 0)
        {
            var n = new Random(seed * 31 + noise);
            for (int k = 0; k < noise; k++)
            {
                int i = n.Next(width * height) * 4;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = (byte)n.Next(256);
            }
        }
        return bgra;
    }

    private static (ulong D, ulong P, double[] T, bool U) Hash(byte[] bgra, int width = 320, int height = 180) =>
        (ImageHash.DHash(bgra, width, height), ImageHash.PHash(bgra, width, height), ImageHash.Thumbnail(bgra, width, height), ImageHash.IsUniform(bgra, width, height));

    [Fact]
    public void HashesAreStableForTheSameSlideAndDifferForAnother()
    {
        var a = Hash(Slide(1));
        var a2 = Hash(Slide(1, noise: 40)); // 圧縮・カーソルくらいの違い
        var b = Hash(Slide(2));
        Assert.True(ImageHash.Distance(a.D, a2.D) <= 4, $"dHash {ImageHash.Distance(a.D, a2.D)}");
        Assert.True(ImageHash.Distance(a.P, a2.P) <= 6, $"pHash {ImageHash.Distance(a.P, a2.P)}");
        Assert.True(ImageHash.Distance(a.P, b.P) >= 10, $"pHash {ImageHash.Distance(a.P, b.P)}");
        Assert.False(a.U);
        // 点の乱れは升目の平均ではほとんど変わらず、1 行増えると面積で分かる
        Assert.True(ImageHash.ChangedArea(a.T, a2.T) < 0.01, $"{ImageHash.ChangedArea(a.T, a2.T)}");
        Assert.True(ImageHash.ChangedArea(a.T, Hash(Slide(1, extraLines: 1)).T) >= 0.015);
    }

    [Fact]
    public void UniformFramesAreDetected()
    {
        var black = new byte[320 * 180 * 4];
        Assert.True(ImageHash.IsUniform(black, 320, 180));
        var white = new byte[320 * 180 * 4];
        Array.Fill(white, (byte)255);
        Assert.True(ImageHash.IsUniform(white, 320, 180));
        Assert.False(ImageHash.IsUniform(Slide(3), 320, 180));
    }

    private static List<SlideDecision> Run(SlideDetector d, params byte[][] frames) =>
        frames.Select(f => { var h = Hash(f); return d.Observe(h.D, h.P, h.T, h.U); }).ToList();

    [Fact]
    public void SavesOnlyWhenTheSlideStopsAndIsNew()
    {
        var d = new SlideDetector(SlideSensitivity.Normal);
        var a = Slide(1);
        var b = Slide(2);
        var decisions = Run(d, a, a, a, a, b, b, b, a, a);
        Assert.Equal(
        [
            SlideDecision.None, SlideDecision.NewSlide, SlideDecision.None, SlideDecision.None, // A が止まったら 1 回だけ
            SlideDecision.None, SlideDecision.NewSlide, SlideDecision.None,                     // B に変わって止まった
            SlideDecision.None, SlideDecision.NewSlide,                                         // A に戻った (直前の B と違う)
        ], decisions);
        Assert.Equal(3, d.Saved);
    }

    [Fact]
    public void MovingContentIsNotSaved()
    {
        // 毎回ちがう画面 (動画・スクロールの途中) は止まらないので残さない
        var d = new SlideDetector(SlideSensitivity.Normal);
        Assert.All(Run(d, Slide(1), Slide(2), Slide(3), Slide(4), Slide(5)), x => Assert.Equal(SlideDecision.None, x));
        Assert.Equal(0, d.Saved);
    }

    [Fact]
    public void SameSlideAfterAFlickerIsNotSavedTwice()
    {
        var d = new SlideDetector(SlideSensitivity.Normal);
        var a = Slide(1);
        var a2 = Slide(1, noise: 40);
        var decisions = Run(d, a, a, Slide(9), a2, a2);
        Assert.Equal(1, d.Saved);
        Assert.Equal(SlideDecision.NewSlide, decisions[1]);
        Assert.Equal(SlideDecision.None, decisions[4]);
    }

    [Fact]
    public void LooseSavesASmallAdditionThatStrictIgnores()
    {
        // 箇条書きが 1 行増えた
        var before = Slide(1);
        var after = Slide(1, extraLines: 1);
        var loose = new SlideDetector(SlideSensitivity.Loose);
        var strict = new SlideDetector(SlideSensitivity.Strict);
        Run(loose, before, before, before, after, after, after);
        Run(strict, before, before, before, after, after, after);
        Assert.True(loose.Saved >= strict.Saved, $"loose {loose.Saved} strict {strict.Saved}");
        Assert.Equal(2, loose.Saved);
        Assert.Equal(1, strict.Saved);
    }

    [Fact]
    public void ProtectedFramesAreReportedOnceAndNotSaved()
    {
        var d = new SlideDetector();
        var black = new byte[320 * 180 * 4];
        var decisions = Run(d, black, black, black, Slide(1), Slide(1), black);
        Assert.Equal([SlideDecision.Protected, SlideDecision.None, SlideDecision.None, SlideDecision.None, SlideDecision.NewSlide, SlideDecision.Protected], decisions);
        Assert.Equal(1, d.Saved);
    }

    [Fact]
    public void PngRoundTripsPixelsWithValidCrc()
    {
        int w = 37, h = 11;
        var bgra = new byte[w * h * 4];
        new Random(5).NextBytes(bgra);
        var png = PngEncoder.Encode(bgra, w, h);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        int pos = 8;
        var idat = new MemoryStream();
        var chunks = new List<string>();
        while (pos < png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, len);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + len));
            Assert.Equal(crc, PngEncoder.Crc(data, PngEncoder.Crc(png.AsSpan(pos + 4, 4))) ^ 0xFFFFFFFFu);
            chunks.Add(type);
            if (type == "IHDR")
            {
                Assert.Equal(w, BinaryPrimitives.ReadInt32BigEndian(data));
                Assert.Equal(h, BinaryPrimitives.ReadInt32BigEndian(data[4..]));
            }
            if (type == "IDAT") idat.Write(data);
            pos += 12 + len;
        }
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks);

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        var bytes = raw.ToArray();
        Assert.Equal((1 + w * 3) * h, bytes.Length);
        var prev = new byte[w * 3];
        for (int y = 0; y < h; y++)
        {
            int row = y * (1 + w * 3);
            Assert.Equal(2, bytes[row]); // Up
            for (int i = 0; i < w * 3; i++)
            {
                byte value = (byte)(bytes[row + 1 + i] + prev[i]);
                prev[i] = value;
                int x = i / 3, c = i % 3;
                Assert.Equal(bgra[(y * w + x) * 4 + (2 - c)], value);
            }
        }
    }

    [Fact]
    public void PptxHasConsistentPartsAndRelationships()
    {
        var slides = new[] { Slide(1), Slide(2), Slide(3) }
            .Select(b => new PptxSlide(PngEncoder.Encode(b, 320, 180), 320, 180)).ToList();
        using var stream = new MemoryStream();
        PptxWriter.Write(stream, slides, "テスト <発表> & 資料");
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();

        XDocument Xml(string name)
        {
            using var s = zip.GetEntry(name)!.Open();
            return XDocument.Load(s); // 形の正しい XML か (読めなければ例外)
        }

        // すべての XML が読め、[Content_Types] に種類がある
        var types = Xml("[Content_Types].xml");
        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        var overrides = types.Root!.Elements(ct + "Override").Select(o => o.Attribute("PartName")!.Value.TrimStart('/')).ToHashSet();
        var defaults = types.Root!.Elements(ct + "Default").Select(d => d.Attribute("Extension")!.Value).ToHashSet();
        foreach (var name in names)
        {
            if (name.EndsWith(".xml") || name.EndsWith(".rels")) Xml(name);
            var ext = Path.GetExtension(name).TrimStart('.');
            Assert.True(overrides.Contains(name) || defaults.Contains(ext), $"種類がない: {name}");
        }
        foreach (var part in overrides) Assert.Contains(part, names);

        // 関係の先がすべてある
        XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";
        foreach (var rels in names.Where(n => n.EndsWith(".rels")))
        {
            var baseDir = rels == "_rels/.rels" ? "" : Path.GetDirectoryName(Path.GetDirectoryName(rels))!.Replace('\\', '/');
            foreach (var r in Xml(rels).Root!.Elements(pr + "Relationship"))
            {
                var target = r.Attribute("Target")!.Value;
                var full = Path.GetFullPath(Path.Combine("/root", baseDir, target)).Replace('\\', '/');
                var relative = full[(full.IndexOf("/root/", StringComparison.Ordinal) + 6)..];
                Assert.Contains(relative, names);
            }
        }

        // スライドは 3 枚、題名は文字の置き換えをしてある
        XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        Assert.Equal(3, Xml("ppt/presentation.xml").Descendants(p + "sldId").Count());
        Assert.Equal(3, names.Count(n => n.StartsWith("ppt/media/image")));
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        Assert.Equal("テスト <発表> & 資料", Xml("docProps/core.xml").Descendants(dc + "title").Single().Value);

        // 16:9 の画像は 16:9 のスライド全体に置く
        var size = Xml("ppt/presentation.xml").Descendants(p + "sldSz").Single();
        Assert.Equal(12192000, (long)size.Attribute("cx")!);
        Assert.InRange((long)size.Attribute("cy")!, 6858000 - 12700, 6858000);
    }

    [Fact]
    public void PngSizeReadsHeader()
    {
        var png = PngEncoder.Encode(new byte[7 * 3 * 4], 7, 3);
        Assert.Equal((7, 3), PresentationPlugin.PngSize(png));
    }
}
