namespace GetText.Tests;

/// <summary>まとめた発言など長い発言の字幕を、読める長さに区切ることを確かめる。</summary>
public class SubtitleSplitTests
{
    private static readonly DateTime Start = new(2026, 10, 2, 14, 0, 0);

    private static string[] Cues(string srt) => srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void 長い日本語の発言は文の切れ目で区切り_時間を割り振る()
    {
        var doc = new MinutesDocument();
        const string text = "まず先週の売上を確認します。全体では前年より一割ほど伸びていて、特に新しい商品の売れ行きが好調でした。"
            + "一方で、在庫が足りなくなった店舗がいくつかあったので、来月からは発注の量を見直したいと考えています。皆さんのご意見を聞かせてください。";
        doc.Add(Start, new TranscriptSegment("win", 0, 30, 1, text, 1));
        var cues = Cues(doc.ToSubtitles(vtt: false));
        Assert.True(cues.Length >= 3, $"{cues.Length} 枚");
        var lines = cues.Select(c => c.Split('\n')).ToList();
        foreach (var l in lines) Assert.True(l[2].Replace("話者1: ", "").Length <= 40, l[2]);
        Assert.StartsWith("話者1: まず先週の売上を確認します。", lines[0][2]);
        Assert.DoesNotContain(":", lines[1][2]); // 続きの字幕には名前を付けない
        Assert.Equal(text, string.Concat(lines.Select(l => l[2].Replace("話者1: ", ""))));
        // 時刻は続けて、最初は 0 秒、最後は 30 秒
        Assert.StartsWith("00:00:00,000 --> ", lines[0][1]);
        Assert.EndsWith(" --> 00:00:30,000", lines[^1][1]);
        for (int i = 1; i < lines.Count; i++) Assert.Equal(lines[i - 1][1].Split(" --> ")[1], lines[i][1].Split(" --> ")[0]);
    }

    [Fact]
    public void 英語は単語の途中で切らない()
    {
        var parts = MinutesDocument.SplitForSubtitles(
            "We reviewed the quarterly numbers and the overall growth was about ten percent compared with last year, which is better than we expected. "
            + "However several stores ran out of stock, so we should adjust the order volume starting next month.", 84);
        Assert.True(parts.Count >= 3);
        Assert.All(parts, p => Assert.True(p.Length <= 84, p));
        Assert.All(parts, p => Assert.False(p.StartsWith(' ') || p.EndsWith(' ')));
        Assert.Equal("We reviewed the quarterly numbers and the overall growth was about ten percent", parts[0]);
    }

    [Fact]
    public void 区切りが無い長い文字列は文字数で切る()
    {
        var parts = MinutesDocument.SplitForSubtitles(new string('あ', 100), 40);
        Assert.Equal([40, 40, 20], parts.Select(p => p.Length));
    }

    [Fact]
    public void 最後の_1_枚がとても短ければ長さをならす()
    {
        var parts = MinutesDocument.SplitForSubtitles("今日は来週の打ち合わせの日程と、当日の資料を誰が用意するかを、皆さんと相談して決めたいと思います。はい。", 40);
        Assert.Equal(2, parts.Count);
        Assert.True(parts[1].Length >= 6, string.Join(" / ", parts));
    }

    [Fact]
    public void 短い発言でも読める時間は出す_ただし次の発言には重ねない()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 0, 1, 1, "来月からは発注の量を見直したいと考えています。", 1)); // 23 字を 1 秒で
        doc.Add(Start, new TranscriptSegment("win", 10, 12, 2, "わかりました。", 2));
        var cues = Cues(doc.ToSubtitles(vtt: false));
        Assert.Contains("00:00:00,000 --> 00:00:02,875", cues[0]); // 23 字 ÷ 8 字/秒
        doc.Add(Start, new TranscriptSegment("win", 1.5, 3, 1, "以上です。", 3));
        cues = Cues(doc.ToSubtitles(vtt: false));
        Assert.Contains("00:00:00,000 --> 00:00:01,500", cues[0]);
    }

    [Fact]
    public void 訳も原文の字幕に合わせて割り振る()
    {
        var doc = new MinutesDocument();
        var e = doc.Add(Start, new TranscriptSegment("win", 0, 20, 1,
            "We reviewed the quarterly numbers and the overall growth was about ten percent compared with last year. "
            + "However several stores ran out of stock, so we should adjust the order volume starting next month.", 1, "en"));
        e.Translation = "四半期の数字を確認したところ、前年比で約一割の伸びでした。しかし在庫切れの店舗がいくつかあったので、来月から発注量を見直すべきです。";
        var cues = Cues(doc.ToSubtitles(vtt: false));
        Assert.True(cues.Length >= 2);
        Assert.All(cues, c => Assert.Equal(4, c.Split('\n').Length)); // 番号・時刻・原文・訳
        Assert.Equal(e.Translation, string.Concat(cues.Select(c => c.Split('\n')[3])));
    }
}
