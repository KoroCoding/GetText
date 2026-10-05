using System.ComponentModel;
using System.Windows;

namespace GetText;

/// <summary>
/// 起動したときに出す、機能を選ぶ画面 (文字の読み取り・議事録・画面の録画)。開いている機能の切り替えと、GetText の終了もここで行う。
/// 機能を開くと最小化し (設定で変えられる)、機能をすべて閉じると戻る。
/// </summary>
public partial class HomeWindow : Window
{
    private readonly TextWindow _text;
    private readonly AppSettings _settings;
    private RecorderWindow? _recorder;
    private bool _exiting;
    private bool _anyOpen;

    public HomeWindow(TextWindow text, AppSettings settings)
    {
        InitializeComponent();
        _text = text;
        _settings = settings;
        MinimizeCheck.IsChecked = settings.HomeMinimizeOnOpen;
        text.FeaturesChanged += () => Dispatcher.BeginInvoke(UpdateCards);
        // 読み取りの画面 (アプリの中心) が閉じたら GetText を終える (終了の確認と保存は読み取りの画面が行う)
        text.Closed += (_, _) =>
        {
            if (_abandoned) return;
            _exiting = true;
            Application.Current.Shutdown();
        };
        Closing += OnClosing;
        Loaded += (_, _) => { if (!_demo) UpdateCards(); };
    }

    private bool _demo;

    /// <summary>見本の画面 (操作手順の画像用): 開いている機能の印を出す (minutes / record は印の文字、null なら閉じている)。</summary>
    internal void ShowDemo(bool ocr, string? minutes, string? record)
    {
        _demo = true;
        SetCard(ocr, OcrOpen, OcrClose, OcrChip);
        SetCard(minutes != null, MinutesOpen, MinutesClose, MinutesChip);
        SetCard(record != null, RecordOpen, RecordClose, RecordChip);
        MinutesChipText.Text = minutes ?? "";
        RecordChipText.Text = record ?? "";
    }

    /// <summary>画面の録画 (開いていなければ null)。</summary>
    public RecorderWindow? Recorder => _recorder;

    // ───────── 機能を開く・閉じる ─────────

    private void OcrOpen_Click(object sender, RoutedEventArgs e)
    {
        _text.OpenOcr();
        AfterOpen();
    }

    private void OcrClose_Click(object sender, RoutedEventArgs e) => _text.CloseOcr();

    private void MinutesOpen_Click(object sender, RoutedEventArgs e)
    {
        _text.OpenMinutes();
        AfterOpen();
    }

    private void MinutesClose_Click(object sender, RoutedEventArgs e) => _text.Minutes?.Close();

    private void RecordOpen_Click(object sender, RoutedEventArgs e)
    {
        OpenRecorder();
        AfterOpen();
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

    private void RecordClose_Click(object sender, RoutedEventArgs e) => _recorder?.Close();

    private void AfterOpen()
    {
        UpdateCards();
        if (_settings.HomeMinimizeOnOpen) WindowState = WindowState.Minimized;
    }

    private void MinimizeCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.HomeMinimizeOnOpen = MinimizeCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>開いている機能の表示を合わせる。機能をすべて閉じたら、この画面を戻す。</summary>
    private void UpdateCards()
    {
        if (_exiting) return;
        bool ocr = _text.IsOcrOpen;
        bool minutes = _text.Minutes is { IsLoaded: true };
        bool record = _recorder is { IsLoaded: true };
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

    private static void SetCard(bool open, System.Windows.Controls.Button openButton, System.Windows.Controls.Button closeButton,
        System.Windows.Controls.Border chip)
    {
        openButton.Content = open ? "前に出す" : "開く";
        closeButton.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        chip.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _text.OpenSettings(0, this);

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

    /// <summary>GetText を終える (録画・議事録を止めてよいか先に聞く)。終わった後に after を行う (セットアップの起動など)。</summary>
    public void ExitThen(Action after)
    {
        _afterExit = after;
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
                if (MessageBox.Show(this, "画面を録画中です。録画を止めて保存し、GetText を終了しますか？", "GetText",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
                {
                    _afterExit = null;
                    return;
                }
            }
            if (_text.Minutes is { IsBusy: true } minutes && !minutes.ConfirmStop("、GetText を終了します"))
            {
                _afterExit = null;
                return;
            }
            // 録画を止めて書き終えるまで待つ (始めている・書き終えている途中でも待つ。途中で終えるとファイルが壊れる)
            if (recorder is { IsBusy: true })
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                await recorder.StopAndWaitAsync();
                // (待っている間に議事録の記録を始めた)
                if (_text.Minutes is { IsBusy: true } started && !started.ConfirmStop("、GetText を終了します"))
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
        }
    }

    /// <summary>Windows のログオフ・シャットダウン: 録画を止めて書き終える (待てる時間は短いので、最大 10 秒)。</summary>
    public void StopRecordingNow()
    {
        _exiting = true;
        _recorder?.StopNow();
    }
}
