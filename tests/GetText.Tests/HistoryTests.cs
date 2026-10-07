using System.IO;
using GetText.Plugins.History;

namespace GetText.Tests;

/// <summary>読み取りの履歴: 保存する・しない (同じ文字・除くアプリ・除く言葉・間隔)、検索、保存期間、すべて消す。</summary>
public class HistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gettext-history-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 9, 0, 0, TimeSpan.FromHours(9));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private HistoryStore Store() => new(_dir) { ExcludedApps = HistoryStore.Lines(HistoryPlugin.DefaultExcludedApps) };

    [Fact]
    public void SavesChangesAndSkipsRepeats()
    {
        var s = Store();
        Assert.True(s.Add(T0, "chrome", "会議は 3 時から\n場所は 5 階"));
        Assert.False(s.Add(T0.AddSeconds(5), "chrome", "会議は 3 時から\n場所は 5 階")); // 同じ
        Assert.False(s.Add(T0.AddSeconds(6), "chrome", "会議は 3 時から  場所は 5 階")); // 空白だけ違う
        Assert.False(s.Add(T0.AddSeconds(7), "chrome", "a")); // 短すぎる
        Assert.True(s.Add(T0.AddSeconds(10), "chrome", "議題: 予算の見直し"));
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void RespectsMinimumInterval()
    {
        var s = Store();
        Assert.True(s.Add(T0, "x", "一つ目の画面"));
        Assert.False(s.Add(T0.AddSeconds(1), "x", "二つ目の画面")); // 2 秒より短い
        Assert.True(s.Add(T0.AddSeconds(3), "x", "三つ目の画面"));
    }

    [Fact]
    public void ExcludesAppsAndWords()
    {
        var s = Store();
        s.ExcludedWords = HistoryStore.Lines("パスワード\n# メモ");
        Assert.False(s.Add(T0, "1Password", "口座の番号 1234"));
        Assert.False(s.Add(T0.AddSeconds(5), "keepassxc", "大文字小文字は無視"));
        Assert.False(s.Add(T0.AddSeconds(10), "chrome", "新しいパスワードを入力"));
        Assert.True(s.Add(T0.AddSeconds(15), "chrome", "ふつうの文"));
        Assert.True(s.Add(T0.AddSeconds(20), null, "アプリが分からない文"));
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void SearchFindsNewestFirstIgnoringWidthAndSpaces()
    {
        var s = Store();
        s.Add(T0, "chrome", "Invoice ＮＯ. 1234\n合計 5,000 円");
        s.Add(T0.AddMinutes(1), "excel", "請求書 no.1234 の修正");
        s.Add(T0.AddMinutes(2), "word", "関係ない文");

        var hits = s.Search("no. 1234");
        Assert.Equal(["excel", "chrome"], hits.Select(h => h.Entry.App));
        Assert.Equal("Invoice ＮＯ. 1234", hits[1].Line);
        Assert.Single(s.Search("請求書 修正")); // すべての言葉を含む回
        Assert.Empty(s.Search("   "));
    }

    [Fact]
    public void PersistsAndReloads()
    {
        Store().Add(T0, "chrome", "保存して読み直す");
        var again = Store();
        Assert.Equal(1, again.Count);
        Assert.Single(again.Search("読み直す"));
    }

    [Fact]
    public void PruneRemovesDaysOlderThanRetention()
    {
        var s = Store();
        s.RetentionDays = 7;
        s.Add(T0.AddDays(-10), "a", "10 日前の文");
        s.Add(T0.AddDays(-6), "a", "6 日前の文");
        s.Add(T0, "a", "今日の文");
        Assert.Equal(1, s.Prune(T0));
        Assert.Equal(2, s.Count);
        Assert.Empty(s.Search("10日前"));
    }

    [Fact]
    public void ClearDeletesEverything()
    {
        var s = Store();
        s.Add(T0, "a", "消す文");
        s.Clear();
        Assert.Equal(0, s.Count);
        Assert.Equal(0, Store().Count);
        Assert.True(s.Add(T0.AddSeconds(1), "a", "消す文")); // 消した後は同じ文も保存できる
    }

    [Fact]
    public void SkipsBrokenLines()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "2026-10-07.jsonl"), "{\"t\":\"2026-10-07T09:00:00+09:00\",\"app\":\"a\",\"text\":\"よい行\"}\n{\"t\":\"2026-10-07T09:00:0");
        Assert.Equal(1, Store().Count);
    }
}
