namespace GetText.Tests;

public class MinutesFileTests
{
    private static readonly DateTime Start = new(2026, 9, 29, 14, 0, 0);

    private static MinutesDocument Sample()
    {
        var doc = new MinutesDocument { Source = "Zoom ミーティング" };
        doc.Add(Start, new TranscriptSegment("win", 5, 8, 1, "本日の会議を始めます。", 1));
        doc.Add(Start, new TranscriptSegment("mic", 9, 12, 0, "はい、完成しました。", 2));
        doc.Add(Start, new TranscriptSegment("win", 14, 17, 2, "Could you share the results?", 3, "en"));
        doc.Entries[2].Translation = "結果を共有してもらえますか？";
        doc.Speaker(1).Name = "田中";
        return doc;
    }

    [Fact]
    public void 議事録データで保存して読み戻せる()
    {
        var json = Sample().ToFile().ToJson();
        var doc = new MinutesDocument();
        doc.Load(MinutesFile.FromJson(json));
        Assert.Equal(3, doc.Entries.Count);
        Assert.Equal(["本日の会議を始めます。", "はい、完成しました。", "Could you share the results?"], doc.Entries.Select(e => e.Text));
        Assert.Equal("田中", doc.Entries[0].Speaker.Name);
        Assert.Equal(0, doc.Entries[1].Speaker.Id); // 自分はそのまま
        Assert.True(doc.Entries[0].Speaker.Id >= 1000); // 声の聞き分けの番号と重ならない
        Assert.Equal("結果を共有してもらえますか？", doc.Entries[2].Translation);
        Assert.Equal("14:00:05", doc.Entries[0].TimeText);
        Assert.Equal(TimeSpan.FromSeconds(3), doc.Entries[0].Duration);
    }

    [Fact]
    public void 日付をまたいだ会議を読み戻しても並びが崩れない()
    {
        var late = new DateTime(2026, 9, 29, 23, 59, 50);
        var doc = new MinutesDocument { Source = "Zoom" };
        doc.Add(late, new TranscriptSegment("win", 0, 3, 1, "日付が変わる前の発言です。", 1));
        doc.Add(late, new TranscriptSegment("win", 20, 23, 2, "日付が変わった後の発言です。", 2));
        var file = MinutesFile.FromText(doc.ToMarkdown());
        Assert.Equal(["日付が変わる前の発言です。", "日付が変わった後の発言です。"], file.Entries.OrderBy(e => e.Time).Select(e => e.Text));
        Assert.True(file.Entries[1].OffsetSeconds > 0);
        Assert.True(file.EndedAt > file.StartedAt);
    }

    [Fact]
    public void ファイルから作った議事録を読み戻すと_メモの時刻の基準がある()
    {
        var doc = new MinutesDocument { Source = "会議.mp4", FromFile = true, Duration = TimeSpan.FromMinutes(10) };
        doc.Add(Start, new TranscriptSegment("win", 5, 8, 1, "始めます。", 1));
        var file = MinutesFile.FromText(doc.ToMarkdown());
        Assert.True(file.FromFile);
        Assert.NotNull(file.StartedAt); // 無いと「ファイルの文字起こしが始まってから…」でメモを足せない
        Assert.Equal(5, file.Entries[0].OffsetSeconds);
    }

    [Fact]
    public void Markdown_の議事録を読める()
    {
        var md = Sample().ToMarkdown();
        var file = MinutesFile.FromText(md);
        Assert.Equal("Zoom ミーティング", file.Source);
        Assert.False(file.FromFile);
        var doc = new MinutesDocument();
        doc.Load(file);
        Assert.Equal(3, doc.Entries.Count);
        Assert.Equal("田中", doc.Entries[0].Speaker.Name);
        Assert.Equal("自分", doc.Entries[1].Speaker.Name);
        Assert.Equal("en", doc.Entries[2].Language);
        Assert.Equal("結果を共有してもらえますか？", doc.Entries[2].Translation);
        Assert.Equal(new DateTime(2026, 9, 29, 14, 0, 14), doc.Entries[2].Time);
        // もう一度書き出しても同じ発言になる
        Assert.Contains("**[14:00:05] 田中**: 本日の会議を始めます。", doc.ToMarkdown());
    }

    [Fact]
    public void テキストや時刻なしの議事録も読める()
    {
        var doc = Sample();
        var txt = MinutesFile.FromText(doc.ToPlainText());
        Assert.Equal(3, txt.Entries.Count);
        Assert.Equal("結果を共有してもらえますか？", txt.Entries[2].Translation);
        var noTime = MinutesFile.FromText(doc.ToMarkdown(new MinutesDocument.ExportOptions(IncludeTime: false)));
        Assert.Equal(["田中", "自分", "話者2"], noTime.Entries.Select(e => noTime.Speakers.Single(s => s.Id == e.Speaker).Name));
    }

    [Fact]
    public void ファイルから作った議事録は先頭からの時間で読む()
    {
        var src = new MinutesDocument { FromFile = true, Source = "会議.mp4", Duration = TimeSpan.FromSeconds(3725) };
        src.Add(Start, new TranscriptSegment("win", 65, 70, 1, "始めます。", 1));
        src.Add(Start, new TranscriptSegment("win", 3700, 3710, 2, "終わります。", 2));
        var doc = new MinutesDocument();
        doc.Load(MinutesFile.FromText(src.ToMarkdown()));
        Assert.True(doc.FromFile);
        Assert.Equal("会議.mp4", doc.Source);
        Assert.Equal(TimeSpan.FromSeconds(3725), doc.Duration);
        Assert.Equal("01:05", doc.Entries[0].TimeText);
        Assert.Equal("1:01:40", doc.Entries[1].TimeText);
    }

    [Fact]
    public void 読み込んだ議事録の話者を直して保存し直せる()
    {
        var doc = new MinutesDocument();
        doc.Load(MinutesFile.FromText(Sample().ToMarkdown()));
        var other = doc.Speakers.Single(s => s.Name == "話者2");
        other.Name = "田中"; // 別の PC で 2 人に分かれていた同じ人
        var (kept, _) = doc.MergeSpeakers(other, doc.SameNameAs(other)!);
        Assert.Equal(2, doc.Speakers.Count);
        Assert.All(doc.Entries.Where(e => e.Speaker.Id != 0), e => Assert.Equal(kept, e.Speaker));
        Assert.Contains("田中", MinutesFile.FromJson(doc.ToFile().ToJson()).Speakers.Select(s => s.Name));
    }

    [Fact]
    public void 議事録でないファイルは読まない() =>
        Assert.Throws<System.IO.InvalidDataException>(() => MinutesFile.FromText("ただのメモ\n買い物リスト"));
}
