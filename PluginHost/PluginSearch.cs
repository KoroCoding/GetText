using GetText.Plugins;

namespace GetText;

/// <summary>
/// コマンドの一覧 (Ctrl+K / ⌘K) で、拡張機能の検索 (読み取りの履歴など) も探す (Windows 版・Mac 版で共有)。
/// 見つかったものはコマンドの形にして、一覧のコマンドの下に出す。押すと文字をコピーする。
/// 遅い・壊れた拡張機能があっても一覧は止めない (1 つにつき 1.5 秒で待つのをやめ、例外はログに書く)。
/// </summary>
public static class PluginSearch
{
    public const int MinQueryLength = 2;

    public static IReadOnlyList<(string PluginId, IPluginSearchProvider Provider)> Providers =>
        PluginRuntime.Plugins
            .Where(p => p.Loaded && p.Context != null)
            .SelectMany(p => p.Context!.SearchProviders.Select(s => (p.Record.Id, s)))
            .ToList();

    public static bool HasProviders => PluginRuntime.Plugins.Any(p => p.Loaded && p.Context is { SearchProviders.Count: > 0 });

    public static async Task<List<AppCommand>> SearchAsync(string query, int max, CancellationToken ct,
        IReadOnlyList<(string PluginId, IPluginSearchProvider Provider)>? providers = null, Action<string>? copied = null)
    {
        query = query.Trim();
        if (query.Length < MinQueryLength) return [];
        var list = providers ?? Providers;
        var tasks = list.Select(async p =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(1.5));
            try
            {
                var search = p.Provider.SearchAsync(query, timeout.Token);
                var done = await Task.WhenAny(search, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
                if (done != search) return (p, Results: (IReadOnlyList<PluginSearchResult>)[]);
                return (p, Results: await search.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                return (p, Results: (IReadOnlyList<PluginSearchResult>)[]);
            }
            catch (Exception ex)
            {
                PluginLog.Write("error", p.PluginId, "検索で問題がありました: " + ex.GetType().Name + ": " + ex.Message);
                return (p, Results: (IReadOnlyList<PluginSearchResult>)[]);
            }
        }).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var commands = new List<AppCommand>();
        foreach (var (p, found) in results)
        {
            int n = 0;
            foreach (var r in found.Take(max))
            {
                var text = r.CopyText;
                var subtitle = string.Join(" ・ ", new[]
                {
                    r.Time is { } t ? t.LocalDateTime.ToString("M/d HH:mm") : null,
                    r.Subtitle?.For("ja"),
                    p.Provider.Title.For("ja"),
                }.Where(s => !string.IsNullOrEmpty(s)));
                commands.Add(new AppCommand
                {
                    Id = $"search:{p.PluginId}:{p.Provider.Id}:{n++}",
                    Title = Shorten(r.Title.For("ja"), 120),
                    Subtitle = subtitle,
                    Category = CommandCategory.SearchResult,
                    Icon = AppIcon.History,
                    Execute = () => copied?.Invoke(text),
                });
            }
        }
        return commands.Take(max).ToList();
    }

    /// <summary>コピーして知らせる (コマンドの一覧で選んだとき)。</summary>
    public static async void CopyAndNotify(string text)
    {
        var ui = PluginRuntime.Ui;
        if (ui == null) return;
        bool ok = await ui.CopyTextAsync(text);
        ui.Post(() => ui.ShowNotification("GetText", ok
            ? new PluginNotification("コピーしました", Shorten(text.Replace('\n', ' '), 60), PluginNotificationKind.Success)
            : new PluginNotification("コピーできませんでした", "ほかのアプリがクリップボードを使っています。", PluginNotificationKind.Warning)));
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
