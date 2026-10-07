using System.Diagnostics;
using System.IO;
using GetText.Plugins;
using GetText.Plugins.History;
using GetText.Plugins.KeywordMonitor;

namespace GetText.Tests;

/// <summary>
/// 性能・長く動かしたとき・壊れた部品 (故障の注入)。遅い・止まった・例外を出す拡張機能があっても、GetText が待たされない・溜め込まないこと。
/// (重い長時間の試験は手で動かす。ここは CI で毎回動かせる短いもの)
/// </summary>
public class PluginStressTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gettext-stress-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class StuckProvider : IPluginSearchProvider
    {
        public string Id => "stuck";
        public LocalizedText Title => "止まる";
        public LocalizedText? Placeholder => null;
        public Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken ct) =>
            new TaskCompletionSource<IReadOnlyList<PluginSearchResult>>().Task; // (いつまでも終わらない。打ち切りも聞かない)
    }

    private sealed class ThrowingProvider : IPluginSearchProvider
    {
        public string Id => "throws";
        public LocalizedText Title => "壊れた";
        public LocalizedText? Placeholder => null;
        public Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken ct) => throw new InvalidOperationException("broken");
    }

    private sealed class GoodProvider : IPluginSearchProvider
    {
        public string Id => "good";
        public LocalizedText Title => "よい";
        public LocalizedText? Placeholder => null;
        public Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PluginSearchResult>>([new PluginSearchResult($"見つかった {query}", null, null, query)]);
    }

    [Fact]
    public async Task StuckAndThrowingSearchProvidersDoNotBlockThePalette()
    {
        var sw = Stopwatch.StartNew();
        var found = await PluginSearch.SearchAsync("予算", 10, CancellationToken.None,
            providers: [("a", new StuckProvider()), ("b", new ThrowingProvider()), ("c", new GoodProvider())]);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"{sw.Elapsed}"); // (1 つにつき 1.5 秒で打ち切る)
        var only = Assert.Single(found);
        Assert.Equal("見つかった 予算", only.Title);
    }

    [Fact]
    public void KeywordMatcherHandlesManyFramesQuickly()
    {
        var matcher = new KeywordMatcher();
        matcher.SetKeywords(string.Join("\n", Enumerable.Range(0, 200).Select(i => $"言葉{i}")));
        var lines = Enumerable.Range(0, 40).Select(i => $"これは {i} 行目の、とても長い説明の文です。Lorem ipsum dolor sit amet {i}").ToList();
        var sw = Stopwatch.StartNew();
        for (int frame = 0; frame < 500; frame++) matcher.Observe(lines, DateTimeOffset.Now);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"{sw.Elapsed}");
    }

    [Fact]
    public void HistorySearchStaysFastWithManyEntries()
    {
        var store = new HistoryStore(_dir) { MinInterval = TimeSpan.Zero, RetentionDays = 30 };
        var t0 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(9));
        for (int i = 0; i < 20000; i++) store.Add(t0.AddSeconds(i), "app", $"記録 {i} の文。会議・予定・請求 No.{i % 97}");
        Assert.Equal(20000, store.Count);
        var sw = Stopwatch.StartNew();
        var hits = store.Search("請求 No.42", 50);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"{sw.Elapsed}");
        Assert.Equal(50, hits.Count);
        // 読み直しても同じ (ファイルから)
        Assert.Equal(20000, new HistoryStore(_dir).Count);
    }
}
