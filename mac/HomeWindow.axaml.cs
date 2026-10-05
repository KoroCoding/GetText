using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace GetText;

/// <summary>
/// 起動したときに出す、機能を選ぶ画面 (文字の読み取り・議事録・画面の録画)。開いている機能の切り替えと、GetText の終了もここで行う。
/// </summary>
public partial class HomeWindow : Window
{
    private readonly TextWindow? _text;
    private readonly AppSettings _settings;
    private RecorderWindow? _recorder;
    private bool _exiting;
    private bool _anyOpen;

    public HomeWindow() : this(null!, new AppSettings()) { }

    public HomeWindow(TextWindow text, AppSettings settings)
    {
        InitializeComponent();
        _text = text;
        _settings = settings;
        MinimizeCheck.IsChecked = settings.HomeMinimizeOnOpen;
        if (text != null)
        {
            text.FeaturesChanged += () => Dispatcher.UIThread.Post(UpdateCards);
            // 読み取りの画面 (アプリの中心) が閉じたら GetText を終える (終了の確認と保存は読み取りの画面が行う)
            text.Closed += (_, _) =>
            {
                _exiting = true;
                (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            };
        }
        Closing += OnClosing;
        Opened += (_, _) => UpdateCards();
    }

    public RecorderWindow? Recorder => _recorder;

    private void OcrOpen_Click(object? sender, RoutedEventArgs e)
    {
        _text?.OpenOcr();
        AfterOpen();
    }

    private void OcrClose_Click(object? sender, RoutedEventArgs e) => _text?.CloseOcr();

    private void MinutesOpen_Click(object? sender, RoutedEventArgs e)
    {
        _text?.OpenMinutes();
        AfterOpen();
    }

    private void MinutesClose_Click(object? sender, RoutedEventArgs e) => _text?.Minutes?.Close();

    private void RecordOpen_Click(object? sender, RoutedEventArgs e)
    {
        OpenRecorder();
        AfterOpen();
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

    private void RecordClose_Click(object? sender, RoutedEventArgs e) => _recorder?.Close();

    private void AfterOpen()
    {
        UpdateCards();
        if (_settings.HomeMinimizeOnOpen) WindowState = WindowState.Minimized;
    }

    private void MinimizeCheck_Click(object? sender, RoutedEventArgs e)
    {
        _settings.HomeMinimizeOnOpen = MinimizeCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>開いている機能の表示を合わせる。機能をすべて閉じたら、この画面を戻す。</summary>
    private void UpdateCards()
    {
        if (_exiting || _text == null) return;
        bool ocr = _text.IsOcrOpen;
        bool minutes = _text.Minutes is { IsVisible: true };
        bool record = _recorder is { IsVisible: true };
        SetCard(ocr, OcrOpen, OcrClose, OcrChip);
        SetCard(minutes, MinutesOpen, MinutesClose, MinutesChip);
        SetCard(record, RecordOpen, RecordClose, RecordChip);
        MinutesChipText.Text = _text.Minutes?.IsBusy == true ? "記録中" : "開いています";
        RecordChipText.Text = _recorder?.IsRecording == true ? "録画中" : "開いています";
        bool any = ocr || minutes || record;
        if (_anyOpen && !any && WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            Activate();
        }
        _anyOpen = any;
    }

    private static void SetCard(bool open, Button openButton, Button closeButton, Border chip)
    {
        openButton.Content = open ? "前に出す" : "開く";
        closeButton.IsVisible = open;
        chip.IsVisible = open;
    }

    private void Settings_Click(object? sender, RoutedEventArgs e) => _text?.OpenSettings(0, this);

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
