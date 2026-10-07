using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace GetText;

/// <summary>ホームの機能のタイル (FeatureInfo から作る表示用のもの)。</summary>
public sealed class FeatureTile(FeatureInfo info) : INotifyPropertyChanged
{
    public FeatureInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Description => Info.Description;
    public AppIcon Icon => Info.Icon.Semantic;

    private FeatureStatus _status = FeatureStatus.Closed;
    private bool _installed = true;

    public bool HasStatus => !_installed || _status.State != FeatureState.Closed;
    public bool CanClose => _installed && _status.State != FeatureState.Closed && Info.Close != null;
    public string StatusLabel => !_installed ? $"追加する{(Info.DownloadSize is { } size ? " ・ " + size : "")}" : _status.Label ?? "";
    public AppIcon StatusIcon => !_installed ? AppIcon.Download : _status.State == FeatureState.Active ? AppIcon.Recording : AppIcon.Success;
    public IBrush StatusForeground => MacTheme.BrushOf(p => !_installed ? p.AccentText : _status.State == FeatureState.Active ? p.Critical : p.Success);
    public IBrush PillBackground => MacTheme.BrushOf(p => !_installed ? p.AccentSubtle : _status.State == FeatureState.Active ? p.CriticalSubtle : p.SuccessSubtle);
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

/// <summary>最近の議事録の 1 行。</summary>
public sealed record RecentItem(string Path, string Label);

/// <summary>
/// 起動したときに出すホーム (GetText Hub): 機能のタイル (開く・状態・閉じる)、検索 (コマンドの一覧 ⌘K)、最近の議事録、設定・終了。
/// 機能は AppCommands.Features から描く。
/// </summary>
public partial class HomeWindow : Window
{
    private readonly TextWindow? _text;
    private readonly AppSettings _settings;
    private RecorderWindow? _recorder;
    private bool _exiting;
    private bool _anyOpen;
    private bool _demo;
    private readonly List<FeatureTile> _tiles = [];

    public HomeWindow() : this(null!, new AppSettings()) { }

    public HomeWindow(TextWindow text, AppSettings settings)
    {
        InitializeComponent();
        _text = text;
        _settings = settings;
        MinimizeCheck.IsChecked = settings.HomeMinimizeOnOpen;
        if (text != null)
        {
            AppCommands.RegisterBuiltIns(this, text, settings);
            foreach (var feature in AppCommands.Features) _tiles.Add(new FeatureTile(feature));
            FeatureList.ItemsSource = _tiles;
            text.FeaturesChanged += () => Dispatcher.UIThread.Post(UpdateCards);
            // 読み取りの画面 (アプリの中心) が閉じたら GetText を終える (終了の確認と保存は読み取りの画面が行う)
            text.Closed += (_, _) =>
            {
                _exiting = true;
                (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            };
        }
        Closing += OnClosing;
        Opened += (_, _) => { if (!_demo) { UpdateCards(); LoadRecent(); } };
        Activated += (_, _) => { if (!_demo) { UpdateCards(); LoadRecent(); } };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (AppCommands.HandleKey(this, e, CommandContext.Home)) return;
        }, RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += (_, _) => UpdateCards();
    }

    /// <summary>見本の画面 (画面の画像用): 開いている機能の印と最近の議事録を出す。</summary>
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

    public RecorderWindow? Recorder => _recorder;

    private void Tile_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not FeatureTile tile || _demo) return;
        if (tile.Installed) tile.Info.Open();
        else tile.Info.Install?.Invoke();
        UpdateCards();
    }

    private void TileClose_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FeatureTile tile && !_demo) tile.Info.Close?.Invoke();
        UpdateCards();
    }

    /// <summary>画面の録画を開く (開いていれば前に出す)。</summary>
    public RecorderWindow OpenRecorder()
    {
        if (_recorder is { IsVisible: true })
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
        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void MinimizeCheck_Click(object? sender, RoutedEventArgs e)
    {
        _settings.HomeMinimizeOnOpen = MinimizeCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>機能の状態の表示を合わせる。機能をすべて閉じたら、この画面を戻す。</summary>
    private void UpdateCards()
    {
        if (_exiting || _text == null) return;
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
        RecentList.IsVisible = items.Count > 0;
        RecentEmpty.IsVisible = items.Count == 0;
    }

    private void Recent_Click(object? sender, RoutedEventArgs e)
    {
        if (_demo || _text == null || (sender as Control)?.Tag is not string path) return;
        _text.OpenMinutes().OpenRecent(path);
        AfterFeatureOpened();
    }

    private void RecentEmptyOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (_demo) return;
        var feature = AppCommands.Features.First(f => f.Id == "minutes");
        if (feature.IsInstalled()) feature.Open();
        else feature.Install?.Invoke();
    }

    private void Search_Click(object? sender, RoutedEventArgs e) => AppCommands.TogglePalette(this, CommandContext.Home);

    private void Settings_Click(object? sender, RoutedEventArgs e) => _text?.OpenSettings(SettingsPage.Appearance, this);

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    private bool _exitInProgress;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting || _text == null || e.CloseReason == WindowCloseReason.ApplicationShutdown) return;
        e.Cancel = true;
        if (_exitInProgress) return; // (確認や書き終えるのを待っている間に、もう一度押された)
        _exitInProgress = true;
        try
        {
            // 止める前に、録画と議事録の両方を止めてよいか聞く (片方を止めてから「止めない」を選ばれないように)
            if (!await ConfirmRecordingAsync(this)) return;
            if (_text.Minutes is { IsBusy: true } minutes && !await minutes.ConfirmStopAsync("GetText を終了します")) return;
            await StopRecordingAndWaitAsync();
            _text.RequestExit(minutesConfirmed: true);
        }
        finally
        {
            _exitInProgress = false;
        }
    }

    /// <summary>録画中なら、止めて保存してよいか確かめる。止めてよい (録画していない) なら true。</summary>
    public async Task<bool> ConfirmRecordingAsync(Window owner)
    {
        if (_recorder is not { IsRecording: true }) return true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        return await Dialogs.ConfirmAsync(owner, "画面を録画中です。録画を止めて保存し、GetText を終了しますか？", "GetText", "止めて終了");
    }

    /// <summary>録画を止めて書き終えるまで待つ (始めている・書き終えている途中でも待つ。途中で終えるとファイルが壊れる)。</summary>
    public async Task StopRecordingAndWaitAsync()
    {
        if (_recorder is { IsBusy: true } recorder) await recorder.StopAndWaitAsync();
    }
}
