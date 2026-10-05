namespace GetText.Tests;

public class MinutesSummaryTests
{
    private static readonly DateTime Start = new(2026, 10, 1, 14, 0, 0);

    private const string Summary = "### 概要\n定例会議で進捗を確認した。\n\n### 決定事項\n- 次回に検証結果を確認する\n\n" +
                                   "### やること\n- 田中: 資料を送る (担当: 田中、期限: 金曜日)\n\n### 主な論点\n- なし";

    private static MinutesDocument Sample()
    {
        var doc = new MinutesDocument { Source = "Zoom ミーティング" };
        doc.Add(Start, new TranscriptSegment("win", 5, 8, 1, "本日の会議を始めます。", 1));
        doc.Add(Start, new TranscriptSegment("mic", 9, 9.5, 0, "はい。", 2));
        doc.Add(Start, new TranscriptSegment("mic", 10, 12, 0, "完成しました。", 3));
        doc.Add(Start, new TranscriptSegment("win", 14, 17, 2, "Could you share the results?", 4, "en"));
        doc.Entries[3].Translation = "結果を共有してもらえますか？";
        doc.Speaker(1).Name = "田中";
        doc.Summary = Summary;
        return doc;
    }

    [Fact]
    public void 要約は保存の先頭に入る()
    {
        var md = Sample().ToMarkdown();
        Assert.True(md.IndexOf("## 要約", StringComparison.Ordinal) < md.IndexOf("## 発言記録", StringComparison.Ordinal));
        Assert.Contains("### 決定事項", md);
        var txt = Sample().ToPlainText();
        Assert.Contains("【要約】", txt);
        Assert.Contains("【決定事項】", txt);
        Assert.Contains("【発言記録】", txt);
    }

    [Theory]
    [InlineData("md")]
    [InlineData("txt")]
    [InlineData("json")]
    public void 要約も読み戻せて発言と混ざらない(string kind)
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
        Assert.Equal(Summary.Replace("\r\n", "\n"), loaded.Summary!.Replace("\r\n", "\n"));
        // 要約の「田中: 資料を送る」を発言として読まない
        Assert.DoesNotContain(loaded.Entries, e => e.Text.Contains("資料を送る"));
        Assert.Equal("本日の会議を始めます。", loaded.Entries[0].Text);
    }

    [Fact]
    public void 要約のない議事録は今までどおり()
    {
        var doc = Sample();
        doc.Summary = null;
        Assert.DoesNotContain("要約", doc.ToMarkdown());
        Assert.DoesNotContain("【発言記録】", doc.ToPlainText());
        Assert.Null(MinutesFile.FromText(doc.ToMarkdown()).Summary);
    }

    [Fact]
    public void 字幕_SRT_は発言ごとに時刻と話者を出す()
    {
        var srt = Sample().ToSubtitles(vtt: false);
        var blocks = srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, blocks.Length);
        // 録音では記録を始めてからの時間
        Assert.Equal("1\n00:00:05,000 --> 00:00:08,000\n田中: 本日の会議を始めます。", blocks[0]);
        // 短い発言も 1.5 秒は出すが、次の発言には重ならない
        Assert.Equal("2\n00:00:09,000 --> 00:00:10,000\n自分: はい。", blocks[1]);
        // 日本語以外は訳を 2 行目に
        Assert.Equal("4\n00:00:14,000 --> 00:00:17,000\n話者2: Could you share the results?\n結果を共有してもらえますか？", blocks[3]);
    }

    [Fact]
    public void 字幕_VTT_と相づちを除く設定()
    {
        var vtt = Sample().ToSubtitles(vtt: true, new MinutesDocument.ExportOptions(ExcludeFillers: true, IncludeTranslation: false));
        Assert.StartsWith("WEBVTT\n\n00:00:05.000 --> 00:00:08.000\n田中: 本日の会議を始めます。", vtt);
        Assert.DoesNotContain("はい。", vtt);
        Assert.DoesNotContain("結果を共有", vtt);
    }

    [Fact]
    public void 要約に渡す文字起こしは相づちを除いて話者ごとにまとめる()
    {
        var text = Sample().SummaryTranscript();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("[14:00:05] 田中: 本日の会議を始めます。", lines[0]);
        Assert.Equal("[14:00:10] 自分: 完成しました。", lines[1]);
    }
}
