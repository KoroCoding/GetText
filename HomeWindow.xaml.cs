using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GetText;

/// <summary>ホームの機能のタイル (FeatureInfo から作る表示用のもの)。</summary>
public sealed class FeatureTile(FeatureInfo info) : INotifyPropertyChanged
{
    public FeatureInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Description => Info.Description;
    public string Glyph => WindowsIcons.Glyph(Info.Icon.Semantic);

    private FeatureStatus _status = FeatureStatus.Closed;
    private bool _installed = true;

    public bool HasStatus => !_installed || _status.State != FeatureState.Closed;
    public bool CanClose => _installed && _status.State != FeatureState.Closed && Info.Close != null;
    public string StatusLabel => !_installed ? $"追加する{(Info.DownloadSize is { } size ? " ・ " + size : "")}" : _status.Label ?? "";
    public string StatusGlyph => WindowsIcons.Glyph(!_installed ? AppIcon.Download : _status.State == FeatureState.Active ? AppIcon.Recording : AppIcon.Success);
    public Brush StatusForeground => Theme.Brush(p => !_installed ? p.AccentText : _status.State == FeatureState.Active ? p.Critical : p.Success);
    public Brush PillBackground => Theme.Brush(p => !_installed ? p.AccentSubtle : _status.State == FeatureState.Active ? p.CriticalSubtle : p.SuccessSubtle);
    public string CloseLabel => $"{Name}を閉じる";
    public string AccessibleName => !_installed ? $"{Name}: {StatusLabel}" : HasStatus ? $"{Name}: {StatusLabel}。押すと前に出します" : $"{Name}を開く";
    public string ToolTip => !_installed ? $"{Description}\nセットアップで入れると使えます" : Description;
    public bool Installed => _installed;

    public void Refresh()
    {
        _installed = Info.IsInstalled();
        _status = _installed ? Info.Status() : FeatureStatus.Closed;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 起動したときに出すホーム (GetText Hub): 機能のタイル (開く・状態・閉じる)、検索 (コマンドの一覧)、最近の議事録、設定・終了。
/// 機能は AppCommands.Features から描く (拡張機能が増えてもタイルが並ぶだけで、画面の作りは変わらない)。
/// 機能を開くと最小化し (設定で変えられる)、機能をすべて閉じると戻る。
/// </summary>
public partial class HomeWindow : Window
{
    private readonly TextWindow _text;
    private readonly AppSettings _settings;
    private RecorderWindow? _recorder;
    private bool _exiting;
    private bool _anyOpen;
    private readonly List<FeatureTile> _tiles = [];

    public HomeWindow(TextWindow text, AppSettings settings)
    {
        InitializeComponent();
        _text = text;
        _settings = settings;
        MinimizeCheck.IsChecked = settings.HomeMinimizeOnOpen;
        Activated += (_, _) => MinimizeCheck.IsChecked = _settings.HomeMinimizeOnOpen; // (設定の画面で変えたとき)
        AppCommands.RegisterBuiltIns(this, text, settings);
        AppCommands.SetHost(text);
        foreach (var feature in AppCommands.Features.Where(f => !f.InReadingWindow)) _tiles.Add(new FeatureTile(feature));
        // 拡張機能のタイルの状態 (「記録中 ・ 12 枚」など) は、ホームが出ている間 1 秒ごとに描き直す (前に出たときだけでは古くなる)
        var refresh = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        refresh.Tick += (_, _) =>
        {
            if (!IsVisible || WindowState == WindowState.Minimized) return;
            foreach (var tile in _tiles.Where(t => t.Info.Id.StartsWith("plugin:", StringComparison.Ordinal))) tile.Refresh();
        };
        refresh.Start();
        Closed += (_, _) => refresh.Stop();
        FeatureList.ItemsSource = _tiles;
        text.FeaturesChanged += () => Dispatcher.BeginInvoke(UpdateCards);
        // 読み取りの画面 (アプリの中心) が閉じたら GetText を終える (終了の確認と保存は読み取りの画面が行う)
        text.Closed += (_, _) =>
        {
            if (_abandoned) return;
            _exiting = true;
            Application.Current.Shutdown();
        };
        Closing += OnClosing;
        PreviewKeyDown += (_, e) =>
        {
            if (AppCommands.HandleKey(this, e, CommandContext.Home)) return;
            if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
            {
                _text.OpenSettings(SettingsPage.Appearance, this);
                e.Handled = true;
            }
        };
        Theme.Changed += UpdateCards;
        Closed += (_, _) => Theme.Changed -= UpdateCards;
        Loaded += (_, _) => { if (!_demo) { UpdateCards(); LoadRecent(); } };
        Activated += (_, _) => { if (!_demo) { UpdateCards(); LoadRecent(); } }; // 議事録を保存した・セットアップから戻ったとき
    }

    private bool _demo;

    /// <summary>見本の画面 (操作手順の画像用): 開いている機能の印と最近の議事録を出す (minutes / record は印の文字、null なら閉じている)。</summary>
    internal void ShowDemo(bool ocr, string? minutes, string? record, IReadOnlyList<string>? recent = null, bool minutesInstalled = true)
    {
        _demo = true;
        var states = new Dictionary<string, FeatureStatus>
        {
            ["ocr"] = ocr ? new FeatureStatus(FeatureState.Open, "開いています") : FeatureStatus.Closed,
            ["minutes"] = minutes == null ? FeatureStatus.Closed : new FeatureStatus(minutes == "記録中" ? FeatureState.Active : FeatureState.Open, minutes),
            ["record"] = record == null ? FeatureStatus.Closed : new FeatureStatus(record == "録画中" ? FeatureState.Active : FeatureState.Open, record),
        };
        _tiles.Clear();
        foreach (var f in AppCommands.Features)
        {
            var status = states.GetValueOrDefault(f.Id, FeatureStatus.Closed);
            bool installed = f.Id != "minutes" || minutesInstalled;
            _tiles.Add(new FeatureTile(new FeatureInfo
            {
                Id = f.Id, Name = f.Name, Description = f.Description, Icon = f.Icon, DownloadSize = f.DownloadSize,
                IsInstalled = () => installed, Status = () => status, Open = () => { }, Close = f.Close, Install = f.Install,
            }));
        }
        FeatureList.ItemsSource = null;
        FeatureList.ItemsSource = _tiles;
        foreach (var tile in _tiles) tile.Refresh();
        ShowRecent((recent ?? []).Select(label => new RecentItem(label, label)).ToList());
    }

    /// <summary>拡張機能を読み込んだ・外したとき: 機能のタイルを並べ直す (画面の作りは変わらない)。</summary>
    internal void ReloadFeatures()
    {
        if (_demo || _exiting) return;
        _tiles.Clear();
        foreach (var feature in AppCommands.Features.Where(f => !f.InReadingWindow)) _tiles.Add(new FeatureTile(feature));
        FeatureList.ItemsSource = null;
        FeatureList.ItemsSource = _tiles;
        UpdateCards();
    }

    /// <summary>画面の録画 (開いていなければ null)。</summary>
    public RecorderWindow? Recorder => _recorder;

    // ───────── 機能を開く・閉じる ─────────

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FeatureTile tile || _demo) return;
        if (tile.Installed) tile.Info.Open();
        else tile.Info.Install?.Invoke();
        UpdateCards();
    }

    private void TileClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FeatureTile tile && !_demo) tile.Info.Close?.Invoke();
        UpdateCards();
    }

    /// <summary>画面の録画を開く (開いていれば前に出す)。</summary>
    public RecorderWindow OpenRecorder()
    {
        if (_recorder is { IsLoaded: true })
        {
            if (_recorder.WindowState == WindowState.Minimized) _recorder.WindowState = WindowState.Normal;
            _recorder.Activate();
            return _recorder;
        }
        _recorder = new RecorderWindow(_settings);
        _recorder.Closed += (_, _) =>
        {
            _recorder = null;
            UpdateCards();
        };
        _recorder.RecordingChanged += UpdateCards;
        _recorder.Show();
        UpdateCards();
        return _recorder;
    }

    /// <summary>機能を開いた後 (設定によってはホームをしまう)。</summary>
    internal void AfterFeatureOpened()
    {
        UpdateCards();
        if (_settings.HomeMinimizeOnOpen && !_demo) WindowState = WindowState.Minimized;
    }

    /// <summary>ホームを前に出す。</summary>
    internal void ShowHome()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void MinimizeCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.HomeMinimizeOnOpen = MinimizeCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>機能の状態の表示を合わせる。機能をすべて閉じたら、この画面を戻す。</summary>
    private void UpdateCards()
    {
        if (_exiting) return;
        foreach (var tile in _tiles) tile.Refresh();
        if (_demo) return;
        bool any = _tiles.Any(t => t.HasStatus && t.Installed);
        if (_anyOpen && !any && WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            Activate();
        }
        _anyOpen = any;
    }

    // ───────── 最近の議事録 ─────────

    public sealed record RecentItem(string Path, string Label);

    private void LoadRecent()
    {
        try
        {
            ShowRecent(RecentMinutes.List(MinutesWindow.RecentFolder, 4).Select(r => new RecentItem(r.Path, r.Label)).ToList());
        }
        catch (Exception ex)
        {
            App.Log("RecentMinutes", ex);
            ShowRecent([]);
        }
    }

    private void ShowRecent(List<RecentItem> items)
    {
        RecentList.ItemsSource = items;
        RecentList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentEmpty.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (_demo || (sender as FrameworkElement)?.Tag is not string path) return;
        _text.OpenMinutes().OpenRecent(path);
        AfterFeatureOpened();
    }

    private void RecentEmptyOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_demo) return;
        var feature = AppCommands.Features.First(f => f.Id == "minutes");
        if (feature.IsInstalled()) feature.Open();
        else feature.Install?.Invoke();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => AppCommands.TogglePalette(this, CommandContext.Home);

    private void Settings_Click(object sender, RoutedEventArgs e) => _text.OpenSettings(SettingsPage.Appearance, this);

    // ───────── 終了 ─────────

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        await ExitAsync();
    }

    private bool _exitInProgress;
    private bool _abandoned;
    private Action? _afterExit;
    /// <summary>確かめるときの言い方 (「終了」「再起動」)。</summary>
    private string _exitAction = "終了";

    /// <summary>GetText を終える (録画・議事録を止めてよいか先に聞く)。終わった後に after を行う (セットアップの起動・再起動など)。</summary>
    public void ExitThen(Action after, string action = "終了")
    {
        _afterExit = after;
        _exitAction = action;
        _ = ExitAsync();
    }

    /// <summary>GetText が終わるとき (App の Exit) に、頼まれていたことを行う。</summary>
    internal void RunAfterExit()
    {
        var after = _afterExit;
        _afterExit = null;
        try { after?.Invoke(); }
        catch (Exception ex) { App.Log("AfterExit", ex); }
    }

    /// <summary>起動に失敗して作り直すとき: この画面を閉じても GetText を終えない。</summary>
    internal void Abandon()
    {
        _abandoned = true;
        _exiting = true;
    }

    private async Task ExitAsync()
    {
        if (_exitInProgress) return; // (確認や書き終えるのを待っている間に、もう一度押された)
        _exitInProgress = true;
        try
        {
            var recorder = _recorder;
            // 止める前に、録画と議事録の両方を止めてよいか聞く (片方を止めてから「止めない」を選ばれないように)
            if (recorder is { IsRecording: true })
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                if (MessageBox.Show(this, $"画面を録画中です。録画を止めて保存し、GetText を{_exitAction}しますか？", "GetText",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
                {
                    _afterExit = null;
                    return;
                }
            }
            if (_text.Minutes is { IsBusy: true } minutes && !minutes.ConfirmStop($"、GetText を{_exitAction}します"))
            {
                _afterExit = null;
                return;
            }
            // 拡張機能が動かしている機能 (スライドの記録など) も、黙って止めない
            var running = AppCommands.Features
                .Where(f => f.Id.StartsWith("plugin:", StringComparison.Ordinal) && !f.InReadingWindow && f.Status().State == FeatureState.Active)
                .Select(f => f.Name).ToList();
            if (running.Count > 0)
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                if (MessageBox.Show(this, $"{string.Join("・", running)} が動いています。止めて GetText を{_exitAction}しますか？", "GetText",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    _afterExit = null;
                    return;
                }
            }
            // 録画を止めて書き終えるまで待つ (始めている・書き終えている途中でも待つ。途中で終えるとファイルが壊れる)
            if (recorder is { IsBusy: true })
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                await recorder.StopAndWaitAsync();
                // (待っている間に議事録の記録を始めた)
                if (_text.Minutes is { IsBusy: true } started && !started.ConfirmStop($"、GetText を{_exitAction}します"))
                {
                    _afterExit = null;
                    return;
                }
            }
            // 設定と議事録の保存は読み取りの画面が行い、閉じたら終わる
            _text.RequestExit(minutesConfirmed: true);
        }
        finally
        {
            _exitInProgress = false;
            _exitAction = "終了";
        }
    }

    /// <summary>Windows のログオフ・シャットダウン: 録画を止めて書き終える (待てる時間は短いので、最大 10 秒)。</summary>
    public void StopRecordingNow()
    {
        _exiting = true;
        _recorder?.StopNow();
    }
}
