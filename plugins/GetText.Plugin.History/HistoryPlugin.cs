namespace GetText.Plugins.History;

/// <summary>
/// 読み取りの履歴: 読み取りの画面が読んだ文字を、この PC に保存して、コマンドの一覧 (Ctrl+K / ⌘K) で探せるようにする。
/// 既定では保存しない (タイル・コマンド・設定で始めたときだけ)。除くアプリ・除く言葉・保存期間を選べ、すべて消せる。
/// 読み取った文字はインターネットに送らず、ログにも書かない。
/// </summary>
public sealed class HistoryPlugin : IGetTextPlugin, IPluginSearchProvider
{
    private const string EnabledKey = "enabled";
    private const string RetentionKey = "retention";
    private const string ExcludedAppsKey = "excludedApps";
    private const string ExcludedWordsKey = "excludedWords";

    /// <summary>既定で除くアプリ (パスワードの管理アプリ)。</summary>
    public const string DefaultExcludedApps = "1Password\nKeePass\nKeePassXC\nBitwarden\nDashlane";

    private IPluginContext? _context;
    private HistoryStore? _store;
    private bool _enabled;
    private DateTime _lastPrune;

    public string Id => "history";
    public LocalizedText Title => new(new Dictionary<string, string> { ["ja"] = "読み取りの履歴", ["en"] = "OCR history" });
    public LocalizedText? Placeholder => "読み取った文字を探す";

    public void Initialize(IPluginContext context)
    {
        _context = context;
        _store = new HistoryStore(Path.Combine(context.DataDirectory, "history"));
        context.AddSettings(new PluginSettingsPage
        {
            Title = Title,
            Items =
            [
                new PluginSetting
                {
                    Key = EnabledKey,
                    Label = "読み取った文字を履歴に保存する",
                    Description = "この PC の中にだけ保存し、コマンドの一覧 (Ctrl+K / ⌘K) で探せます。既定は保存しません。",
                    Kind = PluginSettingKind.Toggle,
                    Default = "false",
                },
                new PluginSetting
                {
                    Key = RetentionKey,
                    Label = "保存しておく期間",
                    Description = "これより前の履歴は自動で消します。",
                    Kind = PluginSettingKind.Choice,
                    Default = "7",
                    Choices = [("1", "1 日"), ("7", "7 日"), ("30", "30 日"), ("90", "90 日")],
                },
                new PluginSetting
                {
                    Key = ExcludedAppsKey,
                    Label = "保存しないアプリ",
                    Description = "1 行に 1 つ、アプリの名前 (プロセス名)。読み取りの範囲の下がこのアプリのときは保存しません。",
                    Kind = PluginSettingKind.MultilineText,
                    Default = DefaultExcludedApps,
                },
                new PluginSetting
                {
                    Key = ExcludedWordsKey,
                    Label = "この言葉を含むときは保存しない",
                    Description = "1 行に 1 つ。例: パスワード・暗証番号・社外秘",
                    Kind = PluginSettingKind.MultilineText,
                    Default = "",
                    Advanced = true,
                },
            ],
        });
        context.AddFeature(new PluginFeature
        {
            Id = "history",
            // 読み取りの画面が読んだ文字を使うので、読み取りの画面の「表示」メニューで切り替える (ホームのタイルにはしない)
            Placement = PluginFeaturePlacement.ReadingWindow,
            Name = Title,
            Description = "読み取った文字をこの PC に保存して、あとで探せます (既定は保存しない)",
            Icon = "History",
            Status = Status,
            Open = _ => SetEnabled(true),
            Close = _ => SetEnabled(false),
        });
        context.AddCommand(new PluginCommand
        {
            Id = "toggle",
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "読み取りの履歴の保存を始める / 止める", ["en"] = "Start / stop saving OCR history" }),
            Keywords = ["history", "履歴", "保存", "OCR"],
            Icon = "History",
            Execute = _ => SetEnabled(!_enabled),
        });
        context.AddCommand(new PluginCommand
        {
            Id = "clear",
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "読み取りの履歴をすべて消す", ["en"] = "Delete all OCR history" }),
            Keywords = ["history", "delete", "clear", "履歴", "消す", "削除"],
            Icon = "Clear",
            Execute = ClearAsync,
        });
        context.AddCommand(new PluginCommand
        {
            Id = "folder",
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "読み取りの履歴のフォルダを開く", ["en"] = "Open OCR history folder" }),
            Keywords = ["history", "folder", "履歴", "フォルダ"],
            Icon = "Folder",
            Execute = _ =>
            {
                Directory.CreateDirectory(_store!.Folder);
                context.GetService<IPluginUi>()?.Reveal(_store.Folder);
                return Task.CompletedTask;
            },
        });
        context.AddSearchProvider(this);

        Apply();
        context.Settings.Changed += key =>
        {
            Apply();
            if (key == EnabledKey) Subscribe(context.Settings.GetBool(EnabledKey, false));
            // 保存期間を短くしたら、過ぎた履歴をすぐ消す (次に起動するまで検索に出さない)
            if (key == RetentionKey) _ = Task.Run(Prune);
        };
        Subscribe(context.Settings.GetBool(EnabledKey, false));
        Prune();
    }

    private void Apply()
    {
        var s = _context!.Settings;
        _store!.RetentionDays = int.TryParse(s.Get(RetentionKey), out var days) ? Math.Clamp(days, 1, 365) : 7;
        _store.ExcludedApps = HistoryStore.Lines(s.Get(ExcludedAppsKey) ?? DefaultExcludedApps);
        _store.ExcludedWords = HistoryStore.Lines(s.Get(ExcludedWordsKey));
    }

    /// <summary>保存している間だけ読み取りの結果を受け取る (保存しないときは、GetText は結果を作らない)。</summary>
    private void Subscribe(bool on)
    {
        if (on == _enabled) return;
        _enabled = on;
        if (on)
        {
            _context!.OcrFrameRead += OnFrame;
            // (保存してある履歴を、裏で先に読んでおく。読めなければ、保存のときにもう一度試す)
            _ = Task.Run(() =>
            {
                try { _ = _store!.Count; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _context.Log.Error("履歴を読めませんでした", ex); }
            });
        }
        else
        {
            _context!.OcrFrameRead -= OnFrame;
            lock (_pendingLock) _pending = null;
        }
    }

    private Task SetEnabled(bool on)
    {
        var context = _context!;
        context.Settings.Set(EnabledKey, on ? "true" : "false");
        Subscribe(on);
        if (on)
            context.Notify(new PluginNotification("読み取りの履歴の保存を始めました",
                $"この PC にだけ保存し、{_store!.RetentionDays} 日たったら消します。Ctrl+K (⌘K) で探せます。", PluginNotificationKind.Success));
        return Task.CompletedTask;
    }

    // (ホームのタイルは UI のスレッドで呼ばれるので、まだ読んでいない履歴は読み込まない)
    private PluginFeatureStatus Status() =>
        !_enabled ? new PluginFeatureStatus(PluginFeatureState.Closed)
        : new PluginFeatureStatus(PluginFeatureState.Open, _store!.CountIfLoaded is { } n ? $"保存中 ・ {n} 件" : "保存中");

    private void OnFrame(object? sender, OcrFrameEventArgs e)
    {
        if (!_enabled) return;
        Save(e.Time, e.SourceApplication, e.Text);
    }

    private readonly object _pendingLock = new();
    private (string? App, string Text)? _pending;
    private Timer? _flushTimer;

    private void Save(DateTimeOffset time, string? app, string text)
    {
        try
        {
            if (_store!.Add(time, app, text))
            {
                lock (_pendingLock) _pending = null;
                if (DateTime.Now - _lastPrune > TimeSpan.FromHours(1)) Prune();
                return;
            }
            // 続けて変わっている間 (前の保存から間もない) は、最新の 1 回分だけ取っておき、間隔があいたら保存する
            // (読み取りの結果は画面が変わったときにしか届かないので、捨てると止まった最後の画面が残らない)
            if (time - _store.LastSaved < _store.MinInterval)
            {
                lock (_pendingLock) _pending = (app, text);
                _flushTimer ??= new Timer(_ => FlushPending());
                _flushTimer.Change(_store.MinInterval, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context!.Log.Error("履歴を保存できませんでした", ex); // (文字は書かない)
        }
    }

    private void FlushPending()
    {
        (string? App, string Text)? pending;
        lock (_pendingLock)
        {
            pending = _pending;
            _pending = null;
        }
        if (pending is { } p && _enabled) Save(DateTimeOffset.Now, p.App, p.Text);
    }

    private void Prune()
    {
        _lastPrune = DateTime.Now;
        try
        {
            int removed = _store!.Prune(DateTimeOffset.Now);
            if (removed > 0) _context!.Log.Info($"保存期間を過ぎた履歴 ({removed} 日分) を消しました");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context!.Log.Error("古い履歴を消せませんでした", ex);
        }
    }

    private async Task ClearAsync(CancellationToken ct)
    {
        var context = _context!;
        int count = _store!.Count;
        var ui = context.GetService<IPluginUi>();
        if (ui == null) return;
        // (初めに選ばれているのは「消さない」。Enter だけでは消えない)
        var answer = await ui.ChooseAsync("読み取りの履歴を消す", $"保存した {count} 件をすべて消します。元に戻せません。", ["消さない", "すべて消す"], ct);
        if (answer != 1) return;
        try
        {
            _store.Clear();
            context.Log.Info("履歴をすべて消しました");
            context.Notify(new PluginNotification("読み取りの履歴をすべて消しました", null, PluginNotificationKind.Success));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Notify(new PluginNotification("履歴を消せませんでした", ex.Message, PluginNotificationKind.Error));
        }
    }

    public Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<PluginSearchResult>>(() => _store!.Search(query, 30)
            .Select(h => new PluginSearchResult(h.Line, h.Entry.App is { } app ? new LocalizedText(app) : null, h.Entry.Time, h.Entry.Text))
            .ToList(), cancellationToken);

    public void Shutdown()
    {
        _flushTimer?.Dispose();
        if (_enabled && _context != null) _context.OcrFrameRead -= OnFrame;
    }
}
