using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GetText;

/// <summary>"#RRGGBB" をブラシにする。</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString((string)value));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 話者の色を、話者の名前の文字に使える色にする ([0] 話者の色 "#RRGGBB"、[1] 一覧の文字の色 = テーマの判定用)。
/// テーマが変わると [1] が変わり、色も選び直される。
/// </summary>
public sealed class SpeakerTextBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [string hex, ..]) return Brushes.Gray;
        bool dark = values is [_, SolidColorBrush fg, ..] && SpeakerColors.Luminance(fg.Color) > 0.5; // 文字が明るい = ダーク
        return new SolidColorBrush(SpeakerColors.ForText((Color)ColorConverter.ConvertFromString(hex), dark));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>話者の色の見やすさの調整。</summary>
public static class SpeakerColors
{
    // 一覧の背景で最も文字が読みにくいもの (選んだ行・マウスを載せた行の背景を含む)
    private static readonly Color LightBackground = Color.FromRgb(0xEA, 0xEA, 0xEA);
    private static readonly Color DarkBackground = Color.FromRgb(0x2D, 0x2D, 0x2D);

    /// <summary>
    /// 背景との明るさの比が 4.5 以上 (WCAG AA・普通の大きさの文字) になるまで、色合いはそのままで
    /// ライトでは暗く・ダークでは明るくする。すでに足りていればそのまま返す。
    /// </summary>
    public static Color ForText(Color color, bool dark)
    {
        var background = dark ? DarkBackground : LightBackground;
        var (h, s, l) = ToHsl(color);
        var result = color;
        for (int i = 0; i < 100 && Contrast(result, background) < 4.5; i++)
        {
            l = Math.Clamp(l + (dark ? 0.01 : -0.01), 0, 1);
            result = FromHsl(h, s, l);
        }
        return result;
    }

    /// <summary>ライト・ダークで最も読みにくい背景との明るさの比。</summary>
    public static double ContrastOnTheme(Color color, bool dark) => Contrast(color, dark ? DarkBackground : LightBackground);

    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static double Luminance(Color c)
    {
        static double Lin(byte v)
        {
            double x = v / 255.0;
            return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2;
        if (max - min < 1e-9) return (0, 0, l);
        double d = max - min;
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s < 1e-9)
        {
            byte v = (byte)Math.Round(l * 255);
            return Color.FromRgb(v, v, v);
        }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Hue(double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
        }
        return Color.FromRgb((byte)Math.Round(Hue(h + 1.0 / 3) * 255), (byte)Math.Round(Hue(h) * 255), (byte)Math.Round(Hue(h - 1.0 / 3) * 255));
    }
}

/// <summary>割合 (0〜1) と欄の幅から、棒の長さを出す。</summary>
public sealed class ShareWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [double share, double width] ? Math.Max(0, Math.Min(1, share)) * width : 0.0;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>最初の案内の手順。</summary>
public sealed record GuideStep(int Number, string Title, string Detail);

/// <summary>準備中の表示の 1 段階 (pending → active → done、失敗したら failed)。</summary>
public sealed class LoadStep(string key, string title) : Observable
{
    private string _state = "pending";
    private DateTime? _started;
    private TimeSpan? _took;

    public string Key { get; } = key;
    public string Title { get; } = title;
    public string State { get => _state; private set { Set(ref _state, value); OnChanged(nameof(Icon)); OnChanged(nameof(TimeText)); } }

    public string Icon => _state switch
    {
        "active" => "",   // 回る矢印
        "done" => "",     // チェック
        "failed" => "",   // ！
        _ => "",          // 空の丸
    };

    /// <summary>かかった時間 (読み込み中は経過時間)。</summary>
    public string TimeText => _took is { } t ? $"{t.TotalSeconds:0.0} 秒"
        : _state == "active" && _started is { } s ? $"{(DateTime.Now - s).TotalSeconds:0} 秒" : "";

    public void Start()
    {
        if (_state != "pending") return;
        _started = DateTime.Now;
        State = "active";
    }

    public void Finish()
    {
        if (_state is "done" or "failed") return;
        _took = _started is { } s ? DateTime.Now - s : null;
        State = "done";
    }

    public void Fail()
    {
        _took = null;
        State = "failed";
    }

    public void Tick()
    {
        if (_state == "active") OnChanged(nameof(TimeText));
    }

    /// <summary>見本の画面用に、状態と時間を決める。</summary>
    internal void SetDemo(string state, double seconds)
    {
        _started = DateTime.Now - TimeSpan.FromSeconds(seconds);
        _took = state == "done" ? TimeSpan.FromSeconds(seconds) : null;
        State = state;
    }
}

/// <summary>選んだウィンドウの音声 (とマイク) を文字起こしし、時刻と話者つきの議事録にする画面。</summary>
public partial class MinutesWindow : Window
{
    private static readonly string AutoSaveDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "minutes");

    private readonly TextWindow _host;
    private readonly AppSettings _settings;
    private readonly TranscriptionService _service = new();
    private readonly MinutesDocument _doc = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _autoSave = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _autoSavePath;
    private TimeSpan _recordedBefore; // 一時停止する前までの記録時間
    private bool _busy;
    private bool _captureWindowAudio;
    private DateTime _lastWindowSound;
    private bool _silenceHintShown;
    // 削除した発言 (Ctrl+Z で新しい順に戻す)
    private readonly Stack<IReadOnlyList<(int Index, MinutesEntry Entry)>> _deleted = new();
    private DateTime _fileStarted;
    // 発言時間の集計はまとめて行う (発言が続けて届くたびに数えないように)
    private readonly DispatcherTimer _talkTimes = new() { Interval = TimeSpan.FromMilliseconds(500) };
    // 日本語訳: 訳す順番待ちの発言 (書き直し中は少し待ってからまとめて訳す)
    private readonly List<MinutesEntry> _translateQueue = [];
    private readonly HashSet<MinutesEntry> _editedForTranslation = [];
    private readonly HashSet<MinutesEntry> _hooked = [];
    private readonly DispatcherTimer _translateDelay = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool _translating;
    private bool _translateErrorShown;

    public MinutesWindow(TextWindow host, AppSettings settings)
    {
        InitializeComponent();
        _host = host;
        _settings = settings;
        ApplyFontSize();
        RestorePlacement();
        PreviewKeyDown += OnWindowKeyDown;
        PreviewMouseLeftButtonDown += OnWindowPreviewMouseDown;
        HideOcrCheck.IsChecked = settings.HideOcrDuringMinutes;
        if (settings.HideOcrDuringMinutes) host.SetOcrHidden(true);
        Closed += (_, _) => _host.SetOcrHidden(false);
        EntryList.ItemsSource = _doc.Entries;
        EmptySteps.ItemsSource = new GuideStep[]
        {
            new(1, "文字起こしするアプリと言語を選ぶ",
                "会議アプリやブラウザなど。どのアプリか分からないときは「すべての音」を選んでください。"),
            new(2, "「記録を開始」を押す",
                "話している途中の文字は下に灰色で表示され、話し終わると時刻と話者つきで確定します。録画・録音済みの会議は「ファイルから…」で MP4 や MP3 などを選びます。"),
            new(3, "直して、まとめて、保存する",
                "本文はクリックして書き直せ、右クリックで削除や話者の変更ができます。「要約」で決定事項ややることをまとめ、「保存…」で Word・テキスト・字幕などに書き出せます。"),
        };
        SpeakerList.ItemsSource = _doc.Speakers;
        TranslateCheck.IsChecked = settings.MinutesTranslate;
        SpeedBox.ItemsSource = SpeedOptions;
        SpeedBox.SelectedItem = SettingsOptions.Find(SpeedOptions, settings.MinutesSpeed);
        _service.InitialSpeed = settings.MinutesSpeed;
        if (settings.MinutesCorrect is bool correct) CorrectCheck.IsChecked = correct;
        _service.SpeedStatus += (speed, gpu, fast) => Dispatcher.BeginInvoke(() => OnSpeedStatus(speed, gpu, fast));
        UpdateSpeedNote();
        SaveAudioCheck.IsChecked = settings.MinutesSaveAudio;
        RecordVideoCheck.IsChecked = settings.MinutesRecordVideo;
        SubtitleVideoCheck.IsChecked = settings.MinutesSubtitledVideo;
        SubtitleVideoCheck.IsEnabled = settings.MinutesRecordVideo;
        SpeakerCountBox.ItemsSource = new[] { "自動" }.Concat(Enumerable.Range(1, 12).Select(n => $"{n} 人")).ToList();
        SpeakerCountBox.SelectedIndex = 0;
        UpdateTermsLabel();
        UpdateVoicesLabel(MinutesDialogs.LoadVoices().Count);
        _service.VoiceMatch += (speaker, name) => Dispatcher.BeginInvoke(() => OnVoiceMatch(speaker, name));
        _service.ProcessExited += () => Dispatcher.BeginInvoke(OnProcessExited);
        _service.CaptureLost += message => Dispatcher.BeginInvoke(() =>
            ShowNotice(message + " (もう一方の音は記録を続けています)"));
        _service.CaptureEnded += message => Dispatcher.BeginInvoke(async () =>
        {
            // 取り込める音が無くなったら記録を止める (「記録中」のまま何も記録しない状態にしない)
            if (!_service.IsRecording || _busy) return;
            await StopWithBusyAsync();
            ShowNotice(message + " 記録を止めました。接続を確かめてから「記録を再開」を押してください。", "error");
        });
        _service.VoicesChanged += count => Dispatcher.BeginInvoke(() => UpdateVoicesLabel(count));
        _doc.ShowTranslations = settings.MinutesTranslate;
        _translateDelay.Tick += (_, _) =>
        {
            _translateDelay.Stop();
            foreach (var entry in _editedForTranslation) QueueTranslation(entry);
            _editedForTranslation.Clear();
        };
        _doc.Entries.CollectionChanged += (_, e) =>
        {
            _talkTimes.Stop();
            _talkTimes.Start();
            foreach (var entry in e.NewItems?.OfType<MinutesEntry>() ?? []) OnEntryAdded(entry);
            EmptyHint.Visibility = _doc.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateFillerCount();
            UpdateMarkedCount();
            if (SearchBox.Text.Trim().Length > 0) SearchCount.Text = $"{EntryList.Items.Count} 件";
        };
        foreach (var l in _languages) l.IsSelected = settings.MinutesLanguages.Contains(l.Code);
        if (!_languages.Any(l => l.IsSelected)) _languages[0].IsSelected = true;
        LanguageList.ItemsSource = _languages;
        UpdateLanguageLabel();
        AutoScrollCheck.IsChecked = settings.MinutesAutoScroll;
        TimeCheck.IsChecked = settings.MinutesShowTime;
        EntryList.Tag = settings.MinutesShowTime;
        FillerCheck.IsChecked = settings.MinutesHideFillers;
        // 相づちだけの発言は隠す (本文を書き直して相づちでなくなれば、その場で表示に戻る)
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_doc.Entries);
        view.IsLiveFiltering = true;
        view.LiveFilteringProperties.Add(nameof(MinutesEntry.IsFiller));
        view.LiveFilteringProperties.Add(nameof(MinutesEntry.IsMarked));
        view.Filter = item => item is MinutesEntry en
                              && (FillerCheck.IsChecked != true || !en.IsFiller || en.IsMarked || en == _editingEntry)
                              && (MarkedOnlyToggle.IsChecked != true || en.IsMarked)
                              && MatchesSearch(en);
        _talkTimes.Tick += (_, _) => { _talkTimes.Stop(); _doc.UpdateTalkTimes(); };
        // 書き直している発言 (相づちを除いていても、書いている途中で消えないように)
        EntryList.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (e.NewFocus is TextBox { DataContext: MinutesEntry entry }) _editingEntry = entry;
        }), true);
        EntryList.AddHandler(UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (e.OldFocus is TextBox { DataContext: MinutesEntry } && _editingEntry is { } edited)
            {
                _editingEntry = null;
                if (FillerCheck.IsChecked == true && edited.IsFiller)
                    Dispatcher.BeginInvoke(() => CollectionViewSource.GetDefaultView(_doc.Entries).Refresh(), DispatcherPriority.Background);
                UpdateFillerCount();
            }
        }), true);

        _service.SegmentReceived += segment => Dispatcher.BeginInvoke(() => OnSegment(segment));
        _service.Revised += revision => Dispatcher.BeginInvoke(() => OnRevised(revision));
        _service.Respeaker += changes => Dispatcher.BeginInvoke(() =>
        {
            if (!_dropLate && _doc.ApplySpeakerChanges(changes) > 0)
            {
                RefreshTalkTimes();
                _autoSave.Stop();
                _autoSave.Start();
            }
        });
        _service.LlmStatus += state => Dispatcher.BeginInvoke(() => OnLlmStatus(state));
        _service.Partial += (source, speaker, text) => Dispatcher.BeginInvoke(() => OnPartial(source, speaker, text));
        _service.Lag += seconds => Dispatcher.BeginInvoke(() =>
        {
            if (!_service.IsRecording) return;
            SetStatus(seconds > 0
                ? $"処理が {seconds:0} 秒遅れています (PC の負荷が高いため)。話し中の表示を省いて追いつくようにしています。"
                : "処理が追いつきました ・ 記録中");
        });
        _service.Error += message => Dispatcher.BeginInvoke(() => ShowNotice(message, "error"));
        _service.Progress += (done, total) => Dispatcher.BeginInvoke(() => OnFileProgress(done, total));
        _service.MultilingualStatus += state => Dispatcher.BeginInvoke(() => OnMultilingualStatus(state));
        _service.ForeignSpeech += () => Dispatcher.BeginInvoke(() =>
            SetStatus("日本語以外 (英語など) が話されているようです。上の「言語」を「自動」か「English」にすると、原文と日本語訳で表示します"));
        _service.Refined += () => Dispatcher.BeginInvoke(() =>
        {
            if (_doc.FromFile && !_service.IsTranscribingFile && _llmState is "gpu" or "cpu" && CorrectCheck.IsChecked == true)
                SetStatus($"文脈による補正も終わりました ・ {VisibleCount()} 発言");
        });
        _service.Level += (source, level) => Dispatcher.BeginInvoke(() =>
        {
            (source == "win" ? WindowLevel : MicLevel).Value = level;
            if (source == "win" && level > 0.02) _lastWindowSound = DateTime.Now;
            if (source == "mic" && level > 0.01) _lastMicSound = DateTime.Now;
        }, DispatcherPriority.Background);

        _clock.Tick += (_, _) =>
        {
            UpdateElapsed();
            UpdateTitle();
            CheckSilence();
            CheckMicSilence();
        };
        _autoSave.Tick += (_, _) => { _autoSave.Stop(); AutoSave(); };
        Closing += OnClosing;
        SizeChanged += (_, _) =>
        {
            FitSummaryHeight();
            FitSpeakerPanel();
        };
        Loaded += (_, _) => FitSpeakerPanel(); // 前回の話者の欄の幅にする
        Closed += (_, _) => KeepAwake(false);
        if (App.DemoMode) return; // 見本の画面: 実際のウィンドウの一覧も音声認識も使わない
        Loaded += (_, _) => RefreshWindows();

        if (!TranscriptionService.IsInstalled)
        {
            SetStatus("音声認識が未セットアップです");
            ShowNotice("議事録の音声認識がまだ入っていません。「セットアップを開く」から入れてください (数分〜数十分かかります)。",
                "warning", "セットアップを開く", OpenSetup);
            SetState("setup");
            RecordButton.IsEnabled = FileButton.IsEnabled = false;
        }
        else
            BeginLoading();
    }

    // ───────── 準備中の表示 ─────────

    private readonly System.Collections.ObjectModel.ObservableCollection<LoadStep> _loadSteps = [];
    private readonly DispatcherTimer _loadTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime _loadStarted;
    private bool _loading;

    /// <summary>音声認識のモデルを読み込み始め、終わるまで準備中の表示を出す。</summary>
    private void BeginLoading()
    {
        _loadSteps.Clear();
        _loadSteps.Add(new LoadStep("python", "音声認識の準備 (Python の起動)"));
        _loadSteps.Add(new LoadStep("asr", _settings.MinutesSpeed switch
        {
            "light" => "日本語の音声認識モデル (速いモデル)",
            "accurate" => "日本語の音声認識モデル (kotoba-whisper)",
            _ => "日本語の音声認識モデル",
        }));
        _loadSteps.Add(new LoadStep("speakers", "話者の聞き分けのモデル"));
        if (NeedsMultilingual)
            _loadSteps.Add(new LoadStep("multi", "英語などの音声認識モデル (Whisper large-v3 turbo)"));
        LoadingSteps.ItemsSource = _loadSteps;
        _loading = true;
        _loadStarted = DateTime.Now;
        _loadTimer.Tick -= OnLoadTick;
        _loadTimer.Tick += OnLoadTick;
        _loadTimer.Start();
        ActivateStep("python");
        SetState("loading");
        LoadingOverlay.Visibility = Visibility.Visible;
        SetStatus("音声認識モデルを読み込んでいます…");
        _service.LoadingStep -= OnLoadingStep;
        _service.LoadingStep += OnLoadingStep;
        _service.EnsureStartedAsync().ContinueWith(t => Dispatcher.BeginInvoke(() => OnServiceStarted(t)));
    }

    private void OnLoadingStep(string step) => Dispatcher.BeginInvoke(() => ActivateStep(step));

    /// <summary>step を読み込み中にし、それより前の段階を済みにする。</summary>
    private void ActivateStep(string key)
    {
        if (!_loading) return;
        int index = _loadSteps.ToList().FindIndex(s => s.Key == key);
        if (index < 0) return;
        for (int i = 0; i < index; i++) _loadSteps[i].Finish();
        _loadSteps[index].Start();
    }

    private void OnServiceStarted(Task t)
    {
        if (t.IsFaulted)
        {
            var message = t.Exception!.InnerException!.Message;
            _loadSteps.FirstOrDefault(s => s.State == "active")?.Fail();
            _loading = false;
            _loadTimer.Stop();
            LoadingTitle.Text = "準備できませんでした";
            SetState("error");
            LoadingNote.Text = message + "\n「もう一度試す」で直らないときは、「セットアップを開く」から入れ直してください。";
            LoadingBar.Visibility = Visibility.Collapsed;
            LoadingHideButton.Content = "閉じる";
            LoadingRetryButton.Visibility = LoadingSetupButton.Visibility = Visibility.Visible;
            SetStatus("準備できませんでした: " + message);
            return;
        }
        foreach (var s in _loadSteps.Where(s => s.Key != "multi")) s.Finish();
        if (_settings.MinutesSpeed != _service.InitialSpeedUsed) _service.SetSpeed(_settings.MinutesSpeed); // 準備中に変えた速さ
        SendMinutesOptions();
        if (_loadSteps.Any(s => s.Key == "multi") && !NeedsMultilingual)
            _loadSteps.Remove(_loadSteps.First(s => s.Key == "multi")); // 準備中に日本語だけにした・軽さ優先で動く
        if (_loadSteps.Any(s => s.Key == "multi"))
        {
            // 英語なども文字にするときは、そのモデルも読み込んでから「準備完了」にする (読み込むまで言語を判定できないため)
            ActivateStep("multi");
            _service.SetLanguages(SpeechLanguages);
            return;
        }
        FinishLoading();
    }

    private void FinishLoading()
    {
        if (!_loading) return;
        foreach (var s in _loadSteps) if (s.State != "failed") s.Finish();
        _loading = false;
        _loadTimer.Stop();
        var took = DateTime.Now - _loadStarted;
        _settings.MinutesLoadSeconds = Math.Round(took.TotalSeconds);
        _settings.Save();
        LoadingTitle.Text = "準備ができました";
        if (!_service.IsRecording && !_service.IsTranscribingFile) SetState("ready");
        LoadingBar.Visibility = Visibility.Collapsed;
        if (!_service.IsRecording && !_service.IsTranscribingFile)
            SetStatus($"準備完了 ({(_service.Device == "cuda" ? "GPU" : "CPU")}・{(_service.Speed == "light" ? "軽さ優先" : "正確さ優先")}・{took.TotalSeconds:0} 秒) ・ 音を選んで「記録を開始」を押してください");
        UpdateSpeedNote();
        // すべてに印が付いたところを一瞬見せてから閉じる
        var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        close.Tick += (_, _) =>
        {
            close.Stop();
            LoadingOverlay.Visibility = Visibility.Collapsed;
        };
        close.Start();
    }

    private void OnLoadTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.Now - _loadStarted;
        foreach (var s in _loadSteps) s.Tick();
        var previous = _settings.MinutesLoadSeconds is { } p
            ? $" ・ 前回は約 {TimeSpan.FromSeconds(p):m\\:ss}"
            : " ・ 初めてのときは 1〜2 分かかることがあります";
        LoadingElapsed.Text = $"経過 {elapsed:m\\:ss}{previous}";
    }

    /// <summary>まだ準備中なら、準備中の表示をもう一度出す (閉じていても)。</summary>
    private void ShowLoadingIfBusy(string note)
    {
        if (!_loading) return;
        LoadingNote.Text = "音声認識の AI を読み込んでいます。" + note;
        LoadingOverlay.Visibility = Visibility.Visible;
    }

    private void LoadingRetry_Click(object sender, RoutedEventArgs e)
    {
        LoadingRetryButton.Visibility = LoadingSetupButton.Visibility = Visibility.Collapsed;
        LoadingHideButton.Content = "裏で準備";
        LoadingBar.Visibility = Visibility.Visible;
        LoadingTitle.Text = "議事録の準備をしています";
        LoadingNote.Text = "音声認識の AI を読み込んでいます。準備ができると、この表示は自動で消えます。";
        _service.Restart(); // 途中まで起動した処理を止めてから起動し直す
        BeginLoading();
    }

    private void OpenSetup_Click(object sender, RoutedEventArgs e) => OpenSetup();

    /// <summary>設定の「情報」(セットアップ) を開く。</summary>
    private void OpenSetup() => _host.OpenSettings(5);

    private void LoadingHide_Click(object sender, RoutedEventArgs e)
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        if (_loading) SetStatus("裏で準備しています… (終わるとここに「準備完了」と出ます。準備中に「記録を開始」を押すと、準備ができしだい記録を始めます)");
    }

    internal void SetStatus(string text) => StatusText.Text = text;

    /// <summary>
    /// 記録ボタンの下の、いまの状態の印。loading (準備中)・ready (準備完了)・recording (記録中)・file (文字起こし中)・
    /// error (エラー)・setup (未セットアップ)。text を渡すとその言葉にする。
    /// </summary>
    private void SetState(string state, string? text = null)
    {
        _state = state;
        if (!App.DemoMode) Dispatcher.BeginInvoke(_host.MinutesStateChanged); // 機能を選ぶ画面の「記録中」
        var (color, label) = state switch
        {
            "loading" => ("#D97706", "準備中"),
            "ready" when text != null => ("#6B7280", text),
            "ready" => ("#16A34A", "準備完了"),
            "recording" => ("#DC2626", "記録中"),
            "file" => ("#2563EB", "文字起こし中"),
            "error" => ("#DC2626", "止まりました"),
            _ => ("#808080", "未セットアップ"),
        };
        var c = (Color)ColorConverter.ConvertFromString(color);
        StateDot.Fill = new SolidColorBrush(c);
        StateBadge.Background = new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B));
        StateText.Text = text ?? label;
        System.Windows.Automation.AutomationProperties.SetName(StateBadge, "状態: " + StateText.Text);
        bool busy = state is "recording" or "file";
        KeepAwake(busy);
        Taskbar.ProgressState = state switch
        {
            "recording" => System.Windows.Shell.TaskbarItemProgressState.Error, // 赤い印 (記録中)
            "file" => System.Windows.Shell.TaskbarItemProgressState.Normal,
            _ => System.Windows.Shell.TaskbarItemProgressState.None,
        };
        if (state == "recording") Taskbar.ProgressValue = 1;
        UpdateTitle();
        StateBadge.ToolTip = state switch
        {
            "loading" => "音声認識の AI を読み込んでいます。準備中に「記録を開始」を押すと、準備ができしだい記録を始めます",
            "ready" => "「記録を開始」を押すと、選んだアプリの音を文字にします",
            "recording" => "記録しています。話し終わってから数秒で文字になります",
            "file" => "ファイルを文字起こししています",
            "error" => "文字起こしの処理が止まりました。もう一度「記録を開始」を押すと起動し直します",
            _ => "設定 → セットアップ・情報 → 「セットアップを実行」で音声認識を入れてください",
        };
    }

    private string _state = "loading";

    private void UpdateTitle() => Title = _state switch
    {
        "recording" => $"● 記録中 {ElapsedText.Text} — GetText 議事録",
        "file" => $"文字起こし中 {(FileProgress.Value > 0 ? $"{FileProgress.Value:P0} " : "")}— GetText 議事録",
        _ => "GetText — 議事録",
    };

    private bool _awake;

    /// <summary>記録中・文字起こし中は PC がスリープしないようにする (画面は消えてもよい)。</summary>
    private void KeepAwake(bool on)
    {
        if (on == _awake || App.DemoMode) return;
        _awake = on;
        if (on) PowerRequest.Acquire(this); // 録画と同時でも、両方が終わるまでスリープしない
        else PowerRequest.Release(this);
    }

    // ───────── 大事な知らせ (警告・エラー) ─────────

    private Action? _noticeAction;

    /// <summary>
    /// 警告やエラーを、下の欄の上に目立つ帯で出す (閉じるまで残る)。severity は info / warning / error。
    /// action を渡すと、そのボタンも出す。
    /// </summary>
    private string _noticeKind = "";

    private void ShowNotice(string text, string severity = "warning", string? action = null, Action? onAction = null, string kind = "")
    {
        _noticeKind = kind;
        var (icon, color) = severity switch
        {
            "error" => ("\uEA39", "#C42B1C"),
            "info" => ("\uE946", "#0067C0"),
            _ => ("\uE7BA", "#9D5D00"),
        };
        var c = (Color)ColorConverter.ConvertFromString(color);
        NoticeBar.Background = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
        NoticeBar.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B));
        bool dark = TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush fg && SpeakerColors.Luminance(fg.Color) > 0.5;
        NoticeIcon.Foreground = new SolidColorBrush(dark ? SpeakerColors.ForText(c, dark: true) : c);
        NoticeIcon.Text = icon;
        NoticeText.Text = text;
        _noticeAction = onAction;
        NoticeAction.Content = action;
        NoticeAction.Visibility = action != null && onAction != null ? Visibility.Visible : Visibility.Collapsed;
        NoticeBar.Visibility = Visibility.Visible;
        if (severity != "info") App.Log("MinutesNotice", new Exception(text));
    }

    private void HideNotice() => NoticeBar.Visibility = Visibility.Collapsed;

    /// <summary>その種類の知らせが出ているときだけ消す。</summary>
    private void HideNotice(string kind)
    {
        if (_noticeKind == kind) HideNotice();
    }

    private void NoticeClose_Click(object sender, RoutedEventArgs e) => HideNotice();

    private void NoticeAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _noticeAction;
        HideNotice();
        action?.Invoke();
    }

    // ───────── 開く欄 (言語・詳細・表示) ─────────

    // 開いたら中の最初の項目にフォーカスを移す (キーボードだけでも選べるように)
    private void Popup_Opened(object? sender, EventArgs e)
    {
        if (sender == OptionsPopup) FillMicrophones(); // マイクを挿したり外したりしていることがあるので、開くたびに読み直す
        if (sender is not System.Windows.Controls.Primitives.Popup { Child: { } child }) return;
        Dispatcher.BeginInvoke(() => child.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Input);
    }

    // Esc で閉じて、開いたボタンにフォーカスを戻す
    private void Popup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not System.Windows.Controls.Primitives.Popup popup) return;
        popup.IsOpen = false;
        (popup.PlacementTarget as UIElement)?.Focus();
        e.Handled = true;
    }

    // ───────── マイク ─────────

    private bool _fillingMics;

    private void FillMicrophones()
    {
        _fillingMics = true;
        try
        {
            // 見本の画面 (操作手順の動画など) には、この PC のマイクの名前を出さない
            var (devices, defaultName) = App.DemoMode
                ? ((IReadOnlyList<(string Id, string Name)>)[("demo", "マイク (USB マイク)")], "マイク (USB マイク)")
                : AudioSources.ListMicrophones();
            var items = new List<Option<string>> { new(defaultName != null ? $"既定の通信デバイス ({defaultName})" : "既定の通信デバイス", "") };
            items.AddRange(devices.Select(d => new Option<string>(d.Name, d.Id)));
            // 選んでいたマイクが外されていても、一覧には残して分かるようにする
            if (_settings.MinutesMicDevice.Length > 0 && devices.All(d => d.Id != _settings.MinutesMicDevice))
                items.Add(new Option<string>("選んでいたマイク (今は接続されていません。既定を使います)", _settings.MinutesMicDevice));
            MicBox.ItemsSource = items;
            MicBox.SelectedItem = items.FirstOrDefault(o => o.Value == _settings.MinutesMicDevice) ?? items[0];
            MicBox.IsEnabled = !_service.IsRecording;
            MicBox.ToolTip = _service.IsRecording ? "記録を止めてから変えられます" : null;
        }
        finally
        {
            _fillingMics = false;
        }
    }

    private void MicBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingMics || MicBox.SelectedItem is not Option<string> o || o.Value == _settings.MinutesMicDevice) return;
        _settings.MinutesMicDevice = o.Value;
        _settings.Save();
        SetStatus(o.Value.Length == 0 ? "マイク: 既定の通信デバイスを使います" : $"マイク: {o.Label} を使います");
    }

    // ───────── 言語 ─────────

    /// <summary>選べる言語 (Whisper の言語コード)。</summary>
    public sealed class LanguageChoice(string code, string label) : Observable
    {
        private bool _selected;
        public string Code { get; } = code;
        public string Label { get; } = label;
        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    }

    private readonly LanguageChoice[] _languages =
    [
        new("ja", "日本語"), new("en", "英語"), new("zh", "中国語"), new("ko", "韓国語"),
        new("es", "スペイン語"), new("fr", "フランス語"), new("de", "ドイツ語"), new("pt", "ポルトガル語"),
        new("it", "イタリア語"), new("ru", "ロシア語"), new("vi", "ベトナム語"), new("th", "タイ語"),
        new("id", "インドネシア語"), new("hi", "ヒンディー語"), new("ar", "アラビア語"),
    ];

    private List<string> SpeechLanguages => _languages.Where(l => l.IsSelected).Select(l => l.Code).ToList();

    private void UpdateLanguageLabel()
    {
        var names = _languages.Where(l => l.IsSelected).Select(l => l.Label).ToList();
        LanguageLabel.Text = names.Count <= 3 ? string.Join("・", names) : $"{string.Join("・", names.Take(2))} など {names.Count} 言語";
    }

    private void LanguageCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!_languages.Any(l => l.IsSelected) && (sender as CheckBox)?.DataContext is LanguageChoice c)
        {
            c.IsSelected = true; // 少なくとも 1 つは選ぶ
            SetStatus("言語は少なくとも 1 つ選んでください");
            return;
        }
        UpdateLanguageLabel();
        var langs = SpeechLanguages;
        _settings.MinutesLanguages = langs;
        _settings.Save();
        if (langs.Any(l => l != "ja") && !TranscriptionService.IsMultilingualInstalled)
        {
            SetStatus("日本語以外の音声認識モデルが未セットアップです。設定 → セットアップ・情報 → 「セットアップを実行」で入ります (約 1.6GB)。それまでは日本語として文字にします");
            return;
        }
        _service.SetLanguages(langs); // 記録中でも、次の発言から切り替わる
        SetStatus(langs.Count == 1
            ? $"{LanguageLabel.Text}で文字にします"
            : $"発言ごとに {LanguageLabel.Text} のどれかを判定して文字にします (選んでいない言語にはなりません)");
    }

    private void OnMultilingualStatus(string state)
    {
        if (_loading && state != "loading" && _loadSteps.FirstOrDefault(s => s.Key == "multi") is { } step)
        {
            if (state == "missing") step.Fail();
            FinishLoading();
            if (state == "ready") return;
        }
        if (state == "loading") SetStatus("英語などの音声認識モデルを読み込んでいます…");
        else if (state == "missing") SetStatus("英語などの音声認識モデルがありません。設定 → セットアップ・情報 → 「セットアップを実行」で入ります。いまは日本語として文字にしています");
        else if (state == "ready" && !_service.IsRecording && !_service.IsTranscribingFile) SetStatus("英語などの音声認識モデルの準備ができました");
    }

    // ───────── ウィンドウの選択 ─────────

    private void RefreshWindows()
    {
        var selected = WindowBox.SelectedItem as WindowInfo;
        var items = new List<WindowInfo> { WindowInfo.AllAudio };
        items.AddRange(WindowList.GetWindows());
        WindowBox.ItemsSource = items;
        // 前に選んでいたもの → いま音を出しているウィンドウ → すべての音
        WindowBox.SelectedItem = (selected is { IsAll: true } ? WindowInfo.AllAudio : null)
                                 ?? items.FirstOrDefault(w => !w.IsAll && w.ProcessId == selected?.ProcessId)
                                 ?? items.FirstOrDefault(w => w.IsPlaying)
                                 ?? WindowInfo.AllAudio;
    }

    /// <summary>選んだウィンドウから音が来ないときは、選び直しを案内する。</summary>
    private void CheckSilence()
    {
        if (!_service.IsRecording || !_captureWindowAudio || _silenceHintShown) return;
        if (DateTime.Now - _lastWindowSound < TimeSpan.FromSeconds(8)) return;
        _silenceHintShown = true;
        // ほかに音を出しているアプリがあれば名前を出す (会議アプリの音が別のアプリから出ていることがある)
        string? other = null;
        try
        {
            var playing = WindowList.GetWindows().Where(w => w.IsPlaying && w.ProcessId != (WindowBox.SelectedItem as WindowInfo)?.ProcessId).Select(w => w.ProcessName).Distinct().Take(2).ToList();
            if (playing.Count > 0) other = string.Join("・", playing);
        }
        catch
        {
            // 調べられなくても案内は出す
        }
        ShowNotice(other != null
            ? $"選んだアプリから音が来ていません。いま音を出しているのは {other} です。停止して、🔊 の付いたアプリか「すべての音」を選び直してください。"
            : "選んだアプリから音が来ていません。会議の音が出ているか確かめ、停止して、🔊 の付いたアプリか「すべての音」を選び直してください。");
    }

    /// <summary>マイクの音が来ないときは、マイクの設定を案内する (ミュート・別のマイクを使っているなど)。</summary>
    private void CheckMicSilence()
    {
        if (!_service.IsRecording || !_captureMic || _micHintShown) return;
        if (DateTime.Now - _lastMicSound < TimeSpan.FromSeconds(30)) return;
        _micHintShown = true;
        ShowNotice("マイクから 30 秒以上音が来ていません。マイクがミュートになっていないか確かめてください。会議アプリで別のマイク (ヘッドセットなど) を使っているときは、" +
                   "記録を止めて「⚙ 詳細」→「マイク」でそのマイクを選んでください。", "warning", "マイクを選ぶ",
            () => OptionsButton.IsChecked = true);
    }

    private bool _captureMic;
    private bool _micHintShown;
    private DateTime _lastMicSound;

    private void HideOcr_Click(object sender, RoutedEventArgs e)
    {
        _settings.HideOcrDuringMinutes = HideOcrCheck.IsChecked == true;
        _settings.Save();
        _host.SetOcrHidden(_settings.HideOcrDuringMinutes);
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    // ───────── 記録 ─────────

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _service.IsTranscribingFile) return;
        if (_savingProject && !_service.IsRecording)
        {
            SetStatus("プロジェクトの保存・読み込みの途中です。終わってから記録を始めてください");
            return;
        }
        _busy = true;
        RecordButton.IsEnabled = false;
        FileButton.IsEnabled = false; // 記録の準備中・停止中はファイルを選べない
        try
        {
            if (_service.IsRecording) await StopAsync();
            else await StartAsync();
        }
        finally
        {
            RecordButton.IsEnabled = true;
            FileButton.IsEnabled = !_service.IsRecording;
            _busy = false;
        }
    }

    private async Task StartAsync()
    {
        var window = WindowBox.SelectedItem as WindowInfo;
        bool mic = MicCheck.IsChecked == true;
        if (window == null && !mic)
        {
            SetStatus("文字起こしするアプリを選ぶか、マイクを入れてください");
            return;
        }
        if (_doc.FromFile && _doc.Entries.Count > 0 &&
            MessageBox.Show(this, "今の議事録 (ファイルから作ったもの) を閉じて、新しく記録を始めますか？\n(今の内容は自動保存されています)",
                "GetText — 議事録", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        SetStatus("記録を準備しています…");
        ShowLoadingIfBusy("準備ができしだい、記録を始めます。");
        try
        {
            if (_doc.FromFile)
            {
                // ファイルから作った議事録に録音を足すと時刻が混ざるので、新しい議事録にする
                AutoSave();
                ResetDocument();
            }
            bool newSession = !_doc.Entries.Any(en => !en.IsNote); // メモしか無ければ新しい会議
            _service.RecordAudioPath = !App.DemoMode && SaveAudioCheck.IsChecked == true ? NewAudioPath(newSession) : null;
            _dropLate = false;
            AudioSources.MicrophoneDevice = _settings.MinutesMicDevice;
            if (window is { IsAll: true })
                await _service.StartAsync(Environment.ProcessId, excludeProcess: true, mic, newSession);
            else
                await _service.StartAsync(window?.ProcessId, excludeProcess: false, mic, newSession);
            _service.SetSpeakerSensitivity(ThresholdSlider.Value);
            _service.SetContextCorrection(CorrectCheck.IsChecked == true);
            _service.SetLanguages(SpeechLanguages);
            SendMinutesOptions();
            if (newSession)
            {
                if (_doc.Entries.Count == 0 || _doc.StartedAt == null) _doc.StartedAt = _service.SessionStart;
                _doc.Source = window?.Title ?? "マイクのみ";
                _autoSavePath = null;
            }
        }
        catch (Exception ex)
        {
            if (_closed) return; // 準備中に閉じた
            App.Log("Minutes", ex);
            SetStatus("記録を始められませんでした: " + ex.Message);
            MessageBox.Show(this, "記録を始められませんでした。\n\n" + ex.Message, "GetText — 議事録",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _captureWindowAudio = window != null;
        _lastWindowSound = DateTime.Now;
        _silenceHintShown = false;
        _captureMic = mic;
        _lastMicSound = DateTime.Now;
        _micHintShown = false;
        if (_noticeKind != "autosave") HideNotice();
        WindowBox.IsEnabled = MicCheck.IsEnabled = FileButton.IsEnabled = SaveAudioCheck.IsEnabled = RecordVideoCheck.IsEnabled = false;
        await StartVideoAsync(window, mic);
        if (!_service.IsRecording || IsStopping || _closed) return; // (録画を始めている間に止められた・閉じられた)
        RecordIcon.Text = ""; // 停止
        ShowPartialArea(true, window: window != null, mic: mic);
        SetState("recording");
        RecordLabel.Text = "記録を停止";
        _clock.Start();
        var name = window == null ? "" : window.IsAll ? "すべての音" : window.ProcessName;
        SetStatus($"記録中 ・ {name}{(window != null && mic ? " + " : "")}{(mic ? "マイク" : "")} ・ 話し終わってから数秒で文字になります");
    }

    /// <summary>止め終わるまで (最大 30 秒) 記録・ファイルのボタンを押せないようにして止める。</summary>
    private async Task StopWithBusyAsync()
    {
        _busy = true;
        RecordButton.IsEnabled = FileButton.IsEnabled = false;
        try
        {
            await StopAsync();
        }
        finally
        {
            _busy = false;
            RecordButton.IsEnabled = true;
            FileButton.IsEnabled = !_service.IsRecording;
        }
    }

    /// <summary>記録を止めて、残りの音声を文字にしている途中 (最大 30 秒)。この間は IsRecording が false になる。</summary>
    public bool IsStopping { get; private set; }

    /// <summary>止めている途中なら、終わるまで待つ (閉じる・終了する前に、最後の発言と音声を議事録に入れるため)。</summary>
    public async Task WaitStoppedAsync()
    {
        // (残りの音声を文字にする: 最大 30 秒 + 録画を書き終える: 最大 30 秒)
        for (int i = 0; IsStopping && i < 450; i++) await Task.Delay(200);
    }

    /// <summary>終了の前に待つ仕事 (止めている途中・プロジェクトの保存の途中) がある。</summary>
    public bool HasPendingWork => IsStopping || _savingProject;

    /// <summary>終了の前に待つ仕事が終わるまで待つ (プロジェクトの保存は長い動画で数分かかることがある)。</summary>
    public async Task WaitPendingAsync()
    {
        _closingWindow = true; // (GetText を終えるので、字幕つきの動画は作らない)
        await WaitStoppedAsync();
        while (_savingProject) await Task.Delay(200);
    }

    private async Task StopAsync()
    {
        _recordedBefore += DateTime.Now - _service.SessionStart;
        _clock.Stop();
        SetStatus("残りの音声を文字にしています…");
        IsStopping = true;
        // 会議の画面の録画は、押したときに止める (残りの音声を文字にし終えるのを待つと、その分も録画してしまう)
        var video = StopVideoRecorderAsync();
        try
        {
            await _service.StopAsync();
        }
        finally
        {
            await video;
            IsStopping = false;
            FinishVideoParts(); // (止めるのに失敗しても、この記録の録画を次の記録に持ち越さない)
        }
        OnRecordingSaved(_service.LastRecording);
        RecordVideoCheck.IsEnabled = true;
        WindowLevel.Value = MicLevel.Value = 0;
        ShowPartialArea(false);
        WindowBox.IsEnabled = MicCheck.IsEnabled = FileButton.IsEnabled = SaveAudioCheck.IsEnabled = true;
        RecordIcon.Text = ""; // 録音
        SetState("ready", "停止中");
        RecordLabel.Text = "記録を再開";
        AutoSave();
        SetStatus($"停止しました ・ {VisibleCount()} 発言" + (_autoSavePath != null ? $" ・ 自動保存: {_autoSavePath}" : ""));
    }

    // ───────── ファイルの文字起こし ─────────

    private bool _fileCancelRequested;

    private async void File_Click(object sender, RoutedEventArgs e)
    {
        if (_busy && FileLabel.Text == "中止" && !_service.IsTranscribingFile)
        {
            _fileCancelRequested = true; // 準備中に中止された (準備ができても始めない)
            FileButton.IsEnabled = false;
            SetStatus("中止しています…");
            return;
        }
        if (_service.IsTranscribingFile)
        {
            _service.CancelFile();
            FileButton.IsEnabled = false;
            SetStatus("中止しています…");
            return;
        }
        if (_service.IsRecording || _busy) return;
        if (_savingProject)
        {
            SetStatus("プロジェクトの保存・読み込みの途中です。終わってからファイルを選んでください");
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "文字起こしする動画・音声ファイル",
            Filter = TranscriptionService.FileFilter,
        };
        if (dialog.ShowDialog(this) != true) return;
        if (_doc.Entries.Count > 0 &&
            MessageBox.Show(this, "今の議事録を閉じて、選んだファイルから新しい議事録を作りますか？\n(今の内容は自動保存されています)",
                "GetText — 議事録", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        AutoSave();
        ResetDocument();
        _doc.FromFile = true;
        _doc.Source = Path.GetFileName(dialog.FileName);
        _doc.SourcePath = dialog.FileName;

        _busy = true;
        _fileCancelRequested = false;
        RecordButton.Visibility = Visibility.Collapsed;
        WindowBox.IsEnabled = MicCheck.IsEnabled = false;
        FileIcon.Text = "\uE711"; // 中止
        SetState("file");
        FileLabel.Text = "中止";
        FileProgress.Value = 0;
        FileProgress.Visibility = Visibility.Visible;
        SetStatus($"「{_doc.Source}」を読み込んでいます…");
        ShowLoadingIfBusy("準備ができしだい、ファイルの文字起こしを始めます。");
        _fileStarted = DateTime.Now;
        _progressBase = null;
        try
        {
            await _service.EnsureStartedAsync();
            if (_fileCancelRequested || _closed)
            {
                SetStatus("ファイルの文字起こしを中止しました");
                return;
            }
            // 前の会議の話者の記憶を消してから、設定を送る (設定はファイルの処理より先に。処理中は後回しになるため)
            _service.ResetSession();
            _dropLate = false;
            _service.SetSpeakerSensitivity(ThresholdSlider.Value);
            _service.SetContextCorrection(CorrectCheck.IsChecked == true);
            _service.SetLanguages(SpeechLanguages);
            SendMinutesOptions();
            var (duration, cancelled) = await _service.TranscribeFileAsync(dialog.FileName);
            _doc.Duration = TimeSpan.FromSeconds(duration);
            AutoSave();
            var took = DateTime.Now - _fileStarted;
            SetStatus(cancelled
                ? $"中止しました ・ そこまでの {VisibleCount()} 発言を残しています"
                : $"完了しました ・ {VisibleCount()} 発言 ・ 話者 {_doc.Speakers.Count} 人 ・ 処理 {took:m\\:ss} (音声 {_doc.Duration:h\\:mm\\:ss})"
                  + (CorrectCheck.IsChecked == true && _llmState is "gpu" or "cpu" ? " ・ 文脈による補正は続けて行います" : ""));
        }
        catch (Exception ex)
        {
            App.Log("MinutesFile", ex);
            ShowNotice(ex.Message.StartsWith("ファイル") ? ex.Message : "ファイルを文字起こしできませんでした: " + ex.Message, "error");
        }
        finally
        {
            _busy = false;
            RecordButton.Visibility = Visibility.Visible;
            WindowBox.IsEnabled = MicCheck.IsEnabled = FileButton.IsEnabled = true;
            FileIcon.Text = "\uE8E5";
            if (_state != "error") SetState(_loading ? "loading" : "ready");
            FileLabel.Text = "ファイルから…";
            FileProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnFileProgress(double done, double total)
    {
        if (!_service.IsTranscribingFile || total <= 0) return;
        FileProgress.Value = done / total;
        Taskbar.ProgressValue = done / total;
        UpdateTitle();
        ElapsedText.Text = $"{TimeSpan.FromSeconds(done):h\\:mm\\:ss} / {TimeSpan.FromSeconds(total):h\\:mm\\:ss}";
        // 残り時間は、処理が進み始めてからの速さで見積もる (ファイルやモデルの読み込みにかかった時間を含めると、
        // 始めた直後に「残り 3000 分」のような大きすぎる値になるため)。速さが落ち着くまでは出さない
        var now = DateTime.Now;
        if (_progressBase is not { } b || done < b.Done)
        {
            _progressBase = (now, done);
            SetStatus($"ファイルを文字起こししています {done / total:P0} ・ 残り時間を計算しています…");
            return;
        }
        double seconds = (now - b.Time).TotalSeconds, processed = done - b.Done;
        string rest = "残り時間を計算しています…";
        if (seconds >= 20 && processed > 0)
        {
            double speed = processed / seconds; // 1 秒あたりに処理できた音声の秒数
            var remain = TimeSpan.FromSeconds((total - done) / speed);
            rest = (remain.TotalMinutes >= 90 ? $"残り約 {remain.TotalHours:0.0} 時間" : $"残り約 {Math.Max(1, Math.Ceiling(remain.TotalMinutes)):0} 分")
                   + $" (音声の {speed:0.#} 倍の速さ)";
        }
        SetStatus($"ファイルを文字起こししています {done / total:P0} ・ {rest}");
    }

    private (DateTime Time, double Done)? _progressBase;

    private void UpdateElapsed()
    {
        var total = _recordedBefore + (_service.IsRecording ? DateTime.Now - _service.SessionStart : TimeSpan.Zero);
        ElapsedText.Text = total.ToString(@"hh\:mm\:ss");
    }

    private void OnSegment(TranscriptSegment segment)
    {
        if (_dropLate) return; // 新規・開くの後に、前の記録の結果が遅れて届いたもの
        _doc.Add(segment.SessionStart ?? _service.SessionStart, segment);
        if (_voiceNames.TryGetValue(segment.Speaker, out var voiceName)) ApplyVoiceName(segment.Speaker, voiceName);
        ScrollToLatest();
        _autoSave.Stop();
        _autoSave.Start();
    }

    /// <summary>
    /// 記録中は暫定の文字の欄を出しておく (取り込む音ごとに 1 行。高さを変えないので、一覧の最新の発言が隠れない)。
    /// show が false なら閉じる。
    /// </summary>
    private void ShowPartialArea(bool show, bool window = false, bool mic = false)
    {
        WinPartial.Text = MicPartial.Text = "";
        _partialText.Clear();
        WinPartial.Visibility = show && window ? Visibility.Visible : Visibility.Collapsed;
        MicPartial.Visibility = show && mic ? Visibility.Visible : Visibility.Collapsed;
        PartialPanel.Visibility = show && (window || mic) ? Visibility.Visible : Visibility.Collapsed;
        ScrollToLatest();
    }

    private readonly Dictionary<string, (string Name, string Text)> _partialText = [];

    /// <summary>話している途中の暫定の文字を下の欄に出す (長いときは最後の部分だけ。確定したら消える)。</summary>
    private void OnPartial(string source, int speaker, string text)
    {
        var line = source == "mic" ? MicPartial : WinPartial;
        if (text.Length == 0)
        {
            _partialText.Remove(source);
            line.Text = "";
            return;
        }
        var name = _doc.Speakers.FirstOrDefault(s => s.Id == speaker)?.Name
                   ?? (speaker == 0 ? MinutesSpeaker.DefaultName(0) : "話者");
        _partialText[source] = (name, text);
        if (PartialPanel.Visibility != Visibility.Visible)
        {
            // (記録の始まりの前に届いたときなど) 欄を出す
            line.Visibility = Visibility.Visible;
            PartialPanel.Visibility = Visibility.Visible;
            ScrollToLatest();
        }
        line.Text = FitTail(line, $"{name}: ", text);
    }

    /// <summary>
    /// 1 行に収まるよう、話している文字の終わりの方だけを残す (話している所が見えるように)。
    /// </summary>
    private static string FitTail(TextBlock line, string prefix, string text)
    {
        double width = line.ActualWidth > 0 ? line.ActualWidth : 400;
        var typeface = new Typeface(line.FontFamily, line.FontStyle, line.FontWeight, line.FontStretch);
        double dpi = VisualTreeHelper.GetDpi(line).PixelsPerDip;
        double Measure(string s) =>
            new FormattedText(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, line.FontSize, Brushes.Black, dpi).Width;
        var full = prefix + text + "…";
        if (Measure(full) <= width) return full;
        // 収まる最大の長さを二分探索で探す
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Measure(prefix + "…" + text[^mid..] + "…") <= width) lo = mid;
            else hi = mid - 1;
        }
        return prefix + "…" + text[^lo..] + "…";
    }

    private void OnRevised(TranscriptRevision revision)
    {
        if (_dropLate) return;
        if (_doc.Revise(revision) == null) return;
        ScrollToLatest();
        UpdateFillerCount();
        _autoSave.Stop();
        _autoSave.Start();
        if (revision.Stage == "llm") _corrections++;
        UpdateCorrectionStatus();
    }

    private int _corrections;
    private string _llmState = "";

    private void OnLlmStatus(string state)
    {
        _llmState = state;
        UpdateCorrectionStatus();
    }

    private void UpdateCorrectionStatus()
    {
        CorrectCheck.IsEnabled = _llmState is not ("unavailable" or "off");
        CorrectCheck.Content = _llmState switch
        {
            "loading" => "文脈で聞き間違いを補正 (準備中…)",
            "gpu" or "cpu" => $"文脈で聞き間違いを補正{(_corrections > 0 ? $" ({_corrections} 件)" : "")}",
            "unavailable" => "文脈で聞き間違いを補正 (使えません)",
            "off" => "文脈で聞き間違いを補正 (未セットアップ)",
            _ => "文脈で聞き間違いを補正",
        };
        CorrectCheck.ToolTip = _llmState switch
        {
            "gpu" => "前後の発言から、聞き間違えた語を AI (Qwen3-4B, GPU) が直します。直した発言には印が付きます",
            "cpu" => "前後の発言から、聞き間違えた語を AI (Qwen3-4B, CPU) が直します。GPU のメモリが足りないため CPU で動いています",
            "unavailable" => "AI のモデルを読み込めませんでした",
            "off" => "設定 → セットアップ・情報 → セットアップを実行 で入ります",
            _ => CorrectCheck.ToolTip,
        };
    }

    private void Correct_Click(object sender, RoutedEventArgs e)
    {
        _settings.MinutesCorrect = CorrectCheck.IsChecked == true; // 手で選んだら、速さを変えてもそのまま
        _settings.Save();
        _service.SetContextCorrection(CorrectCheck.IsChecked == true);
    }

    // ───────── 速さ ─────────

    public static readonly Option<string>[] SpeedOptions =
    [
        new("自動 (GPU があれば正確さ優先、無ければ軽さ優先)", "auto"),
        new("軽さ優先 — GPU の無い PC でも遅れない", "light"),
        new("正確さ優先 — GPU のある PC 向け", "accurate"),
    ];

    /// <summary>英語などのモデルを使うか (軽さ優先で日本語も選んでいるときは、日本語だけを文字にするので使わない)。</summary>
    private bool NeedsMultilingual =>
        SpeechLanguages.Any(l => l != "ja") && !(LightNow && SpeechLanguages.Contains("ja"));

    // いま軽さ優先で動いているか (起動前は設定から推し量る)
    private bool LightNow => _service.IsReady ? _service.Speed == "light"
        : _settings.MinutesSpeed == "light" || (_settings.MinutesSpeed == "auto" && _service.Speed == "light");

    private void SpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedBox.SelectedItem is not Option<string> o || o.Value == _settings.MinutesSpeed) return;
        _settings.MinutesSpeed = o.Value;
        _settings.Save();
        _service.InitialSpeed = o.Value;
        if (_service.IsReady) _service.SetSpeed(o.Value);
        UpdateSpeedNote();
    }

    private void OnSpeedStatus(string speed, bool gpu, bool fast)
    {
        _speedGpu = gpu;
        _speedFastInstalled = fast;
        // 文脈による補正: 手で選んでいなければ、正確さ優先ならオン、軽さ優先ならオフ (CPU では補正の AI が音声認識と CPU を取り合って遅れるため)
        if (_settings.MinutesCorrect == null)
        {
            bool on = speed == "accurate";
            if (CorrectCheck.IsChecked != on)
            {
                CorrectCheck.IsChecked = on;
                _service.SetContextCorrection(on);
            }
        }
        if (!NeedsMultilingual && _loading && _loadSteps.FirstOrDefault(s => s.Key == "multi") is { } multi && multi.State != "active")
            _loadSteps.Remove(multi);
        UpdateSpeedNote();
        if (speed == "light" && SpeechLanguages.Any(l => l != "ja") && SpeechLanguages.Contains("ja") && !_service.IsRecording)
            SetStatus("軽さ優先で動いています。英語なども選んでいますが、日本語だけを文字にします (英語なども文字にするときは「詳細」→ 速さ を「正確さ優先」に)");
    }

    private bool _speedGpu;
    private bool _speedFastInstalled = TranscriptionService.IsFastInstalled;

    private void UpdateSpeedNote()
    {
        string now = !_service.IsReady ? ""
            : _service.Speed == "light" ? "いまは軽さ優先で動いています (速い音声認識。「、」「。」は話の間 (ま) と言い回しから付けます。「？」は付きません)。"
            : _service.HasGpu ? "いまは正確さ優先で動いています (GPU)。"
            : "いまは正確さ優先で動いています。GPU が無いので、話し終わってから文字になるまで時間がかかります。";
        string missing = _speedFastInstalled ? "" : " 軽さ優先の音声認識が未セットアップです (設定 → セットアップ・情報 → 「セットアップを実行」で入ります。約 160MB)。";
        SpeedNote.Text = (now + missing).Trim();
    }

    private ScrollViewer? _entryScroll;

    /// <summary>
    /// 自動スクロールがオンなら一番下 (最新) までスクロールする。
    /// 訳や補正で発言の高さが変わるので、並べ直しが終わってから動かす。発言を書き直している間は動かさない。
    /// </summary>
    private void ScrollToLatest()
    {
        if (AutoScrollCheck.IsChecked != true || SearchBox.Text.Trim().Length > 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (Keyboard.FocusedElement is TextBox box && EntryList.IsAncestorOf(box)) return;
            HookEntryScroll();
            _entryScroll?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private bool _atBottom = true;

    /// <summary>
    /// 一覧の高さが変わったとき (下の欄が出た・窓の大きさを変えた・訳が付いた) も、最新を見ていたなら最新を見せ続ける
    /// (変わった分だけ最新の発言が下に隠れて、ほかの欄と重なって見えないように)。
    /// </summary>
    private void HookEntryScroll()
    {
        // テーマを変えると一覧の作りが作り直され、前のスクロールの部品は使われなくなるので、毎回探して付け直す
        // (前のものを持ち続けると、自動スクロールが効かなくなり、最新の発言が下に隠れたままになる)
        var current = FindScrollViewer(EntryList);
        if (current == null || ReferenceEquals(current, _entryScroll)) return;
        _entryScroll = current;
        current.ScrollChanged += (_, e) =>
        {
            if (!ReferenceEquals(current, _entryScroll)) return;
            if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            {
                _atBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 4; // 自分でスクロールした
                return;
            }
            if (_atBottom && AutoScrollCheck.IsChecked == true && SearchBox.Text.Trim().Length == 0
                && !(Keyboard.FocusedElement is TextBox box && EntryList.IsAncestorOf(box)))
                current.ScrollToEnd();
        };
    }

    private void AutoScrollCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.MinutesAutoScroll = AutoScrollCheck.IsChecked == true;
        _settings.Save();
        _host.MinutesDisplayChanged();
        ScrollToLatest();
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is System.Windows.Controls.ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    /// <summary>アプリの終了時: 待たずに保存して止める。</summary>
    public void ShutdownNow()
    {
        if (_closed) return;
        _closed = true;
        LeaveTextBox();
        _service.CancelSummary();
        StopPlayback();
        _player?.Close();
        if (_service.IsRecording || IsStopping)
        {
            try
            {
                if (_service.IsRecording) _service.StopCapture(); // 取り込みを止めて、記録した音声を閉じる (残りの文字起こしは待たない)
                OnRecordingSaved(_service.LastRecording); // 止めている途中でも、記録した音声は議事録に入れる
            }
            catch (Exception ex)
            {
                App.Log("MinutesShutdown", ex);
            }
        }
        StopVideoNow();
        AutoSave();
        _service.Dispose();
        DeleteBurnPart(); // 作りかけの字幕つきの動画は残さない
    }

    // 話者の名前: Enter ですぐ確定してすべての発言に反映、Esc で入力前に戻す
    private void SpeakerName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        var binding = box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            binding?.UpdateSource();
            binding?.UpdateTarget(); // 空欄なら既定の名前に戻ったことを表示する
            var named = box.DataContext as MinutesSpeaker;
            if (!MergeIfSameName(named))
            {
                SetStatus($"話者の名前を「{box.Text}」にしました");
                RememberVoice(named);
            }
            _nameAtFocus = box.Text;
            _autoSave.Stop();
            _autoSave.Start();
            LeaveTextBox(box); // 入力を終える (全選択の薄いグレーのまま欄に残さない)
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            binding?.UpdateTarget();
            LeaveTextBox(box);
            e.Handled = true;
        }
    }

    // 名前の欄 (TextBox) の上でもホイールで話者の一覧が動くようにする (TextBox がホイールを受け取ってしまうため)
    private void SpeakerScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || Keyboard.Modifiers == ModifierKeys.Control) return;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    private string? _nameAtFocus;

    private void SpeakerName_GotFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        _nameAtFocus = box.Text;
        box.SelectAll();
    }

    // Enter を押さずに別の所をクリックしたときも、名前が確定した後に同じ名前の話者をまとめる
    private void SpeakerName_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        // 確認の画面が出て窓が非アクティブになっただけ (NewFocus が無い)・まとめている途中・閉じた後は何もしない
        if (e.NewFocus == null || _merging || _closed) return;
        if (sender is TextBox box && box.DataContext is MinutesSpeaker speaker)
        {
            if (box.Text == _nameAtFocus) return; // 名前を変えていない
            var before = _nameAtFocus; // (次の欄に移ると書き換わるので、この欄の元の名前を取っておく)
            Dispatcher.BeginInvoke(() =>
            {
                if (_closed) return;
                if (!MergeIfSameName(speaker, before)) RememberVoice(speaker);
                _autoSave.Stop();
                _autoSave.Start();
            }, DispatcherPriority.Background);
        }
    }

    private bool _merging;

    /// <summary>ほかの話者と同じ名前なら 1 人にまとめる (同じ人が 2 人に分かれたときの直し方)。</summary>
    private bool MergeIfSameName(MinutesSpeaker? speaker, string? nameBefore = null)
    {
        if (_merging) return true;
        _merging = true;
        try
        {
            return MergeIfSameNameCore(speaker, nameBefore ?? _nameAtFocus);
        }
        finally
        {
            _merging = false;
        }
    }

    private bool MergeIfSameNameCore(MinutesSpeaker? speaker, string? nameBefore)
    {
        if (speaker == null || !_doc.Speakers.Contains(speaker) || _doc.SameNameAs(speaker) is not { } other) return false;
        if (speaker.Id == 0 || other.Id == 0)
        {
            var name = speaker.Name;
            speaker.Name = nameBefore is { Length: > 0 } before && before != name ? before : speaker.AutoName;
            SetStatus($"「{name}」は「自分」(マイク) と同じ名前なので付けられません。別の名前にしてください");
            return true; // 状態欄の案内を上書きしない
        }
        // まとめると元に戻せないので確かめる (別の人にうっかり同じ名前を付けたときに、発言の話者が混ざらないように)
        if (!App.DemoMode && MessageBox.Show(this,
                $"「{other.Name}」という名前の話者がもういます。2 人を 1 人にまとめますか？\n\n" +
                "同じ人が 2 人に分かれていたときは「はい」。まとめると元に戻せません。\n別の人なら「いいえ」を押して、別の名前を付けてください。",
                "GetText — 議事録", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            speaker.Name = nameBefore is { Length: > 0 } before && before != other.Name ? before : speaker.AutoName;
            SetStatus("まとめませんでした。別の名前を付けてください");
            return true;
        }
        // 先にいた (番号の小さい) 話者に残す
        var (kept, removed) = other.Id < speaker.Id ? _doc.MergeSpeakers(speaker, other) : _doc.MergeSpeakers(other, speaker);
        if (removed.Id < 1000 && kept.Id < 1000) _service.MergeSpeakers(removed.Id, kept.Id);
        RefreshTalkTimes();
        AutoSave();
        RememberVoice(kept);
        SetStatus($"同じ名前の話者を「{kept.Name}」の 1 人にまとめました");
        return true;
    }

    private void Threshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded) _service.SetSpeakerSensitivity(e.NewValue);
    }

    // ───────── 発言の削除・話者の変更 (右クリック) ─────────

    /// <summary>右クリックした発言。複数選んでいてその中の発言なら、選んだものすべて。</summary>
    /// <summary>右クリックした発言 (複数選んでいても、メニューを出した行の発言)。</summary>
    private static MinutesEntry? ClickedEntry(object sender)
    {
        var node = sender as DependencyObject;
        while (node is MenuItem item) node = item.Parent;
        return ((node as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as MinutesEntry;
    }

    private List<MinutesEntry> TargetsOf(object sender)
    {
        var node = sender as DependencyObject;
        while (node is MenuItem item) node = item.Parent;
        // メニューを開いたときの対象 (メニューを押すまでに選択が変わっても、開いたときの発言に効かせる)
        if (node is ContextMenu { Tag: List<MinutesEntry> opened }) return opened.Where(_doc.Entries.Contains).ToList();
        if (((node as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext is not MinutesEntry entry) return [];
        var selected = EntryList.SelectedItems.Cast<MinutesEntry>().ToList();
        return selected.Count > 1 && selected.Contains(entry) ? selected : [entry];
    }

    private void EntryMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        menu.Tag = null;
        var targets = TargetsOf(menu);
        menu.Tag = targets;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            switch (item.Tag)
            {
                case "undo":
                    item.IsEnabled = _deleted.Count > 0;
                    break;
                case "mark":
                    item.Header = targets.Count > 0 && targets.All(t => t.IsMarked) ? "★ を外す" : "重要な発言にする (★)";
                    break;
                case "play":
                    var clicked = ClickedEntry(menu);
                    item.IsEnabled = clicked != null && !clicked.IsNote && (_doc.FromFile || _doc.AudioAt(clicked) != null);
                    item.Header = _playing != null && _playing == clicked ? "再生を止める" : "この発言の音声を聞く";
                    break;
                case "speakers":
                    // 話者を選び直す一覧 (今の話者には印)
                    item.Items.Clear();
                    foreach (var speaker in _doc.Speakers)
                    {
                        var choice = new MenuItem
                        {
                            Header = speaker.Name,
                            IsCheckable = true,
                            IsChecked = targets.Count > 0 && targets.All(t => t.Speaker == speaker),
                            Icon = new System.Windows.Shapes.Ellipse
                            {
                                Width = 10, Height = 10,
                                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(speaker.Color)),
                            },
                        };
                        var target = speaker;
                        choice.Click += (_, _) => ChangeSpeaker(targets, target);
                        item.Items.Add(choice);
                    }
                    item.Items.Add(new Separator());
                    var added = new MenuItem { Header = "新しい話者" };
                    added.Click += (_, _) => ChangeSpeaker(targets, _doc.NewSpeaker());
                    item.Items.Add(added);
                    item.IsEnabled = targets.Count > 0 && !targets.Any(t => t.IsNote);
                    break;
            }
        }
        if (menu.Items[0] is MenuItem delete)
            delete.Header = targets.Count > 1 ? $"選んだ {targets.Count} 件の発言を削除" : "この発言を削除";
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e) => DeleteEntries(TargetsOf(sender));

    private void UndoDelete_Click(object sender, RoutedEventArgs e) => UndoDelete();

    private void CopyEntry_Click(object sender, RoutedEventArgs e)
    {
        var targets = TargetsOf(sender);
        if (targets.Count == 0) return;
        var options = ExportOptions();
        var text = string.Join(Environment.NewLine, targets.OrderBy(t => _doc.Entries.IndexOf(t)).Select(t =>
            $"{(options.IncludeTime ? $"[{t.TimeText}] " : "")}{t.Speaker.Name}: {t.Text}"
            + (options.IncludeTranslation && t.NeedsTranslation && !string.IsNullOrEmpty(t.Translation)
                ? $"{Environment.NewLine}    (訳) {t.Translation}" : "")));
        try
        {
            Clipboard.SetText(text);
            SetStatus(targets.Count > 1 ? $"{targets.Count} 件の発言をコピーしました" : "発言をコピーしました");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            SetStatus("クリップボードにアクセスできませんでした");
        }
    }

    private void DeleteEntries(List<MinutesEntry> targets)
    {
        if (targets.Count == 0) return;
        _deleted.Push(_doc.Remove(targets));
        AutoSave();
        SetStatus($"{targets.Count} 件の発言を削除しました (Ctrl+Z または右クリックで元に戻せます)");
    }

    private void UndoDelete()
    {
        if (_deleted.Count == 0) return;
        var removed = _deleted.Pop();
        _doc.Restore(removed);
        AutoSave();
        SetStatus($"{removed.Count} 件の発言を元に戻しました");
    }

    private void ChangeSpeaker(List<MinutesEntry> targets, MinutesSpeaker speaker)
    {
        if (targets.Count == 0) return;
        _doc.SetSpeaker(targets, speaker);
        RefreshTalkTimes();
        AutoSave();
        SetStatus($"{targets.Count} 件の発言を「{speaker.Name}」にしました (この発言は声による自動の付け直しをしません)");
    }

    // 本文を書いている途中でなければ、Delete で選んだ発言を削除、Ctrl+Z で削除を元に戻す
    private void EntryList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        if (e.Key is Key.Enter or Key.Escape && EntryList.SelectedItems.Count > 0)
        {
            EntryList.UnselectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete)
        {
            DeleteEntries(EntryList.SelectedItems.Cast<MinutesEntry>().ToList());
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            UndoDelete();
            e.Handled = true;
        }
    }

    // ───────── 文字の大きさ・位置 ─────────

    /// <summary>設定の文字の大きさを発言の一覧に反映する (設定画面で変えたときも呼ばれる)。</summary>
    public void ApplyFontSize()
    {
        double size = Math.Clamp(_settings.MinutesFontSize, 9, 40);
        Resources["EntryFontSize"] = size;
        Resources["EntrySmallFontSize"] = Math.Max(9, size - 1);
        Resources["EntryTimeWidth"] = Math.Round(size * 4.9); // 「14:00:05」が収まる幅
    }

    // 一覧の上で Ctrl+ホイール、または Ctrl + (+ / - / 0) で文字の大きさを変える
    private void EntryList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        ChangeFontSize(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void ChangeFontSize(double delta, bool reset = false)
    {
        _settings.MinutesFontSize = reset ? 14 : Math.Clamp(_settings.MinutesFontSize + delta, 9, 40);
        _settings.Save();
        ApplyFontSize();
        _host.MinutesDisplayChanged();
        SetStatus($"発言の文字の大きさ: {_settings.MinutesFontSize:0} pt (Ctrl+0 で元に戻す)");
    }

    private void RestorePlacement()
    {
        Width = Math.Max(MinWidth, _settings.MinutesWidth);
        Height = Math.Max(MinHeight, _settings.MinutesHeight);
        if (_settings.MinutesLeft is double l && _settings.MinutesTop is double t)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
            ScreenUtil.EnsureVisible(this); // モニターを外したときなどは画面の中に戻す
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        // 窓ができたら物理ピクセルで前回の場所に置き直す (拡大率の違うモニターでも同じ場所に戻す)
        SourceInitialized += (_, _) =>
        {
            ScreenUtil.RestorePixelPosition(this, _settings.MinutesPixel);
            ScreenUtil.FitToWorkArea(this);
        };
    }

    // 要約の欄は窓の高さに合わせる (低い窓で発言の一覧が見えなくならないように)
    private void FitSummaryHeight()
    {
        double max = Math.Clamp((ActualHeight - 360) * 0.6, 90, 300);
        SummaryViewScroll.MaxHeight = SummaryBox.MaxHeight = max;
    }

    /// <summary>位置と大きさを設定に書く (保存はしない)。最大化・最小化中は書かない。</summary>
    internal void StorePlacement()
    {
        if (WindowState != WindowState.Normal) return;
        _settings.MinutesLeft = Left;
        _settings.MinutesTop = Top;
        _settings.MinutesWidth = Width;
        _settings.MinutesHeight = Height;
        _settings.MinutesPixel = ScreenUtil.PixelPosition(this) ?? _settings.MinutesPixel;
    }

    private void SavePlacement()
    {
        if (App.DemoMode || WindowState != WindowState.Normal) return;
        StorePlacement();
        _settings.Save();
    }

    // ───────── 検索 ─────────

    private bool MatchesSearch(MinutesEntry entry)
    {
        var q = SearchBox?.Text.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return entry.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
               || (entry.Translation?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
               || entry.Speaker.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshTalkTimes()
    {
        _talkTimes.Stop();
        _talkTimes.Start();
    }

    // Ctrl+F で検索、Ctrl+S で保存、Ctrl+O で開く、Ctrl + (+ / - / 0) で文字の大きさ
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _playing != null && Keyboard.FocusedElement is not TextBox)
        {
            StopPlayback();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F3 && SearchBox.Text.Trim().Length > 0)
        {
            FindNext(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.R:
                if (RecordButton.IsEnabled && RecordButton.IsVisible) Record_Click(RecordButton, e);
                e.Handled = true;
                break;
            case Key.M:
                MemoBox.Focus();
                e.Handled = true;
                break;
            case Key.B:
                // 選んだ発言 (書き直している発言) に ★ を付ける / 外す
                var targets = EntryList.SelectedItems.Cast<MinutesEntry>().ToList();
                if (targets.Count == 0 && (Keyboard.FocusedElement as FrameworkElement)?.DataContext is MinutesEntry focused) targets = [focused];
                ToggleMark(targets);
                e.Handled = true;
                break;
            case Key.S:
                Save_Click(sender, e);
                e.Handled = true;
                break;
            case Key.O:
                if (!_busy && !_service.IsRecording && !_service.IsTranscribingFile)
                {
                    LeaveTextBox();
                    ChooseAndOpen();
                }
                e.Handled = true;
                break;
            case Key.F:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add:
                ChangeFontSize(1);
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                ChangeFontSize(-1);
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0:
                ChangeFontSize(0, reset: true);
                e.Handled = true;
                break;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchIndex = -1;
        CollectionViewSource.GetDefaultView(_doc.Entries).Refresh();
        bool searching = SearchBox.Text.Trim().Length > 0;
        SearchCount.Text = searching ? $"{EntryList.Items.Count} 件" : "";
        // 検索中は、見つかった発言を読めるよう自動スクロールで一番下に飛ばさない
        if (!searching) ScrollToLatest();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            EntryList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            FindNext(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
        }
    }

    private int _searchIndex = -1;

    /// <summary>検索で見つけた発言を順に (step = 1 で次、-1 で前) 表示し、一致した文字を選択の色で示す。</summary>
    private void FindNext(int step)
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0 || EntryList.Items.Count == 0) return;
        int n = EntryList.Items.Count;
        _searchIndex = _searchIndex < 0 ? (step > 0 ? 0 : n - 1) : ((_searchIndex + step) % n + n) % n;
        var entry = (MinutesEntry)EntryList.Items[_searchIndex];
        EntryList.ScrollIntoView(entry);
        EntryList.UpdateLayout();
        SearchCount.Text = $"{_searchIndex + 1}/{EntryList.Items.Count} 件";
        if (EntryList.ItemContainerGenerator.ContainerFromItem(entry) is not DependencyObject container) return;
        var box = FindDescendant<TextBox>(container);
        int at = box?.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1;
        if (box != null && at >= 0) box.Select(at, q.Length);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    // ───────── 狭い窓では話者の欄をたたむ ─────────

    private const double NarrowWidth = 760;

    private const double SpeakerMinWidth = 170;

    private void FitSpeakerPanel()
    {
        bool narrow = ActualWidth > 0 && ActualWidth < NarrowWidth;
        SpeakersToggle.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        bool show = !narrow || SpeakersToggle.IsChecked == true;
        // 話者の欄は、ドラッグで変えた幅 (発言の欄が狭くなりすぎないよう、窓の幅の半分まで)
        double max = Math.Max(SpeakerMinWidth, ActualWidth > 0 ? ActualWidth * 0.5 : 600);
        SpeakerColumn.MinWidth = show ? SpeakerMinWidth : 0;
        SpeakerColumn.MaxWidth = show ? max : double.PositiveInfinity;
        SpeakerColumn.Width = new GridLength(show ? Math.Clamp(_settings.MinutesSpeakerWidth, SpeakerMinWidth, max) : 0);
        SpeakerGapColumn.Width = new GridLength(show ? 8 : 0);
        SpeakerSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SpeakerCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SpeakersToggle_Click(object sender, RoutedEventArgs e) => FitSpeakerPanel();

    private void SpeakerSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (SpeakerColumn.ActualWidth < SpeakerMinWidth - 1) return;
        _settings.MinutesSpeakerWidth = Math.Round(SpeakerColumn.ActualWidth);
        _settings.Save();
        FitSpeakerPanel(); // 発言の欄の幅を「*」に戻す (ドラッグ中は両方の欄が固定の幅になる)
    }

    private void SpeakerSplitter_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        _settings.MinutesSpeakerWidth = AppSettings.DefaultSpeakerWidth;
        _settings.Save();
        FitSpeakerPanel();
        e.Handled = true;
    }

    // ───────── 表示 (時刻・相づち) ─────────

    /// <summary>設定を議事録のページで開く。</summary>
    private void Settings_Click(object sender, RoutedEventArgs e) => _host.OpenSettings(SettingsWindow.MinutesTab, this);

    /// <summary>設定の画面で変えた議事録の表示を、この画面にも反映する。</summary>
    public void ApplyDisplaySetting(string name)
    {
        switch (name)
        {
            case nameof(AppSettings.MinutesShowTime):
                TimeCheck.IsChecked = _settings.MinutesShowTime;
                TimeCheck_Click(TimeCheck, new RoutedEventArgs());
                break;
            case nameof(AppSettings.MinutesHideFillers):
                FillerCheck.IsChecked = _settings.MinutesHideFillers;
                FillerCheck_Click(FillerCheck, new RoutedEventArgs());
                break;
            case nameof(AppSettings.MinutesTranslate):
                TranslateCheck.IsChecked = _settings.MinutesTranslate;
                TranslateCheck_Click(TranslateCheck, new RoutedEventArgs());
                break;
            case nameof(AppSettings.MinutesAutoScroll):
                AutoScrollCheck.IsChecked = _settings.MinutesAutoScroll;
                AutoScrollCheck_Click(AutoScrollCheck, new RoutedEventArgs());
                break;
        }
    }

    private void TimeCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.MinutesShowTime = TimeCheck.IsChecked == true;
        _settings.Save();
        _host.MinutesDisplayChanged();
        EntryList.Tag = _settings.MinutesShowTime;
        AutoSave();
    }

    private void FillerCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.MinutesHideFillers = FillerCheck.IsChecked == true;
        _settings.Save();
        _host.MinutesDisplayChanged();
        CollectionViewSource.GetDefaultView(_doc.Entries).Refresh();
        UpdateFillerCount();
        AutoSave();
    }

    private void UpdateFillerCount()
    {
        int fillers = _doc.Entries.Count(en => en.IsFiller);
        FillerCheck.Content = fillers > 0 ? $"相づちを除く ({fillers})" : "相づちを除く";
    }

    // ───────── 見本の画面 (操作手順の動画用) ─────────

    /// <summary>
    /// 紹介動画用: ファイルから作った議事録を、until 秒の時点まで (話し終わってから約 1.5 秒で文字になる) 表示する。
    /// finished なら全体と要約を表示する。
    /// </summary>
    internal void LoadDemoFile(MinutesFile file, double until, double total, bool finished)
    {
        WindowBox.ItemsSource = new List<WindowInfo> { WindowInfo.AllAudio };
        WindowBox.SelectedIndex = 0;
        HideSummary();
        var shown = file with
        {
            Entries = file.Entries.Where(e => finished || e.OffsetSeconds + e.DurationSeconds + 1.5 <= until).ToList(),
            Summary = finished ? file.Summary : null,
        };
        _doc.Load(shown);
        EmptyHint.Visibility = Visibility.Collapsed; // 文字起こし中なので、最初の案内は出さない
        UpdateFillerCount();
        UpdateMarkedCount();
        _doc.UpdateTalkTimes();
        if (finished)
        {
            RecordButton.Visibility = Visibility.Visible;
            FileIcon.Text = "";
            FileLabel.Text = "ファイルから…";
            FileProgress.Visibility = Visibility.Collapsed;
            ElapsedText.Text = TimeSpan.FromSeconds(total).ToString(@"h\:mm\:ss");
            if (_doc.Summary is { } summary) ShowSummary(summary, "PC 内の AI が作成 ・ 内容は確かめてから使ってください");
            SetStatus($"完了しました ・ {_doc.Entries.Count} 発言 ・ 話者 {_doc.Speakers.Count} 人");
        }
        else
        {
            RecordButton.Visibility = Visibility.Collapsed;
            WindowBox.IsEnabled = MicCheck.IsEnabled = false;
            FileIcon.Text = "";
            FileLabel.Text = "中止";
            FileProgress.Visibility = Visibility.Visible;
            FileProgress.Value = Math.Min(0.995, until / Math.Max(1, total)); // ちょうど 100% だと描画が落ち着かず、画像にできない
            ElapsedText.Text = $"{TimeSpan.FromSeconds(Math.Min(until, total)):h\\:mm\\:ss} / {TimeSpan.FromSeconds(total):h\\:mm\\:ss}";
            SetStatus($"ファイルを文字起こししています {Math.Min(1, until / Math.Max(1, total)):P0}");
        }
        if (_doc.Entries.Count > 0)
        {
            EntryList.UpdateLayout();
            EntryList.ScrollIntoView(_doc.Entries[^1]);
        }
    }

    /// <summary>見本の議事録を表示する。scene: "empty" / "recording" / "file" / "many" / "summary"。発言や名前は架空のもの。</summary>
    internal void LoadDemo(string scene)
    {
        WindowBox.ItemsSource = new List<WindowInfo>
        {
            WindowInfo.AllAudio,
            new(IntPtr.Zero, "定例ミーティング", 1, "Zoom", IsPlaying: true),
            new(IntPtr.Zero, "チーム会議", 2, "ms-teams"),
            new(IntPtr.Zero, "講演の動画 - ブラウザ", 3, "msedge"),
        };
        WindowBox.SelectedIndex = 1;
        CorrectCheck.Content = "文脈で聞き間違いを補正 (1 件)";
        SetState(scene switch { "loading" => "loading", "recording" => "recording", "file" => "file", _ => "ready" });
        if (scene == "loading")
        {
            _loadSteps.Clear();
            _loadSteps.Add(new LoadStep("python", "音声認識の準備 (Python の起動)"));
            _loadSteps.Add(new LoadStep("asr", "日本語の音声認識モデル (kotoba-whisper)"));
            _loadSteps.Add(new LoadStep("speakers", "話者の聞き分けのモデル"));
            _loadSteps.Add(new LoadStep("multi", "英語などの音声認識モデル (Whisper large-v3 turbo)"));
            _loadSteps[0].SetDemo("done", 2.1);
            _loadSteps[1].SetDemo("done", 6.4);
            _loadSteps[2].SetDemo("active", 1);
            LoadingSteps.ItemsSource = _loadSteps;
            LoadingElapsed.Text = "経過 0:09 ・ 前回は約 0:21";
            LoadingOverlay.Visibility = Visibility.Visible;
            SetStatus("音声認識モデルを読み込んでいます…");
            return;
        }
        if (scene == "empty")
        {
            SetStatus("準備完了 (GPU・正確さ優先・12 秒) ・ 音を選んで「記録を開始」を押してください");
            return;
        }
        var start = new DateTime(2026, 10, 1, 14, 0, 0);
        _doc.FromFile = scene == "file";
        _doc.Source = scene == "file" ? "定例会議_録画.mp4" : "定例ミーティング";
        var lines = new (double Start, double End, int Speaker, string Text, string Lang)[]
        {
            (5, 9, 1, "では定例会議を始めます。まず開発の進捗を確認させてください。", "ja"),
            (10, 11, 0, "はい。", "ja"),
            (11.5, 17, 0, "画面の文字を読み取る機能は完成して、今はテストをしています。", "ja"),
            (18, 22, 2, "Great. Could you share the test results by Friday?", "en"),
            (23, 26, 0, "承知しました。金曜日までに共有します。", "ja"),
            (27, 32, 1, "次回の会議で、読み取りの制度の検証結果を確認しましょう。", "ja"),
        };
        int id = 1;
        foreach (var (s, e, sp, text, lang) in lines)
            _doc.Add(start, new TranscriptSegment(sp == 0 ? "mic" : "win", s, e, sp, text, id++, lang));
        if (scene == "many") // 話者が多いとき (話者の欄のスクロールの確認用)
            for (int sp = 3; sp <= 9; sp++)
                _doc.Add(start, new TranscriptSegment("win", 30 + sp * 3, 32 + sp * 3, sp, $"見本の発言 {sp}", id++));
        _doc.Speaker(1).Name = "田中";
        _doc.Speaker(2).Name = "Sarah";
        _doc.Entries.First(en => en.Language == "en").Translation = "いいですね。金曜日までにテスト結果を共有してもらえますか？";
        _doc.Entries.Last().ApplyRevision("次回の会議で、読み取りの精度の検証結果を確認しましょう。", markCorrected: true);
        _doc.Entries.First(en => en.Text.StartsWith("承知しました")).IsMarked = true; // ★ の見本
        _doc.AddNote(start.AddSeconds(26), "テスト結果の共有先は開発チームのフォルダ"); // メモの見本
        UpdateFillerCount();
        _doc.UpdateTalkTimes();
        WindowLevel.Value = 0.55;
        MicLevel.Value = 0.2;
        if (scene == "recording")
        {
            RecordIcon.Text = "\uE71A"; // 停止
            RecordLabel.Text = "記録を停止";
            ElapsedText.Text = "00:00:41";
            WindowBox.IsEnabled = MicCheck.IsEnabled = FileButton.IsEnabled = false;
            ShowPartialArea(true, window: true, mic: true);
            OnPartial("win", 2, "I would also like to talk about the budget for");
            SetStatus("記録中 ・ Zoom + マイク ・ 話し終わってから数秒で文字になります");
        }
        else if (scene == "file")
        {
            RecordButton.Visibility = Visibility.Collapsed;
            WindowBox.IsEnabled = MicCheck.IsEnabled = false;
            FileIcon.Text = "\uE711";
            FileLabel.Text = "中止";
            FileProgress.Visibility = Visibility.Visible;
            FileProgress.Value = 0.42;
            ElapsedText.Text = "0:12:36 / 0:30:00";
            SetStatus("ファイルを文字起こししています 42% ・ 残り約 4 分");
        }
        else if (scene == "summary")
        {
            ShowSummary("### 概要\n定例会議で開発の進捗を確認した。画面の文字を読み取る機能は完成し、テスト中である。\n\n" +
                        "### 決定事項\n- 次回の会議で読み取りの精度の検証結果を確認する\n\n" +
                        "### やること\n- テスト結果を共有する (担当: 自分、期限: 金曜日)\n\n" +
                        "### 主な論点\n- 読み取りの精度の検証方法", "PC 内の AI が 0:42 で作成 ・ 内容は確かめてから使ってください");
            SetStatus("要約を作りました");
        }
    }

    // ───────── 日本語訳 ─────────

    private void OnEntryAdded(MinutesEntry entry)
    {
        if (_hooked.Add(entry))
        {
            entry.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MinutesEntry.Text) && entry.UserEdited)
                {
                    _autoSave.Stop();
                    _autoSave.Start();
                }
                if (e.PropertyName == nameof(MinutesEntry.IsMarked))
                {
                    UpdateMarkedCount();
                    _autoSave.Stop();
                    _autoSave.Start();
                    return;
                }
                // 本文が変わったら (自動の補正・書き直し) 訳し直す
                if (e.PropertyName != nameof(MinutesEntry.Text) || !entry.NeedsTranslation) return;
                _editedForTranslation.Add(entry);
                _translateDelay.Stop();
                _translateDelay.Start();
            };
        }
        QueueTranslation(entry);
    }

    private void QueueTranslation(MinutesEntry entry)
    {
        if (App.DemoMode) return;
        if (TranslateCheck.IsChecked != true || !entry.NeedsTranslation || entry.Translation != null) return;
        if (!Translator.OfflineAvailable)
        {
            if (!_translateErrorShown) SetStatus("日本語訳には PC 内の翻訳モデルが必要です (設定 → セットアップ・情報 → セットアップを実行)");
            _translateErrorShown = true;
            return;
        }
        if (!_translateQueue.Contains(entry)) _translateQueue.Add(entry);
        PumpTranslations();
    }

    /// <summary>順番待ちの発言を数件ずつまとめて訳す (UI スレッドで待つだけなので画面は止まらない)。</summary>
    private async void PumpTranslations()
    {
        if (_translating) return;
        _translating = true;
        try
        {
            while (_translateQueue.Count > 0 && TranslateCheck.IsChecked == true)
            {
                var batch = _translateQueue.Take(8).ToList();
                _translateQueue.RemoveRange(0, batch.Count);
                var texts = batch.Select(e => e.Text).ToArray();
                var results = await TranslateBatchAsync(batch, texts);
                for (int i = 0; i < batch.Count; i++)
                {
                    // 訳している間に本文が変わっていたら、その発言は訳し直しを待つ
                    if (batch[i].Text == texts[i] && _doc.Entries.Contains(batch[i])) batch[i].Translation = results[i];
                }
                ScrollToLatest(); // 訳の行が増えて最新の発言が下に隠れないように
                _autoSave.Stop();
                _autoSave.Start();
            }
        }
        catch (Exception ex)
        {
            App.Log("MinutesTranslate", ex);
            _translateQueue.Clear();
            SetStatus("日本語訳できませんでした: " + ex.Message);
        }
        finally
        {
            _translating = false;
        }
    }

    /// <summary>
    /// 設定で AI の訳がオンで、文脈補正の AI が使えるときは AI で (前後の発言と用語を使う)、
    /// それ以外は PC 内の翻訳モデルで訳す。AI で訳せなかったときも翻訳モデルで訳す。
    /// </summary>
    private async Task<string[]> TranslateBatchAsync(List<MinutesEntry> batch, string[] texts)
    {
        if (_settings.MinutesAiTranslate && !App.DemoMode && _service.IsReady && _llmState is not ("off" or "unavailable"))
        {
            try
            {
                var items = batch.Select((e, i) => (texts[i], (IReadOnlyList<string>)ContextBefore(e))).ToList();
                return await _service.TranslateWithAiAsync(items);
            }
            catch (Exception ex)
            {
                App.Log("MinutesAiTranslate", ex);
            }
        }
        return await _host.Translator.TranslateOfflineAsync(texts, CancellationToken.None);
    }

    /// <summary>発言の直前の 4 発言 (「話者: 本文」)。AI の訳の文脈に使う。</summary>
    private List<string> ContextBefore(MinutesEntry entry)
    {
        int index = _doc.Entries.IndexOf(entry);
        return _doc.Entries.Take(Math.Max(0, index)).Where(e => !e.IsNote).TakeLast(4)
            .Select(e => $"{e.Speaker.Name}: {e.Text}").ToList();
    }

    private void TranslateCheck_Click(object sender, RoutedEventArgs e)
    {
        bool on = TranslateCheck.IsChecked == true;
        _settings.MinutesTranslate = on;
        _settings.Save();
        _host.MinutesDisplayChanged();
        _doc.ShowTranslations = on;
        foreach (var entry in _doc.Entries)
        {
            entry.ShowTranslation = on;
            if (on) QueueTranslation(entry);
        }
        AutoSave();
    }

    private int VisibleCount() => FillerCheck.IsChecked == true ? _doc.Entries.Count(en => !en.IsFiller) : _doc.Entries.Count;

    private MinutesDocument.ExportOptions ExportOptions() =>
        new(IncludeTime: TimeCheck.IsChecked == true, ExcludeFillers: FillerCheck.IsChecked == true,
            IncludeTranslation: TranslateCheck.IsChecked == true);

    // ───────── 保存 ─────────

    /// <summary>落ちても内容が残るよう、発言が増えるたびに Markdown で自動保存する。</summary>
    private void AutoSave()
    {
        if (App.DemoMode) return;
        if (_doc.Entries.Count == 0) return;
        try
        {
            Directory.CreateDirectory(AutoSaveDir);
            _autoSavePath ??= Path.Combine(AutoSaveDir, _doc.FromFile
                ? $"議事録_{SafeName(Path.GetFileNameWithoutExtension(_doc.Source))}_{_doc.StartedAt ?? DateTime.Now:yyyyMMdd_HHmmss}.md"
                : $"議事録_{_doc.StartedAt ?? DateTime.Now:yyyyMMdd_HHmmss}.md");
            AtomicFile.WriteAllText(_autoSavePath, _doc.ToMarkdown(ExportOptions()));
            // 読み込み直せるよう、議事録データも一緒に保存する (別の PC で開いて話者を直すときなど)
            AtomicFile.WriteAllText(Path.ChangeExtension(_autoSavePath, ".json"), _doc.ToFile().ToJson());
            if (_autoSaveFailed)
            {
                _autoSaveFailed = false;
                HideNotice("autosave");
            }
        }
        catch (Exception ex)
        {
            // 初めて失敗したとき・ほかの知らせで消えた後も失敗が続いているとき (1 分ごと) に知らせる
            if (!_autoSaveFailed || (_noticeKind != "autosave" && DateTime.Now - _autoSaveNoticeAt > TimeSpan.FromMinutes(1)))
            {
                ShowNotice("自動保存できませんでした (" + ex.Message + ")。ディスクの空きを確かめてください。「保存…」で別の場所にも保存できます。", "error", kind: "autosave");
                _autoSaveNoticeAt = DateTime.Now;
            }
            _autoSaveFailed = true;
        }
    }

    private bool _autoSaveFailed;
    private DateTime _autoSaveNoticeAt;

    private static string SafeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 40 ? name[..40] : name;
    }

    // 保存の種類 (設定に残す番号) を、保存の画面の一覧に並べる順。7 = プロジェクト (後から足したので番号は最後)
    private static readonly int[] SaveKindOrder = [1, 7, 3, 2, 4, 5, 6, 8];

    /// <summary>プロジェクト (.gettext) を開いた・保存したとき、その場所 (次に保存するときの初めの名前に使う)。</summary>
    private string? _openedProject;
    private bool _savingProject;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        LeaveTextBox(); // 書きかけの話者の名前も確定してから保存する
        if (_doc.Entries.Count == 0)
        {
            SetStatus("保存する発言がありません");
            return;
        }
        if (_savingProject)
        {
            SetStatus("プロジェクトを保存しています。終わるまでお待ちください");
            return;
        }
        bool reuseProject = _openedProject != null && _settings.MinutesSaveFilter == 7;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = reuseProject ? Path.GetFileNameWithoutExtension(_openedProject!)
                : _doc.FromFile
                ? $"議事録_{SafeName(Path.GetFileNameWithoutExtension(_doc.Source))}"
                : $"議事録_{_doc.StartedAt ?? DateTime.Now:yyyyMMdd_HHmm}",
            InitialDirectory = reuseProject ? Path.GetDirectoryName(_openedProject!) ?? "" : "",
            Filter = "Word 文書 (*.docx)|*.docx|" +
                     "GetText の議事録プロジェクト (*.gettext) — 音声も入れて 1 つのファイルに。あとで開いて聞き直し・直しができる|*.gettext|" +
                     "Markdown (*.md)|*.md|テキスト (*.txt)|*.txt|GetText の議事録データ (*.json) — 音声なし。あとで開いて直せる|*.json|" +
                     "字幕 SRT (*.srt) — 動画プレーヤーで表示できる|*.srt|字幕 WebVTT (*.vtt)|*.vtt|" +
                     "字幕つきの動画 (*.mp4) — 記録しながら録画した会議の動画に、今の発言を字幕にして焼き込む|*.mp4",
            FilterIndex = Math.Max(1, Array.IndexOf(SaveKindOrder, _settings.MinutesSaveFilter) + 1),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            int kind = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
            {
                ".docx" => 1, ".txt" => 2, ".md" => 3, ".json" => 4, ".srt" => 5, ".vtt" => 6, MinutesProject.Extension => 7, ".mp4" => 8,
                _ => SaveKindOrder[Math.Clamp(dialog.FilterIndex, 1, SaveKindOrder.Length) - 1],
            };
            if (kind == 7)
            {
                SaveProject(dialog.FileName);
                return;
            }
            if (kind == 8)
            {
                if (_doc.VideoParts.Count == 0)
                {
                    ShowNotice("この議事録には録画がありません。「⚙ 詳細」→「会議の画面も録画する」をオンにして記録すると、字幕つきの動画を作れます。", "warning");
                    return;
                }
                if (_doc.VideoParts.Any(p => SamePath(p.Path, dialog.FileName)))
                {
                    ShowNotice("元の録画には上書きできません。別の名前を付けて保存してください。", "warning");
                    return;
                }
                _ = BurnSubtitlesAsync([.. _doc.VideoParts], dialog.FileName);
                return;
            }
            if (kind == 1)
                DocxWriter.Save(dialog.FileName, _doc.ToMarkdown(ExportOptions()));
            else
                AtomicFile.WriteAllText(dialog.FileName, kind switch
                {
                    2 => _doc.ToPlainText(ExportOptions()),
                    4 => _doc.ToFile().ToJson(),
                    5 => _doc.ToSubtitles(vtt: false, ExportOptions()),
                    6 => _doc.ToSubtitles(vtt: true, ExportOptions()),
                    _ => _doc.ToMarkdown(ExportOptions()),
                });
            _settings.MinutesSaveFilter = kind;
            _settings.Save();
            SetStatus("保存しました: " + dialog.FileName);
            var saved = dialog.FileName;
            ShowNotice($"保存しました: {Path.GetFileName(saved)}", "info", "フォルダを開く", () =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{saved}\""); } catch { }
            });
        }
        catch (Exception ex)
        {
            ShowNotice("保存できませんでした: " + ex.Message, "error");
        }
    }

    /// <summary>
    /// 議事録を、音声も入れたプロジェクト (.gettext) として保存する。記録した音声の変換が終わるのを待ち、
    /// ファイルから作った議事録で元が動画なら、音声だけを取り出して入れる (動画のままだと大きいため)。
    /// </summary>
    private async void SaveProject(string path)
    {
        _savingProject = true;
        string? extracted = null;
        int generation = _docGeneration;
        try
        {
            SetStatus("プロジェクトを保存しています…");
            // 記録を止めた直後の音声の変換 (WAV → m4a) を待つ (長くても 2 分)
            var pending = _encodes.Where(t => !t.IsCompleted).ToArray();
            if (pending.Length > 0)
            {
                SetStatus("記録した音声の変換が終わるのを待っています…");
                await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromMinutes(2)));
            }
            string? source = null;
            if (_doc.FromFile && _doc.SourcePath is { } src && File.Exists(src) && IsVideo(src) && !App.DemoMode)
            {
                SetStatus("動画から音声を取り出しています (長い動画は数分かかります)…");
                extracted = Path.Combine(Path.GetTempPath(), $"gettext_project_{Guid.NewGuid():N}.m4a");
                try
                {
                    await _service.EncodeAudioAsync(src, extracted);
                    source = extracted;
                }
                catch (Exception ex)
                {
                    App.Log("ProjectExtractAudio", ex); // 取り出せなければ動画のまま入れる
                }
            }
            if (generation != _docGeneration)
            {
                // 待っている間に新規・開くで議事録が替わった (前のプロジェクトを空の議事録で上書きしない)
                SetStatus("保存を待っている間に議事録が替わったので、プロジェクトは保存しませんでした");
                return;
            }
            var file = _doc.ToFile();
            var markdown = _doc.ToMarkdown(ExportOptions());
            SetStatus("プロジェクトを保存しています…");
            var result = await Task.Run(() => MinutesProject.Save(path, file, markdown, source));
            _settings.MinutesSaveFilter = 7;
            _settings.Save();
            _openedProject = path;
            var size = result.Bytes >= 1 << 20 ? $"{result.Bytes / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, result.Bytes / 1024)} KB";
            var audio = result.AudioFiles > 0 ? $"音声 {result.AudioFiles} 個入り" : "音声なし";
            var missing = result.MissingAudio > 0 ? $"。見つからない音声 {result.MissingAudio} 個は入れられませんでした" : "";
            SetStatus($"プロジェクトを保存しました: {path} ({audio}・{size})");
            ShowNotice($"プロジェクトを保存しました: {Path.GetFileName(path)} ({audio}・{size}){missing}", result.MissingAudio > 0 ? "warning" : "info",
                "フォルダを開く", () =>
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
                });
        }
        catch (Exception ex)
        {
            App.Log("ProjectSave", ex);
            ShowNotice("プロジェクトを保存できませんでした: " + ex.Message, "error");
        }
        finally
        {
            _savingProject = false;
            if (extracted != null) TryDelete(extracted);
        }
    }

    private static bool IsVideo(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi" or ".wmv" or ".m4v" or ".flv";

    /// <summary>開いたプロジェクトの音声を取り出しておく場所。</summary>
    private static string ProjectDir => App.DemoMode ? Path.Combine(Path.GetTempPath(), "GetText_selftest_projects") : Path.Combine(AutoSaveDir, "プロジェクト");

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        LeaveTextBox();
        if (_doc.Entries.Count == 0) return;
        try
        {
            Clipboard.SetText(_doc.ToPlainText(ExportOptions()));
            SetStatus("議事録をコピーしました");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            SetStatus("クリップボードにアクセスできませんでした");
        }
    }

    /// <summary>保存した議事録 (.json / .md / .txt。別の PC で作ったものも) を開く。</summary>
    /// <summary>最近の議事録 (自動保存したもの) と「ファイルを選ぶ…」のメニューを出す。</summary>
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            SetStatus("記録の準備中・停止中です。終わってから開いてください");
            return;
        }
        LeaveTextBox();
        if (_service.IsRecording || _service.IsTranscribingFile)
        {
            SetStatus("記録中・文字起こし中は開けません。停止してから開いてください");
            return;
        }
        var menu = new ContextMenu { PlacementTarget = sender as UIElement, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var recent = RecentMinutes.List(AutoSaveDir, 8);
        if (recent.Count > 0)
        {
            menu.Items.Add(new MenuItem { Header = "最近の議事録", IsEnabled = false });
            foreach (var (path, label) in recent)
            {
                var item = new MenuItem { Header = label, ToolTip = path };
                item.Click += (_, _) => OpenPath(path);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
        }
        var choose = new MenuItem { Header = "ファイルを選ぶ… (Ctrl+O)" };
        choose.Click += (_, _) => ChooseAndOpen();
        menu.Items.Add(choose);
        var folder = new MenuItem { Header = "自動保存のフォルダを開く" };
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(AutoSaveDir);
            try { System.Diagnostics.Process.Start("explorer.exe", $"\"{AutoSaveDir}\""); } catch { }
        };
        menu.Items.Add(folder);
        menu.IsOpen = true;
    }

    private void ChooseAndOpen()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "開く議事録",
            Filter = "GetText の議事録 (*.gettext;*.json;*.md;*.txt)|*.gettext;*.json;*.md;*.txt|議事録プロジェクト (*.gettext)|*.gettext|すべてのファイル|*.*",
            InitialDirectory = Directory.Exists(AutoSaveDir) ? AutoSaveDir : null,
        };
        if (dialog.ShowDialog(this) == true) OpenPath(dialog.FileName);
    }

    /// <summary>保存した議事録 (.gettext / .json / .md / .txt) を開く。</summary>
    private async void OpenPath(string fileName)
    {
        if (_busy || _service.IsRecording || _service.IsTranscribingFile || _savingProject) return;
        if (_doc.Entries.Count > 0 &&
            MessageBox.Show(this, "今の議事録を閉じて、選んだ議事録を開きますか？\n(今の内容は自動保存されています)", "GetText — 議事録",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        AutoSave();
        try
        {
            bool project = MinutesProject.IsProject(fileName);
            MinutesFile? data = null;
            if (project)
            {
                StopPlayback();
                _player?.Close(); // 前に取り出した音声を開いたままだと、取り出し直せない
                _playerPath = null;
                SetStatus("プロジェクトを開いています…");
                _savingProject = true; // 開き終わるまで保存・開くを重ねない
                try
                {
                    data = await Task.Run(() => MinutesProject.Open(fileName, ProjectDir));
                }
                finally
                {
                    _savingProject = false;
                }
                if (_busy || _service.IsRecording || _service.IsTranscribingFile) return; // 開いている間に記録が始まった
            }
            data ??= MinutesFile.Read(fileName); // 読めないファイルなら、今の議事録を消さずに知らせる
            ResetDocument();
            _doc.Load(data);
            _openedProject = project ? fileName : null;
            if (_doc.Summary is { } summary) ShowSummary(summary, "保存されていた要約です");
            foreach (var entry in _doc.Entries) QueueTranslation(entry); // 訳が無い日本語以外の発言は訳す
            var md = project || fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "" :
                " (Markdown / テキストから読んだので、同じ話者が続く発言は 1 つにまとまっています)";
            SetStatus($"開きました: {Path.GetFileName(fileName)} ・ {VisibleCount()} 発言 ・ 話者 {_doc.Speakers.Count} 人{md}。" +
                      "名前の書き換え・同じ名前でまとめる・右クリックで話者の変更ができます");
        }
        catch (Exception ex)
        {
            App.Log("MinutesOpen", ex);
            SetStatus("開けませんでした: " + ex.Message);
        }
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (_savingProject)
        {
            SetStatus("プロジェクトの保存・読み込みの途中です。終わってから「新規」を押してください");
            return;
        }
        if (_busy)
        {
            SetStatus("記録の準備中・停止中です。終わってから「新規」を押してください");
            return;
        }
        LeaveTextBox();
        if (_doc.Entries.Count > 0 &&
            MessageBox.Show(this, "今の議事録を閉じて新しく始めますか？\n(内容は自動保存されています)", "GetText",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        if (_service.IsTranscribingFile)
        {
            SetStatus("ファイルの文字起こしを中止してから始めてください");
            return;
        }
        if (_service.IsRecording) await StopWithBusyAsync();
        AutoSave();
        ResetDocument();
        SetStatus("新しい議事録を始めます");
    }

    private void ResetDocument()
    {
        // 記録もファイルの文字起こしもしていなければ、この後に届く結果は前の議事録のもの (次に始めるまで捨てる)
        _dropLate = !_service.IsRecording && !_service.IsTranscribingFile;
        _docGeneration++;
        _doc.Clear();
        _doc.ShowTranslations = TranslateCheck.IsChecked == true;
        _deleted.Clear();
        _translateQueue.Clear();
        _editedForTranslation.Clear();
        _hooked.Clear();
        _autoSavePath = null;
        _openedProject = null;
        _recordedBefore = TimeSpan.Zero;
        _corrections = 0;
        _voiceNames.Clear();
        _enrolled.Clear();
        HideSummary();
        StopPlayback();
        _player?.Close();
        _playerPath = null;
        MarkedOnlyToggle.IsChecked = false;
        SearchBox.Text = ""; // 前の議事録の検索が残って、開いた議事録が空に見えないように
        ElapsedText.Text = "00:00:00";
        RecordLabel.Text = "記録を開始";
        if (_state == "ready" && !_service.IsRecording && !_service.IsTranscribingFile) SetState("ready"); // 「停止中」を残さない
        UpdateCorrectionStatus();
    }

    /// <summary>記録中・ファイルの文字起こし中か (アプリを閉じる前の確認に使う)。</summary>
    public bool IsBusy => _service.IsRecording || _service.IsTranscribingFile || _burning;

    /// <summary>記録中・文字起こし中なら、止めてよいかを聞く。止めてよければ true。</summary>
    public bool ConfirmStop(string what)
    {
        if (!IsBusy || App.DemoMode) return true;
        var doing = _service.IsRecording ? "記録中" : _service.IsTranscribingFile ? "ファイルの文字起こし中" : "字幕つきの動画を作っている途中";
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        bool yes = MessageBox.Show(this, $"{doing}です。止めて{what}か？\n(ここまでの議事録は自動保存されています)",
            "GetText — 議事録", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (yes)
        {
            _burnQueue.Clear(); // (続けて作る予定だった分も作らない)
            _burnCts?.Cancel(); // 字幕つきの動画は作りかけを消して中止する (録画と字幕のファイルは残る)
        }
        return yes;
    }

    private bool _closeConfirmed;

    private bool _closeBusy; // 止めている途中・音声の変換を待っている途中 (重ねて閉じる処理をしない)

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeBusy)
        {
            e.Cancel = true;
            return;
        }
        if (!_closeConfirmed && !_closed)
        {
            if (!ConfirmStop("、議事録の画面を閉じます"))
            {
                e.Cancel = true;
                return;
            }
            _closeConfirmed = true;
        }
        SavePlacement();
        LeaveTextBox();
        if (IsStopping)
        {
            // 残りの音声を文字にしている途中なら、終わってから閉じる (最後の発言と音声を議事録に入れる)
            e.Cancel = true;
            _closeBusy = true;
            _closingWindow = true; // (閉じるので、字幕つきの動画は作らない)
            ((UIElement)Content).IsEnabled = false;
            await WaitStoppedAsync();
            _closeBusy = false;
            Close();
            return;
        }
        if (_savingProject)
        {
            // プロジェクトの保存 (音声の取り出し) の途中なら、終わってから閉じる
            e.Cancel = true;
            _closeBusy = true;
            ((UIElement)Content).IsEnabled = false;
            SetStatus("プロジェクトを保存しています… 終わると閉じます");
            while (_savingProject) await Task.Delay(200);
            _closeBusy = false;
            Close();
            return;
        }
        if (_encodes.Any(t => !t.IsCompleted) && !_closingAfterEncode)
        {
            // 記録した音声を小さなファイルにしている途中なら、終わるまで (最大 2 分) 待ってから閉じる
            e.Cancel = true;
            _closingAfterEncode = true;
            _closeBusy = true;
            ((UIElement)Content).IsEnabled = false; // 待つ間に記録を始めたりできないようにする
            SetStatus("記録した音声を保存しています… 終わると閉じます");
            await Task.WhenAny(Task.WhenAll(_encodes), Task.Delay(TimeSpan.FromMinutes(2)));
            _closeBusy = false;
            Close();
            return;
        }
        if (_service.IsRecording)
        {
            e.Cancel = true;
            _closeBusy = true;
            _closingWindow = true; // (閉じるので、字幕つきの動画は作らない。後で開き直して「保存…」から作れる)
            ((UIElement)Content).IsEnabled = false;
            try
            {
                await StopAsync();
            }
            catch (Exception ex)
            {
                App.Log("MinutesCloseStop", ex);
            }
            _closeBusy = false;
            Close();
            return;
        }
        _closed = true;
        _service.CancelSummary();
        StopPlayback();
        _player?.Close();
        _loadTimer.Stop();
        AutoSave();
        _service.Dispose(); // 準備中なら補助プロセスも止める (準備中に押した記録の開始も、ここで取りやめになる)
        DeleteBurnPart(); // 作りかけの字幕つきの動画は残さない
    }

    private bool _closed;
    private bool _dropLate;
    private MinutesEntry? _editingEntry;
    private int _docGeneration;

    // ───────── 要約 ─────────

    private bool _settingSummary;

    /// <summary>今の発言から、概要・決定事項・やること・主な論点を PC 内の AI でまとめる。</summary>
    private async void Summarize_Click(object sender, RoutedEventArgs e)
    {
        if (_service.IsSummarizing) return;
        var transcript = _doc.SummaryTranscript();
        if (transcript.Length == 0)
        {
            SetStatus("要約する発言がありません");
            return;
        }
        if (!string.IsNullOrWhiteSpace(_doc.Summary) && SummaryPanel.Visibility == Visibility.Visible && sender == SummarizeButton)
        {
            // 要約があるときの「要約」ボタンは、欄をたたんでいれば開くだけ
            if (SummaryBody.Visibility != Visibility.Visible) SummaryFold_Click(sender, e);
            if (MessageBox.Show(this, "今の発言で要約を作り直しますか？\n(書き直した要約は置き換わります)", "GetText — 議事録",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
        }
        var started = DateTime.Now;
        int generation = _docGeneration;
        LeaveTextBox();
        SummaryPanel.Visibility = Visibility.Visible;
        SummaryBody.Visibility = Visibility.Visible;
        SummaryFoldButton.Content = "\uE70E";
        SummaryCancelButton.Visibility = Visibility.Visible;
        SummaryRedoButton.IsEnabled = SummarizeButton.IsEnabled = false;
        SummaryProgress.Visibility = Visibility.Visible;
        SummaryProgress.IsIndeterminate = true;
        SummaryState.Text = "AI を準備しています…";
        SetStatus("要約を作っています (PC 内の AI。長い会議では数分かかります)");
        // 経過時間と (区切って作るときは) 残り時間を 1 秒ごとに出す (CPU だけの PC では数分〜十数分かかるため)
        string stage = "AI を準備しています…";
        DateTime? firstPart = null;
        int partsDone = 0, partsTotal = 0;
        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - started;
            string rest = "";
            if (firstPart is { } first && partsDone > 0 && partsTotal > partsDone)
            {
                var per = (DateTime.Now - first).TotalSeconds / partsDone;
                rest = $" ・ 残り約 {Math.Max(1, Math.Ceiling(per * (partsTotal - partsDone) / 60)):0} 分";
            }
            SummaryState.Text = $"{stage} 経過 {elapsed:m\\:ss}{rest}";
        };
        tick.Start();
        try
        {
            var text = await _service.SummarizeAsync(transcript, (done, total) => Dispatcher.BeginInvoke(() =>
            {
                if (total <= 0)
                {
                    stage = "AI を読み込んでいます…";
                    return;
                }
                firstPart ??= DateTime.Now;
                partsDone = done;
                partsTotal = total;
                SummaryProgress.IsIndeterminate = false;
                SummaryProgress.Value = (double)done / total;
                stage = total == 1 ? "要約を作っています…" : $"要約を作っています… ({done}/{total})";
            }));
            tick.Stop();
            var took = DateTime.Now - started;
            if (generation != _docGeneration) return; // 新規・開くで中止した (今の議事録の表示を変えない)
            if (text == null)
            {
                SummaryState.Text = "中止しました";
                if (string.IsNullOrWhiteSpace(_doc.Summary)) HideSummary();
                SetStatus("要約を中止しました");
                return;
            }
            if (generation != _docGeneration)
            {
                SetStatus("要約を作っている間に議事録が替わったので、作った要約は使いませんでした");
                return;
            }
            _doc.Summary = text;
            // AI は発言にない内容を書くことがあるので、確かめるよう添える
            ShowSummary(text, $"PC 内の AI が {took:m\\:ss} で作成 ・ 内容は確かめてから使ってください");
            AutoSave();
            SetStatus("要約を作りました ・ 保存・コピーの先頭にも入ります");
        }
        catch (Exception ex)
        {
            App.Log("MinutesSummary", ex);
            SummaryState.Text = "作れませんでした";
            if (string.IsNullOrWhiteSpace(_doc.Summary)) HideSummary();
            SetStatus("要約を作れませんでした: " + ex.Message);
        }
        finally
        {
            tick.Stop();
            SummaryCancelButton.Visibility = Visibility.Collapsed;
            SummaryRedoButton.IsEnabled = SummarizeButton.IsEnabled = true;
            SummaryProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowSummary(string text, string state)
    {
        _settingSummary = true;
        SummaryBox.Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        _settingSummary = false;
        SetSummaryEditing(false);
        SummaryState.Text = state;
        SummaryPanel.Visibility = Visibility.Visible;
        SummaryBody.Visibility = Visibility.Visible;
        SummaryFoldButton.Content = "\uE70E";
    }

    private void HideSummary()
    {
        if (_service.IsSummarizing) _service.CancelSummary();
        _settingSummary = true;
        SummaryBox.Text = "";
        _settingSummary = false;
        SetSummaryEditing(false);
        SummaryPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>\u66F8\u304D\u76F4\u3059 (Markdown \u306E\u307E\u307E) \u304B\u3001\u898B\u51FA\u3057\u3068\u7B87\u6761\u66F8\u304D\u306B\u6574\u3048\u3066\u898B\u305B\u308B\u304B\u3002</summary>
    private void SetSummaryEditing(bool editing)
    {
        SummaryBox.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        SummaryViewScroll.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        SummaryEditButton.Content = editing ? "\uE73E" : "\uE70F"; // \u66F8\u304D\u76F4\u3057\u4E2D\u306F\u300C\u5B8C\u4E86\u300D\u306E\u5370
        if (!editing) RenderSummary(SummaryBox.Text);
    }

    private void SummaryEdit_Click(object sender, RoutedEventArgs e)
    {
        bool editing = SummaryBox.Visibility != Visibility.Visible;
        if (SummaryBody.Visibility != Visibility.Visible) SummaryFold_Click(sender, e);
        SetSummaryEditing(editing);
        if (editing)
        {
            SummaryBox.Focus();
            SummaryBox.CaretIndex = SummaryBox.Text.Length;
        }
    }

    /// <summary>\u8981\u7D04\u306E Markdown (### \u898B\u51FA\u3057\u3001- \u7B87\u6761\u66F8\u304D) \u3092\u753B\u9762\u7528\u306B\u4E26\u3079\u308B\u3002</summary>
    private void RenderSummary(string markdown)
    {
        SummaryView.Children.Clear();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var heading = new TextBlock
                {
                    Text = line.TrimStart('#').Trim(),
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, SummaryView.Children.Count == 0 ? 0 : 8, 0, 2),
                    TextWrapping = TextWrapping.Wrap,
                };
                heading.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                heading.SetResourceReference(TextBlock.FontSizeProperty, "EntrySmallFontSize");
                SummaryView.Children.Add(heading);
                continue;
            }
            bool bullet = line.StartsWith("- ") || line.StartsWith("\u30FB") || line.StartsWith("* ");
            var body = bullet ? line[(line.StartsWith('\u30FB') ? 1 : 2)..].Trim() : line;
            var text = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap };
            // \u300C\u306A\u3057\u300D\u306F\u8584\u304F\u51FA\u3059
            text.SetResourceReference(TextBlock.ForegroundProperty, body == "\u306A\u3057" ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
            text.SetResourceReference(TextBlock.FontSizeProperty, "EntrySmallFontSize");
            if (!bullet)
            {
                text.Margin = new Thickness(0, 1, 0, 1);
                SummaryView.Children.Add(text);
                continue;
            }
            var row = new DockPanel { Margin = new Thickness(4, 1, 0, 1) };
            var dot = new TextBlock { Text = "\u2022", Margin = new Thickness(0, 0, 8, 0) };
            dot.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            dot.SetResourceReference(TextBlock.FontSizeProperty, "EntrySmallFontSize");
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(text);
            SummaryView.Children.Add(row);
        }
    }

    private void SummaryCancel_Click(object sender, RoutedEventArgs e)
    {
        _service.CancelSummary();
        SummaryState.Text = "中止しています…";
    }

    // 書き直しを終えて要約の欄から出たら、整えた表示に戻す
    private void SummaryBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject d && (d == SummaryEditButton || SummaryPanel.IsAncestorOf(d))) return;
        if (SummaryBox.Visibility == Visibility.Visible) SetSummaryEditing(false);
    }

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SummaryBox.Text)) return;
        try
        {
            Clipboard.SetText(SummaryBox.Text);
            SetStatus("要約をコピーしました");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            SetStatus("クリップボードにアクセスできませんでした");
        }
    }

    private void SummaryFold_Click(object sender, RoutedEventArgs e)
    {
        bool open = SummaryBody.Visibility != Visibility.Visible;
        SummaryBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SummaryFoldButton.Content = open ? "\uE70E" : "\uE70D";
    }

    private void SummaryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingSummary) return;
        _doc.Summary = string.IsNullOrWhiteSpace(SummaryBox.Text) ? null : SummaryBox.Text;
        _autoSave.Stop();
        _autoSave.Start();
    }

    // ───────── 重要な発言 (★) ─────────

    private void ToggleMark_Click(object sender, RoutedEventArgs e) => ToggleMark(TargetsOf(sender));

    /// <summary>発言に ★ を付ける (すべてに付いていれば外す)。</summary>
    private void ToggleMark(List<MinutesEntry> targets)
    {
        if (targets.Count == 0)
        {
            SetStatus("★ を付ける発言を選んでください (発言をクリックしてから Ctrl+B)");
            return;
        }
        bool mark = !targets.All(t => t.IsMarked);
        foreach (var t in targets) t.IsMarked = mark;
        SetStatus(mark ? $"{targets.Count} 件の発言に ★ を付けました (保存にも入り、要約で重視されます)" : $"{targets.Count} 件の発言の ★ を外しました");
    }

    private void UpdateMarkedCount()
    {
        int marked = _doc.Entries.Count(en => en.IsMarked);
        MarkedCount.Text = marked > 0 ? $"重要 {marked}" : "重要";
        if (marked == 0 && MarkedOnlyToggle.IsChecked == true)
        {
            MarkedOnlyToggle.IsChecked = false;
            CollectionViewSource.GetDefaultView(_doc.Entries).Refresh();
        }
    }

    private void MarkedOnly_Click(object sender, RoutedEventArgs e)
    {
        if (MarkedOnlyToggle.IsChecked == true && !_doc.Entries.Any(en => en.IsMarked))
        {
            MarkedOnlyToggle.IsChecked = false;
            SetStatus("★ の付いた発言はまだありません。発言の右端の ☆ を押すか、発言を選んで Ctrl+B で付けられます");
            return;
        }
        CollectionViewSource.GetDefaultView(_doc.Entries).Refresh();
        SetStatus(MarkedOnlyToggle.IsChecked == true ? "★ の付いた重要な発言だけを表示しています" : "すべての発言を表示しています");
    }

    // ───────── 発言の音声を聞く (ファイルから作った議事録) ─────────

    private MediaPlayer? _player;
    private string? _playerPath;
    private MinutesEntry? _playing;
    private TimeSpan _playFrom;
    // 発言の終わりで止める (細かく確かめないと、次の言葉が少し聞こえる)
    private readonly DispatcherTimer _playStop = new(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(15) };
    private TimeSpan _playUntil;

    private void Time_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MinutesEntry entry || entry.IsNote) return;
        if (!_doc.FromFile && _doc.AudioAt(entry) == null)
        {
            SetStatus("この発言の音声は保存されていません (「音声も保存」をオンにして記録した発言だけ聞けます)");
            return;
        }
        PlayEntry(entry);
        e.Handled = true;
    }

    // 発言の音声を聞く範囲の確認 (PlaybackSelfTest) 用
    internal MinutesDocument Document => _doc;
    internal void LoadForTest(MinutesFile file)
    {
        ResetDocument();
        _doc.Load(file);
    }
    internal void PlayForTest(MinutesEntry entry) => PlayEntry(entry);
    internal bool IsPlayingForTest => _playing != null;

    private void PlayEntry_Click(object sender, RoutedEventArgs e)
    {
        if (ClickedEntry(sender) is { } entry) PlayEntry(entry); // 複数選んでいても、右クリックした発言の音声
    }

    /// <summary>発言の音声を、ファイルのその位置から再生する (再生中の発言なら止める)。</summary>
    private void PlayEntry(MinutesEntry entry)
    {
        if (_playing == entry)
        {
            StopPlayback();
            return;
        }
        if (entry.IsNote) return;
        if (_doc.AudioAt(entry) is not { } audio)
        {
            if (!_doc.FromFile)
            {
                SetStatus("この発言の音声は保存されていません (「音声も保存」をオンにして記録した発言だけ聞けます)");
                return;
            }
            audio = ("", entry.Offset);
        }
        var path = audio.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            // 別の PC で作った議事録などで音声のファイルが見つからないときは、場所を選んでもらう
            var name = _doc.FromFile ? _doc.Source : Path.GetFileName(path);
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"音声のファイル ({name}) の場所を選んでください",
                Filter = TranscriptionService.FileFilter,
                FileName = name,
            };
            if (dialog.ShowDialog(this) != true) return;
            if (_doc.FromFile) _doc.SourcePath = dialog.FileName;
            else RelocateAudio(path, dialog.FileName);
            path = dialog.FileName;
            AutoSave();
        }
        StopPlayback();
        try
        {
            _player ??= CreatePlayer();
            _playing = entry;
            // この発言の範囲だけを流す (前後の発言の声は入れない)
            var range = _doc.PlayRange(entry);
            _playFrom = range?.From ?? (audio.Position > TimeSpan.FromSeconds(0.05) ? audio.Position - TimeSpan.FromSeconds(0.05) : TimeSpan.Zero);
            _playUntil = range?.Until ?? audio.Position + entry.Span + TimeSpan.FromSeconds(0.05);
            if (_playerPath != path)
            {
                _playerPath = path;
                _player.Open(new Uri(path)); // 開けたら MediaOpened で再生を始める
            }
            else
                StartPlayback();
            SetStatus($"▶ {entry.TimeText} の発言を再生しています (もう一度クリック、または Esc で止める)");
        }
        catch (Exception ex)
        {
            _playing = null;
            SetStatus("再生できませんでした: " + ex.Message);
        }
    }

    private MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer();
        player.MediaOpened += (_, _) => { if (_playing != null) StartPlayback(); };
        player.MediaEnded += (_, _) => StopPlayback(finished: true);
        player.MediaFailed += (_, e) =>
        {
            _playing = null;
            _playerPath = null;
            SetStatus("このファイルは再生できませんでした (Windows が再生に対応していない形式の可能性があります): " + e.ErrorException.Message);
        };
        _playStop.Tick += (_, _) =>
        {
            if (_player == null || _playing == null || _player.Position >= _playUntil) StopPlayback(finished: true);
        };
        return player;
    }

    private void StartPlayback()
    {
        if (_player == null) return;
        _player.Position = _playFrom;
        _player.Play();
        _playStop.Start();
    }

    private void StopPlayback(bool finished = false)
    {
        _playStop.Stop();
        if (_playing == null) return;
        _player?.Pause();
        _playing = null;
        SetStatus(finished ? "再生が終わりました" : "再生を止めました");
    }

    // ───────── 記録した音声の保存 ─────────

    private readonly List<Task> _encodes = [];
    private bool _closingAfterEncode;

    // ───────── 会議の画面の録画・字幕つきの動画 ─────────

    private ScreenRecorder? _videoRecorder;
    private bool _burning;
    private bool _closingWindow;
    private readonly List<AudioPart> _sessionVideoParts = []; // この記録で録画したもの (止めたら字幕つきの動画を作る)
    private readonly Queue<(IReadOnlyList<AudioPart> Parts, string? Output)> _burnQueue = new();
    private CancellationTokenSource? _burnCts;
    private string? _burnTarget; // 作っている字幕つきの動画 (中止したら作りかけを消す)

    private void DeleteBurnPart()
    {
        if (_burnTarget == null) return;
        try { File.Delete(_burnTarget + ".part"); } catch { }
    }

    private void RecordVideo_Click(object sender, RoutedEventArgs e)
    {
        _settings.MinutesRecordVideo = RecordVideoCheck.IsChecked == true;
        _settings.MinutesSubtitledVideo = SubtitleVideoCheck.IsChecked == true;
        _settings.Save();
        SubtitleVideoCheck.IsEnabled = _settings.MinutesRecordVideo;
    }

    /// <summary>録画の保存先 (「画面の録画」と同じ。設定が空なら「ビデオ\GetText」)。</summary>
    private string VideoFolder => string.IsNullOrWhiteSpace(_settings.RecordFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "GetText")
        : _settings.RecordFolder;

    /// <summary>「会議の画面も録画する」なら、選んだアプリのウィンドウ (すべての音なら画面全体) を録画し始める。失敗しても議事録は続ける。</summary>
    private async Task StartVideoAsync(WindowInfo? window, bool mic)
    {
        if (!_settings.MinutesRecordVideo || App.DemoMode || window == null || !ScreenRecorder.IsSupported) return;
        var path = Path.Combine(VideoFolder, $"議事録_{DateTime.Now:yyyyMMdd_HHmmss}_録画.mp4");
        var options = new RecordOptions(Fps: 30, HighQuality: _settings.RecordHighQuality, WindowAudio: true, Microphone: mic,
            MicrophoneDevice: _settings.MinutesMicDevice);
        try
        {
            var recorder = window.IsAll
                ? await ScreenRecorder.StartMonitorAsync(ScreenRecorder.PrimaryMonitor(), path, options)
                : await ScreenRecorder.StartWindowAsync(window.Handle, window.ProcessId, path, options);
            if (!_service.IsRecording || IsStopping || _closed)
            {
                // 録画を始めている間に、記録が止められた・画面が閉じられた
                try { await recorder.StopAsync(); } catch { }
                try { File.Delete(path); } catch { }
                return;
            }
            _videoRecorder = recorder;
            Action<string> failed = message => Dispatcher.BeginInvoke(async () =>
            {
                if (_videoRecorder != recorder) return;
                // 録画を止めてそこまでを保存する (議事録の記録は続ける)
                ShowNotice("会議の画面の録画が止まりました (" + message + ")。議事録の記録は続けています。", "warning");
                await StopVideoRecorderAsync();
            });
            recorder.Failed += failed;
            if (recorder.Failure is { } early) failed(early);
        }
        catch (Exception ex)
        {
            App.Log("MinutesVideo", ex);
            ShowNotice("会議の画面を録画できませんでした (" + ex.Message + ")。議事録の記録は続けます。", "warning");
        }
    }

    private Task _videoStopping = Task.CompletedTask; // 録画を書き終えている途中
    private ScreenRecorder? _stoppingRecorder;

    /// <summary>録画を止めて書き終え、保存できたら議事録に加える (できなければ知らせる)。止めている途中ならそれを待つ。</summary>
    private Task StopVideoRecorderAsync()
    {
        var recorder = _videoRecorder;
        if (recorder == null) return _videoStopping;
        _videoRecorder = null;
        _stoppingRecorder = recorder;
        return _videoStopping = StopCoreAsync();

        async Task StopCoreAsync()
        {
            try
            {
                await recorder.StopAsync();
                AddVideoPart(new AudioPart(recorder.Path, recorder.StartedAt, recorder.Duration.TotalSeconds));
            }
            catch (Exception ex)
            {
                App.Log("MinutesVideoStop", ex);
                ShowNotice("会議の画面の録画を保存できませんでした: " + ex.Message, "warning");
            }
            finally
            {
                if (_stoppingRecorder == recorder) _stoppingRecorder = null;
            }
        }
    }

    /// <summary>録画を議事録に加える (字幕つきの動画は、記録を止めて発言がそろってから作る)。</summary>
    private void AddVideoPart(AudioPart part)
    {
        if (_doc.VideoParts.Any(p => SamePath(p.Path, part.Path))) return;
        _doc.VideoParts.Add(part);
        _sessionVideoParts.Add(part);
        AutoSave();
    }

    /// <summary>記録を止めて発言がそろったら: 録画の隣に字幕のファイル (.srt) を置き、必要なら字幕つきの動画を作り始める。</summary>
    private void FinishVideoParts()
    {
        if (_sessionVideoParts.Count == 0) return;
        var parts = _sessionVideoParts.ToList();
        _sessionVideoParts.Clear();
        foreach (var part in parts) WriteVideoSubtitles(part);
        if (_closingWindow || _closed) return;
        if (_settings.MinutesSubtitledVideo) _ = BurnSubtitlesAsync(parts, null);
        else
            ShowNotice($"会議の画面を録画しました: {Path.GetFileName(parts[^1].Path)}", "info", "フォルダを開く", () => ShowInFolder(parts[^1].Path));
    }

    /// <summary>アプリの終了: 待たずに録画を止めて議事録に加える (字幕つきの動画は作らない)。</summary>
    private void StopVideoNow()
    {
        foreach (var part in _sessionVideoParts) WriteVideoSubtitles(part); // (途中で止まって保存した録画の字幕のファイル)
        _sessionVideoParts.Clear();
        // 録画中のもの・書き終えている途中のもの (止める操作の後に終了した) を書き終える
        var recorder = _videoRecorder ?? _stoppingRecorder;
        if (recorder == null) return;
        _videoRecorder = _stoppingRecorder = null;
        try
        {
            // 書き終えた (再生できる) ときだけ議事録に入れる。間に合わなかったファイルは目次が無く再生できない
            if (Task.Run(recorder.StopAsync).Wait(TimeSpan.FromSeconds(15)) && recorder.FileSize > 0
                && !_doc.VideoParts.Any(p => SamePath(p.Path, recorder.Path)))
            {
                _doc.VideoParts.Add(new AudioPart(recorder.Path, recorder.StartedAt, recorder.Duration.TotalSeconds));
                WriteVideoSubtitles(_doc.VideoParts[^1]);
            }
        }
        catch (Exception ex)
        {
            App.Log("MinutesVideoShutdown", ex);
        }
    }

    // 録画の隣に字幕のファイル (同じ名前の .srt) を置く (動画プレーヤーで字幕を出せる)
    private void WriteVideoSubtitles(AudioPart part)
    {
        try
        {
            AtomicFile.WriteAllText(Path.ChangeExtension(part.Path, ".srt"),
                _doc.ToSubtitles(vtt: false, ExportOptions(), part.Start, TimeSpan.FromSeconds(part.Seconds)));
        }
        catch (Exception ex)
        {
            App.Log("MinutesVideoSrt", ex);
        }
    }

    /// <summary>
    /// 録画した動画に、今の発言を字幕にして焼き込んだ動画を作る (Python の処理。GPU があれば速い)。
    /// output を渡さなければ、録画の隣に「…_字幕.mp4」として作る。
    /// </summary>
    private async Task BurnSubtitlesAsync(IReadOnlyList<AudioPart> parts, string? output)
    {
        if (_closed) return;
        if (_burning)
        {
            _burnQueue.Enqueue((parts, output));
            SetStatus("字幕つきの動画を作っています。今の分が終わったら、続けて作ります");
            return;
        }
        _burning = true;
        _burnCts = new CancellationTokenSource();
        var made = new List<string>();
        try
        {
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (!File.Exists(part.Path))
                {
                    ShowNotice("録画のファイルが見つかりません: " + part.Path, "warning");
                    continue;
                }
                WriteVideoSubtitles(part); // (直した発言で字幕のファイルも作り直す)
                var target = output == null
                    ? Path.Combine(Path.GetDirectoryName(part.Path)!, Path.GetFileNameWithoutExtension(part.Path) + "_字幕.mp4")
                    : parts.Count == 1 ? output
                    : Path.Combine(Path.GetDirectoryName(output)!, $"{Path.GetFileNameWithoutExtension(output)}_{i + 1}.mp4");
                // 録画そのものには上書きしない (元の録画が無くなり、次に作ると字幕が二重になる)
                if (_doc.VideoParts.Any(p => SamePath(p.Path, target)))
                    target = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + "_字幕.mp4");
                var cues = _doc.SubtitleCues(ExportOptions(), part.Start, TimeSpan.FromSeconds(part.Seconds));
                string label = parts.Count > 1 ? $" ({i + 1}/{parts.Count})" : "";
                SetStatus($"字幕つきの動画を作っています{label}…");
                _burnTarget = target;
                await _service.BurnSubtitlesAsync(part.Path, target, cues,
                    (done, total) => Dispatcher.BeginInvoke(() =>
                        SetStatus($"字幕つきの動画を作っています{label}… {(total > 0 ? done / total : 0):P0}")),
                    _burnCts.Token);
                _burnTarget = null;
                made.Add(target);
            }
            if (made.Count > 0)
            {
                var last = made[^1];
                SetStatus("字幕つきの動画を保存しました: " + last);
                ShowNotice($"字幕つきの動画を保存しました: {Path.GetFileName(last)}" + (made.Count > 1 ? $" ほか {made.Count - 1} 本" : ""),
                    "info", "フォルダを開く", () => ShowInFolder(last));
            }
        }
        catch (Exception ex) when (_burnCts.IsCancellationRequested)
        {
            SetStatus("字幕つきの動画を作るのを中止しました (" + ex.Message + ")");
        }
        catch (Exception ex)
        {
            App.Log("MinutesBurn", ex);
            ShowNotice("字幕つきの動画を作れませんでした: " + ex.Message, "warning");
        }
        finally
        {
            DeleteBurnPart(); // (中止・失敗したときの作りかけ)
            _burnTarget = null;
            _burning = false;
            _burnCts = null;
            if (!_closed && _burnQueue.Count > 0)
            {
                var (nextParts, nextOutput) = _burnQueue.Dequeue();
                _ = BurnSubtitlesAsync(nextParts, nextOutput);
            }
        }
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static void ShowInFolder(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    private void SaveAudio_Click(object sender, RoutedEventArgs e)
    {
        _settings.MinutesSaveAudio = SaveAudioCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>今回の記録の音声を書く WAV の場所 (議事録の自動保存と同じフォルダ)。</summary>
    private string NewAudioPath(bool newSession)
    {
        var start = newSession ? DateTime.Now : _doc.StartedAt ?? DateTime.Now;
        int n = newSession ? 1 : _doc.AudioParts.Count + 1;
        return Path.Combine(AutoSaveDir, $"議事録_{start:yyyyMMdd_HHmmss}_音声{n}.wav");
    }

    /// <summary>止めた記録の音声を議事録に加え、裏で小さな .m4a にする (できたら WAV は消す)。</summary>
    private SessionRecorder? _addedRecording; // 議事録に入れた最後の記録の音声 (終了のときと止め終わったときに重ねて入れない)

    private void OnRecordingSaved(SessionRecorder? recording)
    {
        if (recording == null || ReferenceEquals(recording, _addedRecording)) return;
        _addedRecording = recording;
        if (recording.Duration < TimeSpan.FromSeconds(1.5))
        {
            TryDelete(recording.Path);
            return;
        }
        var part = new AudioPart(recording.Path, recording.Start, recording.Duration.TotalSeconds);
        _doc.AudioParts.Add(part);
        AutoSave();
        var ownerJson = _autoSavePath is { } md ? Path.ChangeExtension(md, ".json") : null;
        var wav = recording.Path;
        var m4a = Path.ChangeExtension(wav, ".m4a");
        _encodes.Add(EncodeAsync());

        async Task EncodeAsync()
        {
            try
            {
                await _service.EncodeAudioAsync(wav, m4a);
                int index = _doc.AudioParts.IndexOf(part);
                if (index >= 0) _doc.AudioParts[index] = part with { Path = m4a };
                else if (ownerJson != null) RewriteAudioPath(ownerJson, wav, m4a); // もう別の議事録を開いている
                if (_playerPath == wav)
                {
                    StopPlayback();
                    _player?.Close();
                    _playerPath = null;
                }
                TryDelete(wav);
                AutoSave();
            }
            catch (Exception ex)
            {
                // 変換できなくても WAV のまま聞ける
                App.Log("MinutesAudioEncode", ex);
            }
        }
    }

    /// <summary>保存済みの議事録データの中の、音声の場所を書き換える (WAV を .m4a にした後)。</summary>
    private static void RewriteAudioPath(string json, string from, string to)
    {
        try
        {
            if (!File.Exists(json)) return;
            var file = MinutesFile.FromJson(File.ReadAllText(json));
            if (file.Audio == null || !file.Audio.Any(a => a.Path == from)) return;
            file = file with { Audio = file.Audio.Select(a => a.Path == from ? a with { Path = to } : a).ToList() };
            AtomicFile.WriteAllText(json, file.ToJson());
        }
        catch (Exception ex)
        {
            App.Log("RewriteAudioPath", ex);
        }
    }

    /// <summary>音声のファイルを別の場所で見つけたら、同じフォルダにあるほかの音声もそこから探す。</summary>
    private void RelocateAudio(string oldPath, string newPath)
    {
        var folder = Path.GetDirectoryName(newPath)!;
        for (int i = 0; i < _doc.AudioParts.Count; i++)
        {
            var p = _doc.AudioParts[i];
            var candidate = p.Path == oldPath ? newPath : Path.Combine(folder, Path.GetFileName(p.Path));
            if (File.Exists(candidate)) _doc.AudioParts[i] = p with { Path = candidate };
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 使用中なら残す
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ───────── メモ ─────────

    private void AddMemo_Click(object sender, RoutedEventArgs e) => AddMemo();

    private void MemoBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddMemo();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            MemoBox.Clear();
            e.Handled = true;
        }
    }

    private void MemoBox_TextChanged(object sender, TextChangedEventArgs e) =>
        MemoHint.Visibility = MemoBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 書いたメモを差し込む。記録中は今の時刻、ファイルでは再生中の位置 (再生していなければ最後の発言の後)、
    /// 止めているときは最後の発言の後。
    /// </summary>
    private void AddMemo()
    {
        var text = MemoBox.Text.Trim();
        if (text.Length == 0)
        {
            SetStatus("メモを書いてから Enter を押してください");
            return;
        }
        if (_doc.FromFile && _doc.StartedAt == null)
        {
            SetStatus("ファイルの文字起こしが始まってから、メモを追加してください");
            return;
        }
        DateTime time;
        var last = _doc.Entries.LastOrDefault();
        if (_service.IsRecording)
            time = DateTime.Now;
        else if (_doc.FromFile && _playing != null && _player != null && _doc.StartedAt is { } fileStart)
            time = fileStart + _player.Position;
        else if (last != null)
            time = last.Time + last.Duration + TimeSpan.FromMilliseconds(1);
        else
            time = DateTime.Now;
        var note = _doc.AddNote(time, text);
        MemoBox.Clear();
        if (AutoScrollCheck.IsChecked == true) EntryList.ScrollIntoView(note);
        _autoSave.Stop();
        _autoSave.Start();
        SetStatus($"メモを追加しました ({note.TimeText})。本文はクリックで書き直せ、右クリックで削除できます");
    }

    // ───────── 用語の登録・人数・覚えた声 ─────────

    private readonly Dictionary<int, string> _voiceNames = [];   // 声から付けた名前 (話者の番号 → 名前)
    private readonly Dictionary<int, string> _enrolled = [];     // この会議で覚えさせた名前 (同じ名前で何度も覚えさせない)

    /// <summary>用語と人数を裏の処理に伝える。</summary>
    private void SendMinutesOptions()
    {
        if (App.DemoMode) return;
        _service.SetTerms(_settings.MinutesTerms);
        _service.SetExpectedSpeakers(SpeakerCountBox.SelectedIndex);
    }

    private void UpdateTermsLabel() =>
        TermsLabel.Text = _settings.MinutesTerms.Count > 0 ? $"用語 {_settings.MinutesTerms.Count}" : "用語";

    private void UpdateVoicesLabel(int count) =>
        VoicesLabel.Text = count > 0 ? $"覚えた声 {count}" : "覚えた声";

    private void Terms_Click(object sender, RoutedEventArgs e)
    {
        if (MinutesDialogs.EditTerms(this, _settings.MinutesTerms) is not { } terms) return;
        _settings.MinutesTerms = terms;
        _settings.Save();
        UpdateTermsLabel();
        if (_service.IsReady) _service.SetTerms(terms);
        SetStatus(terms.Count > 0 ? $"用語を {terms.Count} 件登録しました (次の発言から使います)" : "用語の登録を消しました");
    }

    private void SpeakerCount_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || App.DemoMode) return;
        if (_service.IsReady) _service.SetExpectedSpeakers(SpeakerCountBox.SelectedIndex);
        SetStatus(SpeakerCountBox.SelectedIndex == 0
            ? "人数は声から自動で推定します"
            : $"アプリの音声を {SpeakerCountBox.SelectedIndex} 人以内で聞き分けます (多く分かれたときは、その人数で分け直します)");
    }

    private void Voices_Click(object sender, RoutedEventArgs e)
    {
        MinutesDialogs.ManageVoices(this, name =>
        {
            if (_service.IsReady) _service.ForgetVoice(name);
            else MinutesDialogs.ForgetInFile(name);
        });
        UpdateVoicesLabel(MinutesDialogs.LoadVoices().Count);
    }

    /// <summary>名前を付けた話者の声を覚えさせる (声で聞き分けた話者だけ。設定でオフにできる)。</summary>
    private void RememberVoice(MinutesSpeaker? speaker)
    {
        if (speaker != null && _voiceNames.TryGetValue(speaker.Id, out var auto) && auto != speaker.Name)
            _voiceNames.Remove(speaker.Id); // 声から付けた名前を手で書き換えた (消した) ので、もう付け直さない
        if (speaker == null || App.DemoMode || !_settings.MinutesRememberVoices || !_service.IsReady) return;
        if (speaker.Id is <= 0 or >= 1000 || speaker.IsUnnamed) return;
        if (!_doc.Speakers.Contains(speaker) || _enrolled.TryGetValue(speaker.Id, out var done) && done == speaker.Name) return;
        if (_doc.SameNameAs(speaker) != null) return; // ほかの話者と同じ名前 (まとめていない) の声は覚えない
        _service.EnrollVoice(speaker.Id, speaker.Name);
        _enrolled[speaker.Id] = speaker.Name;
        _voiceNames.Remove(speaker.Id); // 手で付けた名前が優先
    }

    /// <summary>覚えた声に似た話者が見つかった (name が null なら、前に付けた名前の取り消し)。</summary>
    private void OnVoiceMatch(int speaker, string? name)
    {
        if (_dropLate) return;
        if (name == null)
        {
            if (_voiceNames.Remove(speaker, out var old) && _doc.Speakers.FirstOrDefault(s => s.Id == speaker) is { } sp && sp.Name == old)
            {
                sp.Name = sp.AutoName;
                SetStatus($"声が集まったので「{old}」さんの判断を取り消しました");
            }
            return;
        }
        _voiceNames[speaker] = name;
        ApplyVoiceName(speaker, name);
    }

    private void ApplyVoiceName(int id, string name)
    {
        var speaker = _doc.Speakers.FirstOrDefault(s => s.Id == id);
        // 既定の名前のままの話者にだけ付ける (手で付けた名前は変えない。同じ名前の人がもういれば付けない)
        if (speaker == null || !speaker.IsUnnamed || _doc.Speakers.Any(s => s != speaker && s.Name == name)) return;
        speaker.Name = name;
        SetStatus($"声から「{name}」さんと判断して名前を付けました (違うときは名前を書き換えてください)");
    }

    // ───────── 入力と選択の解除 ─────────

    /// <summary>
    /// 窓の中を押したとき: 入力中の文字の欄 (話者の名前・検索・メモ・発言の本文) の外なら入力を終え、
    /// 発言の一覧の行の外なら、選んでいた発言の選択を外す (薄いグレーの表示が残らないように)。
    /// </summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        // 右クリックのメニュー・一覧の開いた欄 (別の窓として出る) の中を押したときは何もしない
        // (選択を外すと、「選んだ 3 件の発言を削除」などが押した 1 件にしか効かなくなる)
        if (PresentationSource.FromDependencyObject(source)?.RootVisual != this) return;
        if (Keyboard.FocusedElement is TextBox focused && !IsWithin(source, focused) && FindParent<TextBox>(source) == null
            && !IsWithin(source, SummaryPanel)
            && FindParent<ComboBox>(source) == null && FindParent<System.Windows.Controls.Primitives.ScrollBar>(source) == null)
            LeaveTextBox();
        if (FindParent<ListBoxItem>(source) == null && FindParent<System.Windows.Controls.Primitives.ScrollBar>(source) == null
            && EntryList.SelectedItems.Count > 0)
            EntryList.UnselectAll();
    }

    /// <summary>入力中の欄から抜ける (名前は確定する)。キーボードの操作が効くよう、発言の一覧にフォーカスを移す。</summary>
    private void LeaveTextBox(TextBox? box = null)
    {
        box ??= Keyboard.FocusedElement as TextBox;
        if (box != null)
        {
            box.Select(box.Text.Length, 0);
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
        EntryList.Focusable = true;
        EntryList.Focus();
    }

    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (var n = node; n != null; n = n is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(n) : LogicalTreeHelper.GetParent(n))
            if (n == ancestor) return true;
        return false;
    }

    private static T? FindParent<T>(DependencyObject node) where T : DependencyObject
    {
        for (var n = node; n != null; n = n is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(n) : LogicalTreeHelper.GetParent(n))
            if (n is T t) return t;
        return null;
    }

    // ───────── 文字起こしの処理が止まったとき ─────────

    private async void OnProcessExited()
    {
        if (_closed) return;
        bool recording = _service.IsRecording;
        if (recording) await StopWithBusyAsync();
        // 起動し直した処理は発言と話者の番号を 1 から数え直すので、今の発言を切り離す (前の発言・話者に当たらないように)
        _doc.Detach();
        _voiceNames.Clear();
        _enrolled.Clear();
        _loading = false;
        _loadTimer.Stop();
        LoadingOverlay.Visibility = Visibility.Collapsed;
        SetState("error");
        SetStatus("文字起こしの処理が止まりました");
        ShowNotice("文字起こしの処理が止まりました (ここまでの議事録は自動保存しています)。もう一度「記録を開始」を押すと、起動し直して続けられます。", "error");
        if (recording)
            MessageBox.Show(this, "文字起こしの処理が止まったため、記録を止めました。\nここまでの議事録は自動保存しています。\n\nもう一度「記録を再開」を押すと、起動し直して続けられます。",
                "GetText — 議事録", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
