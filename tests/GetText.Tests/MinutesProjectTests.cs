using System.IO;
using System.IO.Compression;

namespace GetText.Tests;

public class MinutesProjectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gettext_project_" + Guid.NewGuid().ToString("N"));

    public MinutesProjectTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MinutesFile Sample(List<AudioPart>? audio = null, bool fromFile = false, string? sourcePath = null) => new(
        1, fromFile ? "会議.mp4" : "Zoom", fromFile, new DateTime(2026, 10, 3, 14, 0, 0), new DateTime(2026, 10, 3, 14, 30, 0), 1800,
        [new(0, "自分", "#8A8A8A"), new(1000, "田中", "#2B7BD6")],
        [new(new DateTime(2026, 10, 3, 14, 0, 5), 5, 1000, "始めます", "ja", null, 2, false, null)],
        "### 概要\n- テスト", sourcePath, audio);

    [Fact]
    public void 音声を入れて保存し_開くと音声の場所が取り出した先になる()
    {
        var wav = Path.Combine(_dir, "議事録_音声1.m4a");
        File.WriteAllBytes(wav, [1, 2, 3, 4]);
        var lost = Path.Combine(_dir, "消えた音声.m4a");
        var file = Sample([new(wav, new DateTime(2026, 10, 3, 14, 0, 0), 60), new(lost, new DateTime(2026, 10, 3, 14, 10, 0), 60)]);
        var project = Path.Combine(_dir, "会議.gettext");

        var result = MinutesProject.Save(project, file, "# 議事録");
        Assert.Equal(1, result.AudioFiles);
        Assert.Equal(1, result.MissingAudio);
        Assert.False(File.Exists(project + ".tmp"));

        // 元の音声を消しても (別の PC で開くのと同じ)、プロジェクトの中の音声で聞ける
        File.Delete(wav);
        var opened = MinutesProject.Open(project, Path.Combine(_dir, "extract"));
        var part = opened.Audio![0];
        Assert.True(File.Exists(part.Path));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(part.Path));
        Assert.StartsWith(Path.Combine(_dir, "extract"), part.Path);
        Assert.Equal(lost, opened.Audio[1].Path); // 入れられなかった音声は元の場所のまま (後から場所を選べる)
        Assert.Equal("田中", opened.Speakers[1].Name);
        Assert.Equal("### 概要\n- テスト", opened.Summary);

        // 読むだけ用の Markdown も入っている
        using var zip = ZipFile.OpenRead(project);
        Assert.NotNull(zip.GetEntry("議事録.md"));
    }

    [Fact]
    public void ファイルから作った議事録は元の音声を入れる()
    {
        var media = Path.Combine(_dir, "会議.mp4");
        File.WriteAllBytes(media, [9, 9]);
        var extracted = Path.Combine(_dir, "取り出した.m4a");
        File.WriteAllBytes(extracted, [7]);
        var project = Path.Combine(_dir, "ファイル.gettext");

        MinutesProject.Save(project, Sample(fromFile: true, sourcePath: media), "", sourceAudio: extracted);
        var opened = MinutesProject.Open(project, Path.Combine(_dir, "extract"));
        Assert.True(opened.FromFile);
        Assert.Equal([7], File.ReadAllBytes(opened.SourcePath!)); // 動画から取り出した音声のほう
    }

    [Fact]
    public void 同じ名前の音声は別の名前で入れる()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a"));
        Directory.CreateDirectory(Path.Combine(_dir, "b"));
        var a = Path.Combine(_dir, "a", "音声.m4a");
        var b = Path.Combine(_dir, "b", "音声.m4a");
        File.WriteAllBytes(a, [1]);
        File.WriteAllBytes(b, [2]);
        var project = Path.Combine(_dir, "重複.gettext");
        MinutesProject.Save(project, Sample([new(a, DateTime.Now, 1), new(b, DateTime.Now, 1)]), "");
        var opened = MinutesProject.Open(project, Path.Combine(_dir, "extract"));
        Assert.Equal([1], File.ReadAllBytes(opened.Audio![0].Path));
        Assert.Equal([2], File.ReadAllBytes(opened.Audio[1].Path));
    }

    [Fact]
    public void 保存し直したプロジェクトは取り出し直す()
    {
        var audio = Path.Combine(_dir, "音声.m4a");
        File.WriteAllBytes(audio, [1]);
        var project = Path.Combine(_dir, "更新.gettext");
        var root = Path.Combine(_dir, "extract");
        MinutesProject.Save(project, Sample([new(audio, DateTime.Now, 1)]), "");
        var first = MinutesProject.Open(project, root);
        Assert.Equal("田中", first.Speakers[1].Name);

        var renamed = first with { Speakers = [first.Speakers[0], first.Speakers[1] with { Name = "田中部長" }] };
        File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddMinutes(-5));
        MinutesProject.Save(project, renamed, ""); // 取り出した先の音声から保存し直す
        var second = MinutesProject.Open(project, root);
        Assert.Equal("田中部長", second.Speakers[1].Name);
        Assert.True(File.Exists(second.Audio![0].Path));
    }

    [Fact]
    public void フォルダーの外を指す名前は取り出さない()
    {
        var project = Path.Combine(_dir, "悪い.gettext");
        using (var zip = ZipFile.Open(project, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("minutes.json").Open())) w.Write(Sample().ToJson());
            using (var w = new StreamWriter(zip.CreateEntry("../../外.txt").Open())) w.Write("x");
        }
        var root = Path.Combine(_dir, "extract");
        MinutesProject.Open(project, root);
        Assert.False(File.Exists(Path.Combine(_dir, "外.txt")));
        Assert.False(File.Exists(Path.Combine(root, "..", "外.txt")));
    }

    [Fact]
    public void 議事録のプロジェクトでないZIPは開かない()
    {
        var project = Path.Combine(_dir, "ちがう.gettext");
        using (var zip = ZipFile.Open(project, ZipArchiveMode.Create)) zip.CreateEntry("readme.txt");
        Assert.Throws<InvalidDataException>(() => MinutesProject.Open(project, Path.Combine(_dir, "extract")));
    }
}
