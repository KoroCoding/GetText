namespace GetText.Tests;

public class MinutesMarkTests
{
    private static readonly DateTime Start = new(2026, 10, 1, 14, 0, 0);

    private static MinutesDocument Sample()
    {
        var doc = new MinutesDocument { Source = "定例会議.mp4", FromFile = true, SourcePath = @"C:\会議\定例会議.mp4" };
        doc.Add(Start, new TranscriptSegment("win", 5, 8, 1, "本日の会議を始めます。", 1));
        doc.Add(Start, new TranscriptSegment("win", 9, 12, 1, "テスト結果は金曜日までに共有します。", 2));
        doc.Add(Start, new TranscriptSegment("win", 13, 15, 1, "ほかに何かありますか。", 3));
        doc.Speaker(1).Name = "田中";
        doc.Entries[1].IsMarked = true;
        return doc;
    }

    [Fact]
    public void 星の発言は段落をまとめずに印を付けて書き出す()
    {
        var md = Sample().ToMarkdown();
        Assert.Contains("**[00:05] 田中**: 本日の会議を始めます。", md);
        Assert.Contains("**★ [00:09] 田中**: テスト結果は金曜日までに共有します。", md);
        Assert.Contains("**[00:13] 田中**: ほかに何かありますか。", md);
        Assert.Contains("★ [00:09] 田中: テスト結果は", Sample().ToPlainText());
        Assert.Contains("★ [00:09] 田中: テスト結果は", Sample().SummaryTranscript());
    }

    [Theory]
    [InlineData("md")]
    [InlineData("txt")]
    [InlineData("json")]
    public void 星は読み戻せる(string kind)
    {
        var doc = Sample();
        var file = kind switch
        {
            "md" => MinutesFile.FromText(doc.ToMarkdown()),
            "txt" => MinutesFile.FromText(doc.ToPlainText()),
            _ => MinutesFile.FromJson(doc.ToFile().ToJson()),
        };
        var loaded = new MinutesDocument();
        loaded.Load(file);
        Assert.Equal([false, true, false], loaded.Entries.Select(e => e.IsMarked));
        Assert.Equal("テスト結果は金曜日までに共有します。", loaded.Entries[1].Text);
        Assert.Equal("田中", loaded.Entries[1].Speaker.Name);
    }

    [Fact]
    public void ファイルの場所は議事録データに残る()
    {
        var loaded = new MinutesDocument();
        loaded.Load(MinutesFile.FromJson(Sample().ToFile().ToJson()));
        Assert.Equal(@"C:\会議\定例会議.mp4", loaded.SourcePath);
        Assert.Equal(TimeSpan.FromSeconds(9), loaded.Entries[1].Offset);
    }

    [Fact]
    public void 星の付いた相づちは除かない()
    {
        var doc = Sample();
        doc.Add(Start, new TranscriptSegment("win", 16, 16.5, 1, "はい。", 4));
        doc.Entries[3].IsMarked = true;
        Assert.Contains("はい。", doc.ToMarkdown(new MinutesDocument.ExportOptions(ExcludeFillers: true)));
    }
}
