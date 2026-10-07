using GetText.Plugins.KeywordMonitor;

namespace GetText.Tests;

/// <summary>キーワードの見張り: 言葉の照合と、何度も知らせないこと。</summary>
public class KeywordMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private static KeywordMatcher Matcher(string words, int cooldownSeconds = 30)
    {
        var m = new KeywordMatcher { Cooldown = TimeSpan.FromSeconds(cooldownSeconds) };
        m.SetKeywords(words);
        return m;
    }

    [Fact]
    public void ParsesOneKeywordPerLineSkippingCommentsAndDuplicates()
    {
        var m = Matcher("締め切り\n\n# メモ\n  至急  \nＵＲＧＥＮＴ\nurgent\n締め 切り");
        Assert.Equal(["締め切り", "至急", "ＵＲＧＥＮＴ"], m.Keywords);
    }

    [Theory]
    [InlineData("本日の締め切りは 17 時です", "締め切り")]
    [InlineData("本日の締め 切りは 17 時です", "締め切り")] // OCR が入れた空白
    [InlineData("This is URGENT", "urgent")]
    [InlineData("ｕｒｇｅｎｔ の件", "urgent")] // 全角
    [InlineData("Ｅｒｒｏｒ 404", "error")]
    public void MatchesIgnoringWidthCaseAndSpaces(string line, string keyword)
    {
        var hits = Matcher(keyword).Find([line]);
        var hit = Assert.Single(hits);
        Assert.Equal(keyword, hit.Keyword);
        Assert.Equal(line, hit.Line);
    }

    [Fact]
    public void DoesNotMatchWhenAbsent() => Assert.Empty(Matcher("締め切り").Find(["今日は晴れです", ""]));

    [Fact]
    public void NotifiesOnceWhileTheWordStaysOnScreen()
    {
        var m = Matcher("至急");
        Assert.Single(m.Observe(["至急 対応してください"], T0));
        Assert.Empty(m.Observe(["至急 対応してください"], T0.AddSeconds(1)));
        Assert.Empty(m.Observe(["至急 対応してください"], T0.AddMinutes(5))); // 出たままなら知らせない
    }

    [Fact]
    public void NotifiesAgainAfterItDisappearsAndCooldownPasses()
    {
        var m = Matcher("至急", cooldownSeconds: 30);
        Assert.Single(m.Observe(["至急"], T0));
        Assert.Empty(m.Observe(["別の画面"], T0.AddSeconds(5)));
        Assert.Empty(m.Observe(["至急"], T0.AddSeconds(10))); // 消えてすぐ出た: 待つ時間の中
        Assert.Empty(m.Observe(["別の画面"], T0.AddSeconds(20)));
        Assert.Single(m.Observe(["至急"], T0.AddSeconds(40)));
    }

    [Fact]
    public void ReportsSeveralWordsInOneFrame()
    {
        var hits = Matcher("至急\n締め切り\n会議").Observe(["至急: 締め切りの変更", "会議は 3 時から"], T0);
        Assert.Equal(["至急", "締め切り", "会議"], hits.Select(h => h.Keyword));
        Assert.Equal("会議は 3 時から", hits[2].Line);
    }

    [Fact]
    public void ResetAllowsImmediateNotification()
    {
        var m = Matcher("至急");
        Assert.Single(m.Observe(["至急"], T0));
        m.Reset();
        Assert.Single(m.Observe(["至急"], T0.AddSeconds(1)));
    }

    [Fact]
    public void ChangingKeywordsKeepsWorking()
    {
        var m = Matcher("至急");
        Assert.Single(m.Observe(["至急"], T0));
        m.SetKeywords("締め切り");
        Assert.Empty(m.Observe(["至急"], T0.AddMinutes(1)));
        Assert.Single(m.Observe(["締め切り"], T0.AddMinutes(1)));
    }
}
