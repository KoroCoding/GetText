namespace GetText.Plugins.KeywordMonitor;

/// <summary>
/// キーワードの見張り: 読み取りの画面が読んだ文字に、決めた言葉が出たら知らせる。
/// 読み取りの画面を開いている間だけ動く。読み取った文字は保存・送信・ログ出力しない (知らせに出すだけ)。
/// </summary>
public sealed class KeywordMonitorPlugin : IGetTextPlugin
{
    private const string WordsKey = "words";
    private const string ActiveKey = "active";
    private const string SoundKey = "sound";
    private const string CooldownKey = "cooldown";

    private readonly KeywordMatcher _matcher = new();
    private readonly object _lock = new();
    private IPluginContext? _context;
    private bool _active;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.AddSettings(new PluginSettingsPage
        {
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "キーワードの見張り", ["en"] = "Keyword Monitor" }),
            Items =
            [
                new PluginSetting
                {
                    Key = WordsKey,
                    Label = new LocalizedText(new Dictionary<string, string> { ["ja"] = "見張る言葉", ["en"] = "Keywords" }),
                    Description = "1 行に 1 つ。全角・半角、大文字・小文字、空白の違いは無視します。# で始まる行は飛ばします。",
                    Kind = PluginSettingKind.MultilineText,
                    Default = "",
                },
                new PluginSetting
                {
                    Key = SoundKey,
                    Label = "見つけたら音を鳴らす",
                    Kind = PluginSettingKind.Toggle,
                    Default = "true",
                },
                new PluginSetting
                {
                    Key = CooldownKey,
                    Label = "同じ言葉をもう一度知らせるまで (秒)",
                    Description = "画面に出たままの言葉は、消えてまた出るまで知らせません。",
                    Kind = PluginSettingKind.Number,
                    Default = "30",
                    Minimum = 0,
                    Maximum = 3600,
                    Advanced = true,
                },
            ],
        });
        context.AddFeature(new PluginFeature
        {
            Id = "monitor",
            // 読み取りの画面が読んだ文字を使うので、読み取りの画面の「表示」メニューで切り替える (ホームのタイルにはしない)
            Placement = PluginFeaturePlacement.ReadingWindow,
            Name = new LocalizedText(new Dictionary<string, string> { ["ja"] = "キーワードの見張り", ["en"] = "Keyword Monitor" }),
            Description = "読み取った文字に決めた言葉が出たら知らせます",
            Icon = "Search",
            Status = Status,
            Open = _ => Start(),
            Close = _ => Stop(),
        });
        context.AddCommand(new PluginCommand
        {
            Id = "toggle",
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "キーワードの見張りを始める / 止める", ["en"] = "Start / stop keyword monitor" }),
            Keywords = ["keyword", "monitor", "alert", "見張り", "キーワード", "通知"],
            Icon = "Search",
            Execute = _ => _active ? Stop() : Start(),
        });

        Apply();
        context.Settings.Changed += _ => Apply();
        if (context.Settings.GetBool(ActiveKey, false)) Subscribe(true);
    }

    /// <summary>見張っている間だけ読み取りの結果を受け取る (見張っていなければ、GetText は結果を作らない)。</summary>
    private void Subscribe(bool on)
    {
        if (on == _active) return;
        _active = on;
        if (on) _context!.OcrFrameRead += OnFrame;
        else _context!.OcrFrameRead -= OnFrame;
    }

    private void Apply()
    {
        var settings = _context!.Settings;
        lock (_lock)
        {
            _matcher.SetKeywords(settings.Get(WordsKey));
            _matcher.Cooldown = TimeSpan.FromSeconds(Math.Clamp(settings.GetNumber(CooldownKey, 30), 0, 3600));
        }
    }

    private PluginFeatureStatus Status()
    {
        if (!_active) return new PluginFeatureStatus(PluginFeatureState.Closed);
        int count;
        lock (_lock) count = _matcher.Keywords.Count;
        return new PluginFeatureStatus(PluginFeatureState.Active, count == 0 ? "言葉が未設定" : $"見張り中 ・ {count} 語");
    }

    private Task Start()
    {
        var context = _context!;
        int count;
        lock (_lock)
        {
            _matcher.Reset();
            count = _matcher.Keywords.Count;
        }
        Subscribe(true);
        context.Settings.Set(ActiveKey, "true");
        context.Notify(count == 0
            ? new PluginNotification("見張る言葉がまだありません", "設定 → 拡張機能 → キーワードの見張り → 設定 で、言葉を 1 行に 1 つ書いてください。", PluginNotificationKind.Warning)
            : new PluginNotification("キーワードの見張りを始めました", $"{count} 語を見張ります。読み取りの画面を開いている間だけ動きます。", PluginNotificationKind.Success));
        return Task.CompletedTask;
    }

    private Task Stop()
    {
        Subscribe(false);
        _context!.Settings.Set(ActiveKey, "false");
        lock (_lock) _matcher.Reset();
        return Task.CompletedTask;
    }

    private void OnFrame(object? sender, OcrFrameEventArgs e)
    {
        if (!_active) return;
        List<KeywordHit> hits;
        lock (_lock) hits = _matcher.Observe(e.Lines.Select(l => l.Text), e.Time);
        if (hits.Count == 0) return;
        var context = _context!;
        bool sound = context.Settings.GetBool(SoundKey, true);
        var title = hits.Count == 1 ? $"「{hits[0].Keyword}」が出ました" : $"{string.Join("・", hits.Take(3).Select(h => $"「{h.Keyword}」"))}が出ました";
        var line = hits[0].Line.Length > 80 ? hits[0].Line[..80] + "…" : hits[0].Line;
        context.Notify(new PluginNotification(title, line, PluginNotificationKind.Info, PlaySound: sound));
        context.Log.Info($"言葉が見つかりました ({hits.Count} 語)"); // (言葉と文はログに書かない)
    }

    public void Shutdown()
    {
        if (_context != null && _active) _context.OcrFrameRead -= OnFrame;
    }
}
