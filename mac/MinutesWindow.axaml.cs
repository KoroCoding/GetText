using System.Collections.ObjectModel;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GetText;

/// <summary>議事録の画面を開いたアプリ側 (読み取りの画面) の機能。</summary>
public interface IMinutesHost
{
    /// <summary>読み取りの枠と文字の画面を隠す (議事録だけを使うとき)。</summary>
    void SetOcrHidden(bool hidden);

    Translator Translator { get; }

    /// <summary>設定を開く (議事録の画面の「設定」)。</summary>
    void OpenSettings(SettingsPage page = SettingsPage.Meetings, Window? owner = null) { }

    /// <summary>議事録の表示・文字の大きさを変えた (開いている設定の画面にも反映する)。</summary>
    void MinutesDisplayChanged() { }

    /// <summary>議事録の状態 (記録中など) が変わった (機能を選ぶ画面の表示を変える)。</summary>
    void MinutesStateChanged() { }
}

/// <summary>選んだアプリの音声 (とマイク) を文字起こしし、時刻と話者つきの議事録にする画面 (Mac 版)。</summary>
public partial class MinutesWindow : Window
{
    private static readonly string AutoSaveDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "minutes");

    /// <summary>自動保存した議事録のフォルダ (ホームの「最近の議事録」・コマンドの一覧で使う)。</summary>
    public static string RecentFolder => AutoSaveDir;

    /// <summary>最近の議事録を開く (記録中などで開けなければ、その理由を状態の欄に出す)。</summary>
    internal void OpenRecent(string path)
    {
        if (_busy || _service.IsRecording || _service.IsTranscribingFile)
        {
            SetStatus("記録中・文字起こし中は開けません。停止してから開いてください");
            return;
        }
        LeaveTextBox();
        _ = OpenPathAsync(path);
    }

    /// <summary>記録を始める / 止める (記録のボタンと同じ)。</summary>
    internal void ToggleRecording()
    {
        if (RecordButton.IsEnabled) Record_Click(RecordButton, new RoutedEventArgs());
    }

    /// <summary>ファイルから文字起こしする (「ファイルから…」と同じ)。</summary>
    internal void TranscribeFile()
    {
        if (FileButton.IsEnabled && !_service.IsTranscribingFile) File_Click(FileButton, new RoutedEventArgs());
    }

    private readonly IMinutesHost _host;
    private readonly AppSettings _settings;
    private readonly TranscriptionService _service = new();
    private readonly MinutesDocument _doc = new();
    private readonly FilteredCollection<MinutesEntry> _view;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _autoSave = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _autoSavePath;
    private TimeSpan _recordedBefore;
    private bool _busy;
    private bool _captureWindowAudio;
    private DateTime _lastWindowSound;
    private bool _silenceHintShown;
    private readonly Stack<IReadOnlyList<(int Index, MinutesEntry Entry)>> _deleted = new();
    private DateTime _fileStarted;
    private readonly DispatcherTimer _talkTimes = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<MinutesEntry> _translateQueue = [];
    private readonly HashSet<MinutesEntry> _editedForTranslation = [];
    private readonly HashSet<MinutesEntry> _hooked = [];
    private readonly DispatcherTimer _translateDelay = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool _translating;
    private bool _translateErrorShown;

    // XAML の読み込み用 (使わない)
    public MinutesWindow() : this(new NullHost(), new AppSettings()) { }

    private sealed class NullHost : IMinutesHost
    {
        public void SetOcrHidden(bool hidden) { }
        public Translator Translator { get; } = new();
    }

    public MinutesWindow(IMinutesHost host, AppSettings settings)
    {
        InitializeComponent();
        _host = host;
        _settings = settings;
        ApplyFontSize();
        RestorePlacement();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        HideOcrCheck.IsChecked = settings.HideOcrDuringMinutes;
        if (settings.HideOcrDuringMinutes) host.SetOcrHidden(true);
        Closed += (_, _) => _host.SetOcrHidden(false);

        _view = new FilteredCollection<MinutesEntry>(_doc.Entries, Visible,
            nameof(MinutesEntry.IsFiller), nameof(MinutesEntry.IsMarked));
        EntryList.ItemsSource = _view;
        EmptySteps.ItemsSource = new GuideStep[]
        {
            new(1, "文字にしたいウィンドウと言語を選ぶ",
                "会議アプリやブラウザなど。どのアプリか分からないときは「すべての音」を選んでください。"),
            new(2, "「記録を開始」を押す",
                "話している途中の文字は下に灰色で表示され、話し終わると時刻と話者つきで確定します。録画・録音済みの会議は「ファイルから…」で MP4 や MP3 などを選びます。"),
            new(3, "直して、まとめて、保存する",
                "本文はクリックして書き直せ、右クリックで削除や話者の変更ができます。「要約」で決定事項ややることをまとめ、「保存…」で Markdown・字幕などに書き出せます。"),
        };
        EmptyNotes.Text = "・会議を記録するときは、参加者の同意を得てください。\n" +
                          "・スピーカーで聞くとマイクが相手の声も拾うので、ヘッドホンをおすすめします (重複はある程度自動で除きます)。\n" +
                          "・音声も文字も Mac の外に送りません (要約・翻訳も Mac の中で行います)。";
        SpeakerList.ItemsSource = _doc.Speakers;
        TranslateCheck.IsChecked = settings.MinutesTranslate;
        SpeedBox.ItemsSource = SpeedOptions;
        SpeedBox.SelectedItem = SettingsOptions.Find(SpeedOptions, settings.MinutesSpeed);
        _service.InitialSpeed = settings.MinutesSpeed;
        if (settings.MinutesCorrect is bool correct) CorrectCheck.IsChecked = correct;
        _service.SpeedStatus += (speed, gpu, fast) => Dispatcher.UIThread.Post(() => OnSpeedStatus(speed, gpu, fast));
        UpdateSpeedNote();
        SaveAudioCheck.IsChecked = settings.MinutesSaveAudio;
        RecordVideoCheck.IsChecked = settings.MinutesRecordVideo;
        SubtitleVideoCheck.IsChecked = settings.MinutesSubtitledVideo;
        SubtitleVideoCheck.IsEnabled = settings.MinutesRecordVideo;
        SpeakerCountBox.ItemsSource = new[] { "自動" }.Concat(Enumerable.Range(1, 12).Select(n => $"{n} 人")).ToList();
        SpeakerCountBox.SelectedIndex = 0;
        UpdateTermsLabel();
        UpdateVoicesLabel(Dialogs.LoadVoices().Count);
        _service.VoiceMatch += (speaker, name) => Dispatcher.UIThread.Post(() => OnVoiceMatch(speaker, name));
        _service.ProcessExited += () => Dispatcher.UIThread.Post(OnProcessExited);
        _service.CaptureLost += message => Dispatcher.UIThread.Post(() =>
            ShowNotice(message + " (もう一方の音は記録を続けています)"));
        _service.CaptureEnded += message => Dispatcher.UIThread.Post(async () =>
        {
            // 取り込める音が無くなったら記録を止める (「記録中」のまま何も記録しない状態にしない)
            if (!_service.IsRecording || _busy) return;
            await StopWithBusyAsync();
            ShowNotice(message + " 記録を止めました。接続を確かめてから「記録を再開」を押してください。", "error");
        });
        _service.VoicesChanged += count => Dispatcher.UIThread.Post(() => UpdateVoicesLabel(count));
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
            EmptyHint.IsVisible = _doc.Entries.Count == 0;
            UpdateFillerCount();
            UpdateMarkedCount();
            if (Searching) SearchCount.Text = $"{_view.Count} 件";
        };
        foreach (var l in _languages) l.IsSelected = settings.MinutesLanguages.Contains(l.Code);
        if (!_languages.Any(l => l.IsSelected)) _languages[0].IsSelected = true;
        LanguageList.ItemsSource = _languages;
        UpdateLanguageLabel();
        AutoScrollCheck.IsChecked = settings.MinutesAutoScroll;
        TimeCheck.IsChecked = settings.MinutesShowTime;
        EntryList.Tag = settings.MinutesShowTime;
        FillerCheck.IsChecked = settings.MinutesHideFillers;
        _talkTimes.Tick += (_, _) => { _talkTimes.Stop(); _doc.UpdateTalkTimes(); };
        // 書き直している発言 (相づちを除いていても、書いている途中で消えないように)
        EntryList.AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox { DataContext: MinutesEntry entry }) _editingEntry = entry;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        EntryList.AddHandler(LostFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox { DataContext: MinutesEntry } && _editingEntry != null)
            {
                _editingEntry = null;
                _view.Refresh();
                UpdateFillerCount();
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        EntryList.ContextRequested += EntryList_ContextRequested;
        EntryList.AddHandler(KeyDownEvent, EntryList_KeyDown, RoutingStrategies.Tunnel);
        EntryList.AddHandler(PointerWheelChangedEvent, EntryList_PointerWheel, RoutingStrategies.Tunnel);

        _service.SegmentReceived += segment => Dispatcher.UIThread.Post(() => OnSegment(segment));
        _service.Revised += revision => Dispatcher.UIThread.Post(() => OnRevised(revision));
        _service.Respeaker += changes => Dispatcher.UIThread.Post(() =>
        {
            if (!_dropLate && _doc.ApplySpeakerChanges(changes) > 0)
            {
                RefreshTalkTimes();
                RestartAutoSave();
            }
        });
        _service.LlmStatus += state => Dispatcher.UIThread.Post(() => OnLlmStatus(state));
        _service.Partial += (source, speaker, text) => Dispatcher.UIThread.Post(() => OnPartial(source, speaker, text));
        _service.Lag += seconds => Dispatcher.UIThread.Post(() =>
        {
            if (!_service.IsRecording) return;
            SetStatus(seconds > 0
                ? $"処理が {seconds:0} 秒遅れています (Mac の負荷が高いため)。話し中の表示を省いて追いつくようにしています。"
                : "処理が追いつきました ・ 記録中");
        });
        _service.Error += message => Dispatcher.UIThread.Post(() => ShowNotice(message, "error"));
        _service.Progress += (done, total) => Dispatcher.UIThread.Post(() => OnFileProgress(done, total));
        _service.MultilingualStatus += state => Dispatcher.UIThread.Post(() => OnMultilingualStatus(state));
        _service.ForeignSpeech += () => Dispatcher.UIThread.Post(() =>
            SetStatus("日本語以外 (英語など) が話されているようです。上の「言語」を「英語」なども選ぶと、原文と日本語訳で表示します"));
        _service.Refined += () => Dispatcher.UIThread.Post(() =>
        {
            if (_doc.FromFile && !_service.IsTranscribingFile && _llmState is "gpu" or "cpu" && CorrectCheck.IsChecked == true)
                SetStatus($"文脈による補正も終わりました ・ {VisibleCount()} 発言");
        });
        _service.Level += (source, level) => Dispatcher.UIThread.Post(() =>
        {
            (source == "win" ? WindowLevel : MicLevel).Value = level;
            if (source == "win" && level > 0.02) _lastWindowSound = DateTime.Now;
        }, DispatcherPriority.Background);
        MacHelper.Instance.EventReceived += OnHelperEvent;

        _clock.Tick += (_, _) =>
        {
            UpdateElapsed();
            UpdateTitle();
            CheckSilence();
        };
        _autoSave.Tick += (_, _) => { _autoSave.Stop(); AutoSave(); };
        Closing += OnClosing;
        // 低い窓 (ノート PC の画面など) では要約の欄も低くして、発言の欄を残す
        SizeChanged += (_, e) =>
        {
            double h = Math.Clamp(e.NewSize.Height * 0.24, 90, 190);
            SummaryViewScroll.MaxHeight = h;
            SummaryBox.MaxHeight = h;
            FitSpeakerPanel();
        };
        Opened += (_, _) => FitSpeakerPanel(); // 前回の話者の欄の幅にする
        if (App.DemoMode) return; // 見本の画面: 実際のウィンドウの一覧も音声認識も使わない
        Opened += (_, _) => RefreshWindows();

        if (!TranscriptionService.IsInstalled)
        {
            SetStatus("音声認識が未セットアップです。設定 → 情報 → 「セットアップを実行」でインストールしてください。");
            SetState("setup");
        }
        else
            BeginLoading();
    }

    private bool Searching => (SearchBox.Text ?? "").Trim().Length > 0;

    // 表示する発言 (相づち・★ だけ・検索で絞り込む)
    private bool Visible(MinutesEntry en) =>
        (FillerCheck?.IsChecked != true || !en.IsFiller || en.IsMarked || en == _editingEntry)
        && (MarkedOnlyToggle?.IsChecked != true || en.IsMarked)
        && MatchesSearch(en);

    private void RestartAutoSave()
    {
        _autoSave.Stop();
        _autoSave.Start();
    }

    // ───────── 準備中の表示 ─────────

    private readonly ObservableCollection<LoadStep> _loadSteps = [];
    private readonly DispatcherTimer _loadTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime _loadStarted;
    private bool _loading;

    private void BeginLoading()
    {
        _loadSteps.Clear();
        _loadSteps.Add(new LoadStep("python", "音声認識の準備 (Python の起動)"));
        _loadSteps.Add(new LoadStep("asr", _settings.MinutesSpeed == "accurate" ? "日本語の音声認識モデル (kotoba-whisper)" : "日本語の音声認識モデル (速いモデル)"));
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
        LoadingOverlay.IsVisible = true;
        SetStatus("音声認識モデルを読み込んでいます…");
        _service.LoadingStep += step => Dispatcher.UIThread.Post(() => ActivateStep(step));
        _service.EnsureStartedAsync().ContinueWith(t => Dispatcher.UIThread.Post(() => OnServiceStarted(t)));
    }

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
            LoadingNote.Text = message + "\n設定 → 情報 → 「セットアップを実行」で入れ直すと直ることがあります。";
            LoadingBar.IsVisible = false;
            LoadingHideButton.Content = "閉じる";
            SetStatus("エラー: " + message);
            return;
        }
        foreach (var s in _loadSteps.Where(s => s.Key != "multi")) s.Finish();
        SendMinutesOptions();
        if (_loadSteps.Any(s => s.Key == "multi") && !NeedsMultilingual)
            _loadSteps.Remove(_loadSteps.First(s => s.Key == "multi"));
        if (_loadSteps.Any(s => s.Key == "multi"))
        {
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
        LoadingBar.IsVisible = false;
        if (!_service.IsRecording && !_service.IsTranscribingFile)
            SetStatus($"準備完了 ({(_service.Device == "cuda" ? "GPU" : "CPU")}・{(_service.Speed == "light" ? "軽さ優先" : "正確さ優先")}・{took.TotalSeconds:0} 秒) ・ 音を選んで「記録を開始」を押してください");
        UpdateSpeedNote();
        DispatcherTimer.RunOnce(() => LoadingOverlay.IsVisible = false, TimeSpan.FromMilliseconds(700));
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

    private void ShowLoadingIfBusy(string note)
    {
        if (!_loading) return;
        LoadingNote.Text = "音声認識の AI を読み込んでいます。" + note;
        LoadingOverlay.IsVisible = true;
    }

    private void LoadingHide_Click(object? sender, RoutedEventArgs e)
    {
        LoadingOverlay.IsVisible = false;
        if (_loading) SetStatus("裏で準備しています… (終わるとここに「準備完了」と出ます。準備中に「記録を開始」を押すと、準備ができしだい記録を始めます)");
    }

    internal void SetStatus(string text)
    {
        StatusText.Text = text;
        ToolTip.SetTip(StatusText, text);
    }

    // ───────── 大事な知らせ (警告・エラー) ─────────

    private Action? _noticeAction;

    /// <summary>警告やエラーを、下の欄の上に目立つ帯で出す (閉じるまで残る)。severity は info / warning / error。</summary>
    internal void ShowNotice(string text, string severity = "warning", string? action = null, Action? onAction = null)
    {
        // その場の帯: 色はデザイントークンの状態の色、アイコン (線の絵) と文字でも種類を示す
        var (icon, color, subtle) = severity switch
        {
            "error" => (AppIcon.Error, (Func<Palette, uint>)(p => p.Critical), (Func<Palette, uint>)(p => p.CriticalSubtle)),
            "info" => (AppIcon.Info, p => p.AccentText, p => p.AccentSubtle),
            _ => (AppIcon.Warning, p => p.Warning, p => p.WarningSubtle),
        };
        NoticeBar.Background = MacTheme.BrushOf(subtle);
        NoticeBar.BorderBrush = MacTheme.BrushOf(p => p.Border);
        NoticeIcon.Icon = icon;
        NoticeIcon.Foreground = MacTheme.BrushOf(color);
        NoticeText.Text = text;
        _noticeAction = onAction;
        NoticeAction.Content = action;
        NoticeAction.IsVisible = action != null && onAction != null;
        NoticeBar.IsVisible = true;
    }

    private void HideNotice() => NoticeBar.IsVisible = false;

    private void NoticeClose_Click(object? sender, RoutedEventArgs e) => HideNotice();

    private void NoticeAction_Click(object? sender, RoutedEventArgs e)
    {
        var action = _noticeAction;
        HideNotice();
        action?.Invoke();
    }

    // ───────── 記録中はスリープさせない ─────────

    private Process? _caffeinate;

    /// <summary>記録中・文字起こし中は Mac がスリープしないようにする (caffeinate。画面は消えてもよい)。</summary>
    private void KeepAwake(bool on)
    {
        if (App.DemoMode || on == (_caffeinate is { HasExited: false })) return;
        try
        {
            if (on)
                _caffeinate = Process.Start(new ProcessStartInfo("/usr/bin/caffeinate", ["-i", "-w", Environment.ProcessId.ToString()])
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            else
            {
                _caffeinate?.Kill();
                _caffeinate?.Dispose();
                _caffeinate = null;
            }
        }
        catch (Exception)
        {
            // スリープを止められなくても記録は続ける
        }
    }

    internal string StatusMessage => StatusText.Text ?? "";

    // ───────── 言語 ─────────

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

    private void LanguageCheck_Click(object? sender, RoutedEventArgs e)
    {
        if (!_languages.Any(l => l.IsSelected) && (sender as CheckBox)?.DataContext is LanguageChoice c)
        {
            c.IsSelected = true;
            SetStatus("言語は少なくとも 1 つ選んでください");
            return;
        }
        UpdateLanguageLabel();
        var langs = SpeechLanguages;
        _settings.MinutesLanguages = langs;
        _settings.Save();
        if (langs.Any(l => l != "ja") && !TranscriptionService.IsMultilingualInstalled)
        {
            SetStatus("日本語以外の音声認識モデルが未セットアップです。設定 → 情報 → 「セットアップを実行」で入ります (約 1.6GB)。それまでは日本語として文字にします");
            return;
        }
        _service.SetLanguages(langs);
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
        else if (state == "missing") SetStatus("英語などの音声認識モデルがありません。設定 → 情報 → 「セットアップを実行」で入ります。いまは日本語として文字にしています");
        else if (state == "ready" && !_service.IsRecording && !_service.IsTranscribingFile) SetStatus("英語などの音声認識モデルの準備ができました");
    }

    // ───────── ウィンドウの選択 ─────────

    private void RefreshWindows()
    {
        var selected = WindowBox.SelectedItem as WindowInfo;
        var items = new List<WindowInfo> { WindowInfo.AllAudio };
        items.AddRange(MacServices.GetWindows());
        WindowBox.ItemsSource = items;
        WindowBox.SelectedItem = (selected is { IsAll: true } ? WindowInfo.AllAudio : null)
                                 ?? items.FirstOrDefault(w => !w.IsAll && w.ProcessId == selected?.ProcessId)
                                 ?? items.FirstOrDefault(w => w.IsPlaying)
                                 ?? WindowInfo.AllAudio;
    }

    private void CheckSilence()
    {
        if (!_service.IsRecording || !_captureWindowAudio || _silenceHintShown) return;
        if (DateTime.Now - _lastWindowSound < TimeSpan.FromSeconds(8)) return;
        _silenceHintShown = true;
        string? other = null;
        try
        {
            var current = (WindowBox.SelectedItem as WindowInfo)?.ProcessId;
            var playing = MacServices.GetWindows().Where(w => w.IsPlaying && w.ProcessId != current).Select(w => w.ProcessName).Distinct().Take(2).ToList();
            if (playing.Count > 0) other = string.Join("・", playing);
        }
        catch
        {
            // 調べられなくても案内は出す
        }
        ShowNotice(other != null
            ? $"選んだウィンドウから音が来ていません。いま音を出しているのは {other} です。停止して、🔊 の付いたウィンドウか「すべての音」を選び直してください。"
            : "選んだウィンドウから音が来ていません。停止して、🔊 の付いたウィンドウか「すべての音」を選び直してください (システム設定 → プライバシーとセキュリティ → 画面収録とシステムオーディオ録音 で GetText の許可も確かめてください)。");
    }

    private void HideOcr_Click(object? sender, RoutedEventArgs e)
    {
        _settings.HideOcrDuringMinutes = HideOcrCheck.IsChecked == true;
        _settings.Save();
        _host.SetOcrHidden(_settings.HideOcrDuringMinutes);
    }

    private void RefreshWindows_Click(object? sender, RoutedEventArgs e) => RefreshWindows();

    // ───────── 記録 ─────────

    private async void Record_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _service.IsTranscribingFile) return;
        if (_savingProject && !_service.IsRecording)
        {
            SetStatus("プロジェクトの保存・読み込みの途中です。終わってから記録を始めてください");
            return;
        }
        _busy = true;
        RecordButton.IsEnabled = false;
        FileButton.IsEnabled = false;
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
            SetStatus("ウィンドウを選ぶか、マイクを含めてください");
            return;
        }
        if (_doc.FromFile && _doc.Entries.Count > 0 &&
            !await Dialogs.ConfirmAsync(this, "今の議事録 (ファイルから作ったもの) を閉じて、新しく記録を始めますか？\n(今の内容は自動保存されています)"))
            return;
        if (!await EnsurePermissionsAsync(window != null, mic)) return;
        SetStatus("記録を準備しています…");
        ShowLoadingIfBusy("準備ができしだい、記録を始めます。");
        try
        {
            if (_doc.FromFile)
            {
                AutoSave();
                ResetDocument();
            }
            bool newSession = !_doc.Entries.Any(en => !en.IsNote);
            _service.RecordAudioPath = !App.DemoMode && SaveAudioCheck.IsChecked == true ? NewAudioPath(newSession) : null;
            _dropLate = false;
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
            if (_closed) return;
            App.Log("Minutes", ex);
            SetStatus("記録を始められませんでした: " + ex.Message);
            await Dialogs.AlertAsync(this, "記録を始められませんでした。\n\n" + ex.Message);
            return;
        }
        _captureWindowAudio = window != null;
        _lastWindowSound = DateTime.Now;
        _silenceHintShown = false;
        HideNotice(); // 前の「処理が止まりました」などは、記録を始め直せたら消す
        WindowBox.IsEnabled = MicCheck.IsEnabled = FileButton.IsEnabled = SaveAudioCheck.IsEnabled = RecordVideoCheck.IsEnabled = false;
        await StartVideoAsync(window);
        if (!_service.IsRecording || IsStopping || _closed) return; // (録画を始めている間に止められた・閉じられた)
        RecordIcon.Text = "■";
        ShowPartialArea(true, window: window != null, mic: MicCheck.IsChecked == true);
        SetState("recording");
        RecordLabel.Text = "記録を停止";
        _clock.Start();
        var name = window == null ? "" : window.IsAll ? "すべての音" : window.ProcessName;
        SetStatus($"記録中 ・ {name}{(window != null && mic ? " + " : "")}{(mic ? "マイク" : "")} ・ 話し終わってから数秒で文字になります");
    }

    /// <summary>
    /// アプリの音の取り込み (画面収録とシステムオーディオ録音) とマイクの許可を確かめ、無ければ頼む。
    /// 許可は Mac のシステム設定で GetText に与える (一度許可すれば次からは聞かれない)。
    /// </summary>
    private async Task<bool> EnsurePermissionsAsync(bool appAudio, bool mic)
    {
        if (!MacHelper.IsAvailable || App.DemoMode) return true;
        try
        {
            var (screen, micState) = await MacServices.PermissionsAsync();
            if (appAudio && !screen)
            {
                await MacHelper.Instance.RequestAsync("request_screen");
                (screen, _) = await MacServices.PermissionsAsync();
                if (!screen)
                {
                    await Dialogs.AlertAsync(this,
                        "アプリの音を取り込むには、Mac の許可が必要です。\n\n" +
                        "システム設定 → プライバシーとセキュリティ → 「画面収録とシステムオーディオ録音」で GetText をオンにして、GetText を開き直してください。\n" +
                        "(GetText が音を取り込むのは、選んだアプリの音だけです。画面の映像は議事録には使いません)");
                    return false;
                }
            }
            if (mic && micState != "granted")
            {
                var reply = await MacHelper.Instance.RequestAsync("request_mic", timeout: TimeSpan.FromMinutes(5));
                if (reply["granted"]?.GetValue<bool>() != true)
                {
                    await Dialogs.AlertAsync(this,
                        "マイクを使う許可がありません。\n\nシステム設定 → プライバシーとセキュリティ → 「マイク」で GetText をオンにしてください。" +
                        "マイクを使わないときは「マイク (自分の声) も含める」を外してください。");
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            App.Log("Permissions", ex);
            return true; // 確かめられないときは、取り込みのエラーで知らせる
        }
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
        // (残りの音声を文字にする: 最大 30 秒 + 録画を書き終える: 最大 60 秒)
        for (int i = 0; IsStopping && i < 450; i++) await Task.Delay(200);
    }

    /// <summary>終了の前に待つ仕事 (止めている途中・プロジェクトの保存の途中) がある。</summary>
    public bool HasPendingWork => IsStopping || _savingProject || !_videoStopping.IsCompleted;

    /// <summary>終了の前に待つ仕事が終わるまで待つ。</summary>
    public async Task WaitPendingAsync()
    {
        _closingWindow = true; // (GetText を終えるので、字幕つきの動画は作らない)
        await WaitStoppedAsync();
        await Task.WhenAny(_videoStopping, Task.Delay(TimeSpan.FromSeconds(70))); // (途中で止まった録画を書き終えて議事録に入れる)
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
        WindowLevel.Value = MicLevel.Value = 0;
        ShowPartialArea(false);
        OnPartial("mic", 0, "");
        WindowBox.IsEnabled = MicCheck.IsEnabled = FileButton.IsEnabled = SaveAudioCheck.IsEnabled = RecordVideoCheck.IsEnabled = true;
        RecordIcon.Text = "●";
        SetState("ready", "停止中");
        RecordLabel.Text = "記録を再開";
        AutoSave();
        SetStatus($"停止しました ・ {VisibleCount()} 発言" + (_autoSavePath != null ? $" ・ 自動保存: {_autoSavePath}" : ""));
    }

    // ───────── ファイルの文字起こし ─────────

    private bool _fileCancelRequested;

    private async void File_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy && FileLabel.Text == "中止" && !_service.IsTranscribingFile)
        {
            _fileCancelRequested = true;
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
        var path = await Dialogs.OpenFileAsync(this, "文字起こしする動画・音声ファイル", Dialogs.MediaKinds);
        if (path == null) return;
        await TranscribeFileAsync(path);
    }

    /// <summary>ファイルを文字起こしして議事録にする (動作確認からも呼ぶ)。</summary>
    internal async Task TranscribeFileAsync(string path)
    {
        if (_doc.Entries.Count > 0 &&
            !await Dialogs.ConfirmAsync(this, "今の議事録を閉じて、選んだファイルから新しい議事録を作りますか？\n(今の内容は自動保存されています)"))
            return;
        AutoSave();
        ResetDocument();
        _doc.FromFile = true;
        _doc.Source = Path.GetFileName(path);
        _doc.SourcePath = path;

        _busy = true;
        _fileCancelRequested = false;
        RecordButton.IsVisible = false;
        WindowBox.IsEnabled = MicCheck.IsEnabled = false;
        SetState("file");
        FileLabel.Text = "中止";
        FileProgress.Value = 0;
        FileProgress.IsVisible = true;
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
            _service.ResetSession();
            _dropLate = false;
            _service.SetSpeakerSensitivity(ThresholdSlider.Value);
            _service.SetContextCorrection(CorrectCheck.IsChecked == true);
            _service.SetLanguages(SpeechLanguages);
            SendMinutesOptions();
            var (duration, cancelled) = await _service.TranscribeFileAsync(path);
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
            SetStatus(ex.Message.StartsWith("ファイル") ? ex.Message : "ファイルを文字起こしできませんでした: " + ex.Message);
        }
        finally
        {
            _busy = false;
            RecordButton.IsVisible = true;
            WindowBox.IsEnabled = MicCheck.IsEnabled = FileButton.IsEnabled = true;
            SetState(_loading ? "loading" : "ready");
            FileLabel.Text = "ファイルから…";
            FileProgress.IsVisible = false;
        }
    }

    private void OnFileProgress(double done, double total)
    {
        if (!_service.IsTranscribingFile || total <= 0) return;
        FileProgress.Value = done / total;
        ElapsedText.Text = $"{TimeSpan.FromSeconds(done):h\\:mm\\:ss} / {TimeSpan.FromSeconds(total):h\\:mm\\:ss}";
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
            double speed = processed / seconds;
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
        if (_dropLate) return;
        _doc.Add(segment.SessionStart ?? _service.SessionStart, segment);
        if (_voiceNames.TryGetValue(segment.Speaker, out var voiceName)) ApplyVoiceName(segment.Speaker, voiceName);
        ScrollToLatest();
        RestartAutoSave();
    }

    /// <summary>
    /// 記録中は暫定の文字の欄を出しておく (取り込む音ごとに 1 行。高さを変えないので、一覧の最新の発言が隠れない)。
    /// </summary>
    private void ShowPartialArea(bool show, bool window = false, bool mic = false)
    {
        WinPartial.Text = MicPartial.Text = WinPartialName.Text = MicPartialName.Text = "";
        WinPartialRow.IsVisible = show && window;
        MicPartialRow.IsVisible = show && mic;
        PartialPanel.IsVisible = show && (window || mic);
        ScrollToLatest();
    }

    /// <summary>話している途中の暫定の文字を下の欄に出す (長いときは終わりの方を見せる。確定したら消える)。</summary>
    private void OnPartial(string source, int speaker, string text)
    {
        var (row, nameBlock, line) = source == "mic" ? (MicPartialRow, MicPartialName, MicPartial) : (WinPartialRow, WinPartialName, WinPartial);
        if (text.Length == 0)
        {
            nameBlock.Text = line.Text = "";
            return;
        }
        var name = _doc.Speakers.FirstOrDefault(s => s.Id == speaker)?.Name
                   ?? (speaker == 0 ? MinutesSpeaker.DefaultName(0) : "話者");
        nameBlock.Text = name + ": ";
        line.Text = text + "…";
        if (!PartialPanel.IsVisible || !row.IsVisible)
        {
            row.IsVisible = true;
            PartialPanel.IsVisible = true;
            ScrollToLatest();
        }
    }

    private void OnRevised(TranscriptRevision revision)
    {
        if (_dropLate) return;
        if (_doc.Revise(revision) == null) return;
        ScrollToLatest();
        UpdateFillerCount();
        RestartAutoSave();
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
        var tip = _llmState switch
        {
            "gpu" or "cpu" => "前後の発言から、聞き間違えた語を AI (Qwen3-4B) が直します。直した発言には印が付きます",
            "unavailable" => "AI のモデルを読み込めませんでした",
            "off" => "設定 → 情報 → セットアップを実行 で入ります",
            _ => null,
        };
        if (tip != null) ToolTip.SetTip(CorrectCheck, tip);
    }

    private void Correct_Click(object? sender, RoutedEventArgs e)
    {
        _settings.MinutesCorrect = CorrectCheck.IsChecked == true; // 手で選んだら、速さを変えてもそのまま
        _settings.Save();
        _service.SetContextCorrection(CorrectCheck.IsChecked == true);
    }

    // ───────── 速さ・状態の印 ─────────

    public static readonly Option<string>[] SpeedOptions =
    [
        new("自動 (Mac では軽さ優先)", "auto"),
        new("軽さ優先 — 記録中も遅れない", "light"),
        new("正確さ優先 — 重い (Apple シリコン向け)", "accurate"),
    ];

    private bool NeedsMultilingual =>
        SpeechLanguages.Any(l => l != "ja") && !(LightNow && SpeechLanguages.Contains("ja"));

    private bool LightNow => _service.IsReady ? _service.Speed == "light"
        : _settings.MinutesSpeed != "accurate" && TranscriptionService.IsFastInstalled;

    private void SpeedBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
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
        _speedFastInstalled = fast;
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

    private bool _speedFastInstalled = TranscriptionService.IsFastInstalled;

    private void UpdateSpeedNote()
    {
        string now = !_service.IsReady ? ""
            : _service.Speed == "light" ? "いまは軽さ優先で動いています (速い音声認識。「、」「。」は話の間 (ま) と言い回しから付けます。「？」は付きません)。"
            : "いまは正確さ優先で動いています。話し終わってから文字になるまで時間がかかることがあります。";
        string missing = _speedFastInstalled ? "" : " 軽さ優先の音声認識が未セットアップです (設定 → 情報 → 「セットアップを実行」で入ります。約 160MB)。";
        SpeedNote.Text = (now + missing).Trim();
    }

    /// <summary>記録ボタンの下の、いまの状態の印。</summary>
    private void SetState(string state, string? text = null)
    {
        if (!App.DemoMode) Dispatcher.UIThread.Post(_host.MinutesStateChanged); // 機能を選ぶ画面の「記録中」
        // 状態の印: 点の色と薄い背景 (デザイントークンの状態の色)。文字でも状態を書く (色だけに頼らない)
        var (dot, fill, label) = state switch
        {
            "loading" => ((Func<Palette, uint>)(p => p.Warning), (Func<Palette, uint>)(p => p.WarningSubtle), "準備中"),
            "ready" => (p => p.Success, p => p.SuccessSubtle, "準備完了"),
            "recording" => (p => p.Critical, p => p.CriticalSubtle, "記録中"),
            "file" => (p => p.AccentText, p => p.AccentSubtle, "文字起こし中"),
            "error" => (p => p.Critical, p => p.CriticalSubtle, "止まりました"),
            _ => (p => p.TextTertiary, p => p.SurfaceSecondary, "未セットアップ"),
        };
        StateDot.Fill = MacTheme.BrushOf(dot);
        StateBadge.Background = MacTheme.BrushOf(fill);
        StateText.Text = text ?? label;
        _state = state;
        UpdateTitle();
        KeepAwake(state is "recording" or "file");
    }

    private string _state = "loading";

    // 記録中はタイトルにも出す (ほかの窓の後ろにあっても、Dock や Mission Control で分かるように)
    private void UpdateTitle() => Title = _state switch
    {
        "recording" => $"● 記録中 {ElapsedText.Text} — GetText 議事録",
        "file" => "文字起こし中 — GetText 議事録",
        _ => "GetText — 議事録",
    };

    /// <summary>自動スクロールがオンなら一番下 (最新) までスクロールする。発言を書き直している間は動かさない。</summary>
    private void ScrollToLatest()
    {
        if (AutoScrollCheck.IsChecked != true || Searching) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (FocusManager?.GetFocusedElement() is TextBox box && box.FindAncestorOfType<ListBox>() == EntryList) return;
            HookEntryScroll();
            if (_view.Count > 0) EntryList.ScrollIntoView(_view[^1]);
        }, DispatcherPriority.Background);
    }

    private ScrollViewer? _entryScroll;
    private bool _atBottom = true;

    /// <summary>
    /// 一覧の高さが変わったとき (下の欄が出た・窓の大きさを変えた・訳が付いた) も、最新を見ていたなら最新を見せ続ける。
    /// (テーマを変えると一覧の作りが作り直されるので、毎回探して付け直す)
    /// </summary>
    private void HookEntryScroll()
    {
        var current = EntryList.FindDescendantOfType<ScrollViewer>();
        if (current == null || ReferenceEquals(current, _entryScroll)) return;
        _entryScroll = current;
        current.ScrollChanged += (_, e) =>
        {
            if (!ReferenceEquals(current, _entryScroll)) return;
            if (e.ExtentDelta.Y == 0 && e.ViewportDelta.Y == 0)
            {
                _atBottom = current.Offset.Y + current.Viewport.Height >= current.Extent.Height - 4; // 自分でスクロールした
                return;
            }
            if (_atBottom && AutoScrollCheck.IsChecked == true && !Searching
                && !(FocusManager?.GetFocusedElement() is TextBox box && box.FindAncestorOfType<ListBox>() == EntryList))
                current.ScrollToEnd();
        };
    }

    private void AutoScrollCheck_Click(object? sender, RoutedEventArgs e)
    {
        _settings.MinutesAutoScroll = AutoScrollCheck.IsChecked == true;
        _settings.Save();
        _host.MinutesDisplayChanged();
        ScrollToLatest();
    }

    /// <summary>アプリの終了時: 待たずに保存して止める。</summary>
    public void ShutdownNow()
    {
        if (_closed) return;
        _closed = true;
        LeaveTextBox();
        _service.CancelSummary();
        StopPlayback();
        if (_service.IsRecording || IsStopping)
        {
            try
            {
                if (_service.IsRecording) _service.StopCapture();
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

    // ───────── 話者の名前 ─────────
    // Enter ですぐ確定してすべての発言に反映、Esc で入力前に戻す

    private string? _nameAtFocus;

    private void SpeakerName_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not MinutesSpeaker speaker) return;
        if (e.Key == Key.Enter)
        {
            CommitSpeakerName(box, speaker);
            if (!MergeIfSameName(speaker))
            {
                SetStatus($"話者の名前を「{speaker.Name}」にしました");
                RememberVoice(speaker);
            }
            _nameAtFocus = speaker.Name;
            RestartAutoSave();
            LeaveTextBox(box);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            box.Text = speaker.Name;
            LeaveTextBox(box);
            e.Handled = true;
        }
    }

    // 欄の文字を話者の名前にする (空欄なら既定の名前に戻す)
    private static void CommitSpeakerName(TextBox box, MinutesSpeaker speaker)
    {
        var text = (box.Text ?? "").Trim();
        speaker.Name = text.Length > 0 ? text : speaker.AutoName;
        box.Text = speaker.Name;
    }

    private void SpeakerName_GotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is not TextBox box) return;
        _nameAtFocus = box.Text;
        box.SelectAll();
    }

    // Enter を押さずに別の所をクリックしたときも、名前を確定して同じ名前の話者をまとめる
    private void SpeakerName_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not MinutesSpeaker speaker) return;
        box.ClearSelection();
        if (box.Text == _nameAtFocus || box.Text == speaker.Name) return;
        var before = _nameAtFocus; // (次の欄に移ると書き換わるので、この欄の元の名前を取っておく)
        CommitSpeakerName(box, speaker);
        Dispatcher.UIThread.Post(() =>
        {
            if (!MergeIfSameName(speaker, before)) RememberVoice(speaker);
            RestartAutoSave();
        }, DispatcherPriority.Background);
    }

    private bool MergeIfSameName(MinutesSpeaker? speaker, string? nameBefore = null)
    {
        nameBefore ??= _nameAtFocus;
        if (speaker == null || !_doc.Speakers.Contains(speaker) || _doc.SameNameAs(speaker) is not { } other) return false;
        if (speaker.Id == 0 || other.Id == 0)
        {
            var name = speaker.Name;
            speaker.Name = nameBefore is { Length: > 0 } before && before != name ? before : speaker.AutoName;
            SetStatus($"「{name}」は「自分」(マイク) と同じ名前なので付けられません。別の名前にしてください");
            return true;
        }
        _ = ConfirmMergeAsync(speaker, other, nameBefore);
        return true;
    }

    // まとめると元に戻せないので確かめてからまとめる (別の人にうっかり同じ名前を付けたときに、発言の話者が混ざらないように)
    private async Task ConfirmMergeAsync(MinutesSpeaker speaker, MinutesSpeaker other, string? before)
    {
        if (!await Dialogs.ConfirmAsync(this,
                $"「{other.Name}」という名前の話者がもういます。2 人を 1 人にまとめますか？\n" +
                "同じ人が 2 人に分かれていたときはまとめます。まとめると元に戻せません。別の人なら、別の名前を付けてください。", ok: "まとめる"))
        {
            speaker.Name = before is { Length: > 0 } && before != other.Name ? before : speaker.AutoName;
            SetStatus("まとめませんでした。別の名前を付けてください");
            return;
        }
        if (!_doc.Speakers.Contains(speaker) || !_doc.Speakers.Contains(other)) return;
        var (kept, removed) = other.Id < speaker.Id ? _doc.MergeSpeakers(speaker, other) : _doc.MergeSpeakers(other, speaker);
        if (removed.Id < 1000 && kept.Id < 1000) _service.MergeSpeakers(removed.Id, kept.Id);
        RefreshTalkTimes();
        AutoSave();
        RememberVoice(kept);
        SetStatus($"同じ名前の話者を「{kept.Name}」の 1 人にまとめました");
    }

    private void Threshold_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (IsLoaded) _service.SetSpeakerSensitivity(e.NewValue);
    }

    // ───────── 発言の削除・話者の変更 (右クリック) ─────────

    private List<MinutesEntry> TargetsOf(MinutesEntry entry)
    {
        var selected = EntryList.SelectedItems?.Cast<MinutesEntry>().ToList() ?? [];
        return selected.Count > 1 && selected.Contains(entry) ? selected : [entry];
    }

    private void EntryList_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is not MinutesEntry entry || e.Source is not Control source) return;
        var targets = TargetsOf(entry);
        var box = source as TextBox ?? source.FindAncestorOfType<TextBox>();
        var menu = BuildEntryMenu(targets, box);
        menu.Open(source);
        e.Handled = true;
    }

    /// <summary>発言の右クリックのメニュー。</summary>
    private ContextMenu BuildEntryMenu(List<MinutesEntry> targets, TextBox? box)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action, bool enabled = true, string? gesture = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (gesture != null) item.InputGesture = KeyGesture.Parse(gesture);
            item.Click += (_, _) => action();
            return item;
        }
        menu.Items.Add(Item(targets.Count > 1 ? $"選んだ {targets.Count} 件の発言を削除" : "この発言を削除", () => DeleteEntries(targets)));
        menu.Items.Add(Item("削除を元に戻す", UndoDelete, _deleted.Count > 0));
        var speakers = new MenuItem { Header = "話者を変更", IsEnabled = targets.Count > 0 && !targets.Any(t => t.IsNote) };
        foreach (var speaker in _doc.Speakers)
        {
            var target = speaker;
            var choice = new MenuItem
            {
                Header = (targets.All(t => t.Speaker == speaker) ? "✓ " : "　 ") + speaker.Name,
                Icon = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 10, Height = 10,
                    Fill = HexBrushConverter.Instance.Convert(speaker.Color, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture) as IBrush,
                },
            };
            choice.Click += (_, _) => ChangeSpeaker(targets, target);
            speakers.Items.Add(choice);
        }
        speakers.Items.Add(new Separator());
        speakers.Items.Add(Item("新しい話者", () => ChangeSpeaker(targets, _doc.NewSpeaker())));
        menu.Items.Add(speakers);
        menu.Items.Add(Item(targets.All(t => t.IsMarked) ? "★ を外す" : "重要な発言にする (★)", () => ToggleMark(targets)));
        var first = targets[0];
        menu.Items.Add(Item(_playing != null && targets.Contains(_playing) ? "再生を止める" : "この発言の音声を聞く",
            () => PlayEntry(first), !first.IsNote && (_doc.FromFile || _doc.AudioAt(first) != null)));
        menu.Items.Add(Item("発言をコピー", () => CopyEntries(targets)));
        if (box != null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("切り取り", box.Cut, box.SelectionStart != box.SelectionEnd));
            menu.Items.Add(Item("選択した文字をコピー", box.Copy, box.SelectionStart != box.SelectionEnd));
            menu.Items.Add(Item("貼り付け", box.Paste));
        }
        return menu;
    }

    private async void CopyEntries(List<MinutesEntry> targets)
    {
        if (targets.Count == 0) return;
        var options = ExportOptions();
        var text = string.Join("\n", targets.OrderBy(t => _doc.Entries.IndexOf(t)).Select(t =>
            $"{(options.IncludeTime ? $"[{t.TimeText}] " : "")}{t.Speaker.Name}: {t.Text}"
            + (options.IncludeTranslation && t.NeedsTranslation && !string.IsNullOrEmpty(t.Translation)
                ? $"\n    (訳) {t.Translation}" : "")));
        SetStatus(await Dialogs.CopyAsync(this, text)
            ? targets.Count > 1 ? $"{targets.Count} 件の発言をコピーしました" : "発言をコピーしました"
            : "クリップボードにアクセスできませんでした");
    }

    private void DeleteEntries(List<MinutesEntry> targets)
    {
        if (targets.Count == 0) return;
        _deleted.Push(_doc.Remove(targets));
        AutoSave();
        SetStatus($"{targets.Count} 件の発言を削除しました (⌘Z または右クリックで元に戻せます)");
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

    private KeyModifiers CommandKey => TopLevel.GetTopLevel(this)?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    // 本文を書いている途中でなければ、Delete で選んだ発言を削除、⌘Z で削除を元に戻す。Enter / Esc で選択を外す
    private void EntryList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        var selected = EntryList.SelectedItems?.Cast<MinutesEntry>().ToList() ?? [];
        if (e.Key is Key.Enter or Key.Escape && selected.Count > 0)
        {
            EntryList.SelectedItems?.Clear();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Delete or Key.Back)
        {
            DeleteEntries(selected);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && e.KeyModifiers == CommandKey)
        {
            UndoDelete();
            e.Handled = true;
        }
    }

    // ───────── 文字の大きさ・位置 ─────────

    public void ApplyFontSize()
    {
        double size = Math.Clamp(_settings.MinutesFontSize, 9, 40);
        Resources["EntryFontSize"] = size;
        Resources["EntrySmallFontSize"] = Math.Max(9, size - 1);
        Resources["EntryTimeWidth"] = Math.Round(size * 5.0);
    }

    // 一覧の上で ⌘ + ホイールで文字の大きさを変える
    private void EntryList_PointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != CommandKey) return;
        ChangeFontSize(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void ChangeFontSize(double delta, bool reset = false)
    {
        _settings.MinutesFontSize = reset ? 14 : Math.Clamp(_settings.MinutesFontSize + delta, 9, 40);
        _settings.Save();
        ApplyFontSize();
        _host.MinutesDisplayChanged();
        SetStatus($"発言の文字の大きさ: {_settings.MinutesFontSize:0} pt (⌘0 で元に戻す)");
    }

    private void RestorePlacement()
    {
        // 初めて開くときは、上の欄が折り返しても発言が十分に見える大きさにする
        Width = Math.Max(MinWidth, _settings.MinutesPixel == null ? Math.Max(940, _settings.MinutesWidth) : _settings.MinutesWidth);
        Height = Math.Max(MinHeight, _settings.MinutesPixel == null ? Math.Max(720, _settings.MinutesHeight) : _settings.MinutesHeight);
        if (_settings.MinutesPixel is [var x, var y])
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
            Opened += (_, _) => WindowPlacement.EnsureVisible(this);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    internal void StorePlacement()
    {
        if (WindowState != WindowState.Normal) return;
        _settings.MinutesWidth = Width;
        _settings.MinutesHeight = Height;
        _settings.MinutesPixel = [Position.X, Position.Y];
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
        var q = SearchBox?.Text?.Trim();
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

    // ⌘F で検索、⌘S で保存、⌘O で開く、⌘M でメモ、⌘B で ★、⌘ + (+ / - / 0) で文字の大きさ
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (AppCommands.HandleKey(this, e, CommandContext.Meeting)) return; // ⌘K: コマンドの一覧
        if (e.Key == Key.Escape && _playing != null && FocusManager?.GetFocusedElement() is not TextBox)
        {
            StopPlayback();
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != CommandKey) return;
        switch (e.Key)
        {
            case Key.R:
                // ⌘R で記録の開始・停止
                if (RecordButton.IsEnabled && RecordButton.IsVisible) Record_Click(RecordButton, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.M:
                MemoBox.Focus();
                e.Handled = true;
                break;
            case Key.B:
                var targets = EntryList.SelectedItems?.Cast<MinutesEntry>().ToList() ?? [];
                if (targets.Count == 0 && (FocusManager?.GetFocusedElement() as Control)?.DataContext is MinutesEntry focused) targets = [focused];
                ToggleMark(targets);
                e.Handled = true;
                break;
            case Key.S:
                Save_Click(sender, e);
                e.Handled = true;
                break;
            case Key.O:
                LeaveTextBox();
                ChooseAndOpen();
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

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_view == null) return;
        _view.Refresh();
        SearchCount.Text = Searching ? $"{_view.Count} 件" : "";
        if (!Searching) ScrollToLatest();
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            EntryList.Focus();
            e.Handled = true;
        }
    }

    // ───────── 表示 (時刻・相づち) ─────────

    /// <summary>設定を議事録のページで開く。</summary>
    private void Settings_Click(object? sender, RoutedEventArgs e) => _host.OpenSettings(SettingsPage.Meetings, this);

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

    private void TimeCheck_Click(object? sender, RoutedEventArgs e)
    {
        _settings.MinutesShowTime = TimeCheck.IsChecked == true;
        _settings.Save();
        _host.MinutesDisplayChanged();
        EntryList.Tag = _settings.MinutesShowTime;
        AutoSave();
    }

    private void FillerCheck_Click(object? sender, RoutedEventArgs e)
    {
        _settings.MinutesHideFillers = FillerCheck.IsChecked == true;
        _settings.Save();
        _host.MinutesDisplayChanged();
        _view.Refresh();
        UpdateFillerCount();
        AutoSave();
    }

    private void UpdateFillerCount()
    {
        int fillers = _doc.Entries.Count(en => en.IsFiller);
        FillerCheck.Content = fillers > 0 ? $"相づちを除く ({fillers})" : "相づちを除く";
    }

    // ───────── 見本の画面 (動作確認・画面の画像用) ─────────

    /// <summary>見本の議事録を表示する。scene: "empty" / "loading" / "recording" / "file" / "many" / "summary"。発言や名前は架空のもの。</summary>
    internal void LoadDemo(string scene)
    {
        ResetDocument();
        WindowBox.ItemsSource = new List<WindowInfo>
        {
            WindowInfo.AllAudio,
            new(0, "定例ミーティング", 1, "zoom.us", IsPlaying: true),
            new(0, "チーム会議", 2, "Microsoft Teams"),
            new(0, "講演の動画 - Safari", 3, "Safari"),
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
            LoadingOverlay.IsVisible = true;
            SetStatus("音声認識モデルを読み込んでいます…");
            return;
        }
        LoadingOverlay.IsVisible = false;
        if (scene == "empty")
        {
            SetStatus("準備完了 (CPU) ・ ウィンドウを選んで「記録を開始」を押してください");
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
        if (scene == "many")
            for (int sp = 3; sp <= 9; sp++)
                _doc.Add(start, new TranscriptSegment("win", 30 + sp * 3, 32 + sp * 3, sp, $"見本の発言 {sp}", id++));
        _doc.Speaker(1).Name = "田中";
        _doc.Speaker(2).Name = "Sarah";
        _doc.Entries.First(en => en.Language == "en").Translation = "いいですね。金曜日までにテスト結果を共有してもらえますか？";
        _doc.Entries.Last(en => en.Text.StartsWith("次回")).ApplyRevision("次回の会議で、読み取りの精度の検証結果を確認しましょう。", markCorrected: true);
        _doc.Entries.First(en => en.Text.StartsWith("承知しました")).IsMarked = true;
        _doc.AddNote(start.AddSeconds(26), "テスト結果の共有先は開発チームのフォルダ");
        UpdateFillerCount();
        _doc.UpdateTalkTimes();
        WindowLevel.Value = 0.55;
        MicLevel.Value = 0.2;
        if (scene == "recording")
        {
            RecordIcon.Text = "■";
            RecordLabel.Text = "記録を停止";
            ElapsedText.Text = "00:00:41";
            WindowBox.IsEnabled = MicCheck.IsEnabled = FileButton.IsEnabled = false;
            ShowPartialArea(true, window: true, mic: true);
            OnPartial("win", 2, "I would also like to talk about the budget for");
            SetStatus("記録中 ・ zoom.us + マイク ・ 話し終わってから数秒で文字になります");
        }
        else if (scene == "file")
        {
            RecordButton.IsVisible = false;
            WindowBox.IsEnabled = MicCheck.IsEnabled = false;
            FileLabel.Text = "中止";
            FileProgress.IsVisible = true;
            FileProgress.Value = 0.42;
            ElapsedText.Text = "0:12:36 / 0:30:00";
            SetStatus("ファイルを文字起こししています 42% ・ 残り約 4 分");
        }
        else if (scene == "summary")
        {
            ShowSummary("### 概要\n定例会議で開発の進捗を確認した。画面の文字を読み取る機能は完成し、テスト中である。\n\n" +
                        "### 決定事項\n- 次回の会議で読み取りの精度の検証結果を確認する\n\n" +
                        "### やること\n- テスト結果を共有する (担当: 自分、期限: 金曜日)\n\n" +
                        "### 主な論点\n- 読み取りの精度の検証方法", "Mac 内の AI が 0:42 で作成 ・ 内容は確かめてから使ってください");
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
                if (e.PropertyName == nameof(MinutesEntry.Text) && entry.UserEdited) RestartAutoSave();
                if (e.PropertyName == nameof(MinutesEntry.IsMarked))
                {
                    UpdateMarkedCount();
                    RestartAutoSave();
                    return;
                }
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
            if (!_translateErrorShown) SetStatus("日本語訳には Mac 内の翻訳モデルが必要です (設定 → 情報 → セットアップを実行)");
            _translateErrorShown = true;
            return;
        }
        if (!_translateQueue.Contains(entry)) _translateQueue.Add(entry);
        PumpTranslations();
    }

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
                    if (batch[i].Text == texts[i] && _doc.Entries.Contains(batch[i])) batch[i].Translation = results[i];
                ScrollToLatest();
                RestartAutoSave();
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

    private List<string> ContextBefore(MinutesEntry entry)
    {
        int index = _doc.Entries.IndexOf(entry);
        return _doc.Entries.Take(Math.Max(0, index)).Where(e => !e.IsNote).TakeLast(4)
            .Select(e => $"{e.Speaker.Name}: {e.Text}").ToList();
    }

    private void TranslateCheck_Click(object? sender, RoutedEventArgs e)
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
            // 一時ファイルに書いてから入れ替える (書いている途中で落ちても、前の保存が壊れないように)
            AtomicFile.WriteAllText(_autoSavePath, _doc.ToMarkdown(ExportOptions()));
            AtomicFile.WriteAllText(Path.ChangeExtension(_autoSavePath, ".json"), _doc.ToFile().ToJson());
            if (_autoSaveFailed)
            {
                _autoSaveFailed = false;
                HideNotice();
            }
        }
        catch (Exception ex)
        {
            if (!_autoSaveFailed)
                ShowNotice("自動保存できませんでした (" + ex.Message + ")。ディスクの空きを確かめ、「保存…」で別の場所にも保存してください", "error");
            _autoSaveFailed = true;
        }
    }

    private bool _autoSaveFailed;

    private static string SafeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars().Concat([':', '/'])) name = name.Replace(c, '_');
        return name.Length > 40 ? name[..40] : name;
    }

    private static readonly Dialogs.FileKind[] SaveKinds =
    [
        new("Word 文書", "docx"), new("GetText の議事録プロジェクト (音声も入れて 1 つのファイルに)", "gettext"),
        new("Markdown", "md"), new("テキスト", "txt"), new("GetText の議事録データ (音声なし。あとで開いて直せる)", "json"),
        new("字幕 SRT (動画プレーヤーで表示できる)", "srt"), new("字幕 WebVTT", "vtt"),
        new("字幕つきの動画 (記録しながら録画した会議の動画に、今の発言を字幕にして焼き込む)", "mp4"),
    ];

    /// <summary>プロジェクト (.gettext) を開いた・保存したとき、その場所 (次に保存するときの初めの名前に使う)。</summary>
    private string? _openedProject;
    private bool _savingProject;

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        LeaveTextBox();
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
        var name = _openedProject != null ? Path.GetFileName(_openedProject)
            : (_doc.FromFile
                ? $"議事録_{SafeName(Path.GetFileNameWithoutExtension(_doc.Source))}"
                : $"議事録_{_doc.StartedAt ?? DateTime.Now:yyyyMMdd_HHmm}") + ".docx";
        var path = await Dialogs.SaveFileAsync(this, "議事録の保存", name, SaveKinds);
        if (path == null) return;
        await SaveToAsync(path);
    }

    /// <summary>拡張子に合った形式で保存する。</summary>
    internal async Task SaveToAsync(string path)
    {
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == MinutesProject.Extension)
            {
                await SaveProjectAsync(path); // 入れた音声の数などを知らせる
                return;
            }
            if (extension == ".mp4")
            {
                if (_doc.VideoParts.Count == 0)
                {
                    ShowNotice("この議事録には録画がありません。「⚙ 詳細」→「会議の画面も録画する」をオンにして記録すると、字幕つきの動画を作れます。");
                    return;
                }
                if (_doc.VideoParts.Any(p => SamePath(p.Path, path)))
                {
                    ShowNotice("元の録画には上書きできません。別の名前を付けて保存してください。");
                    return;
                }
                _ = BurnSubtitlesAsync([.. _doc.VideoParts], path);
                return;
            }
            if (extension == ".docx")
                DocxWriter.Save(path, _doc.ToMarkdown(ExportOptions()));
            else
                AtomicFile.WriteAllText(path, extension switch
                {
                    ".txt" => _doc.ToPlainText(ExportOptions()),
                    ".json" => _doc.ToFile().ToJson(),
                    ".srt" => _doc.ToSubtitles(vtt: false, ExportOptions()),
                    ".vtt" => _doc.ToSubtitles(vtt: true, ExportOptions()),
                    _ => _doc.ToMarkdown(ExportOptions()),
                });
            SetStatus("保存しました: " + path);
        }
        catch (Exception ex)
        {
            SetStatus("保存できませんでした: " + ex.Message);
        }
    }

    /// <summary>
    /// 議事録を、音声も入れたプロジェクト (.gettext) として保存する。記録した音声の変換が終わるのを待ち、
    /// ファイルから作った議事録で元が動画なら、音声だけを取り出して入れる (動画のままだと大きいため)。
    /// </summary>
    private async Task SaveProjectAsync(string path)
    {
        _savingProject = true;
        string? extracted = null;
        int generation = _docGeneration;
        try
        {
            var pending = _encodes.Where(t => !t.IsCompleted).ToArray();
            if (pending.Length > 0)
            {
                SetStatus("記録した音声の変換が終わるのを待っています…");
                await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromMinutes(2)));
            }
            string? source = null;
            if (_doc.FromFile && _doc.SourcePath is { } src && File.Exists(src) && IsVideo(src))
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
            _openedProject = path;
            var size = result.Bytes >= 1 << 20 ? $"{result.Bytes / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, result.Bytes / 1024)} KB";
            var audio = result.AudioFiles > 0 ? $"音声 {result.AudioFiles} 個入り" : "音声なし";
            SetStatus($"プロジェクトを保存しました: {path} ({audio}・{size})");
            if (result.MissingAudio > 0)
                ShowNotice($"プロジェクトを保存しました ({audio}・{size})。見つからない音声 {result.MissingAudio} 個は入れられませんでした", "warning");
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

    // ───────── 発言と話者の欄の境目 ─────────

    private const double SpeakerMinWidth = 170;
    private ColumnDefinition SpeakerColumn => BodyGrid.ColumnDefinitions[2];

    /// <summary>話者の欄を、ドラッグで変えた幅にする (発言の欄が狭くなりすぎないよう、窓の幅の半分まで)。</summary>
    private void FitSpeakerPanel()
    {
        double max = Math.Max(SpeakerMinWidth, Bounds.Width > 0 ? Bounds.Width * 0.5 : 600);
        SpeakerColumn.MaxWidth = max;
        SpeakerColumn.Width = new GridLength(Math.Clamp(_settings.MinutesSpeakerWidth, SpeakerMinWidth, max));
    }

    private void SpeakerSplitter_DragCompleted(object? sender, VectorEventArgs e)
    {
        if (SpeakerColumn.ActualWidth < SpeakerMinWidth - 1) return;
        _settings.MinutesSpeakerWidth = Math.Round(SpeakerColumn.ActualWidth);
        _settings.Save();
        FitSpeakerPanel();
    }

    private void SpeakerSplitter_DoubleTapped(object? sender, TappedEventArgs e)
    {
        _settings.MinutesSpeakerWidth = AppSettings.DefaultSpeakerWidth;
        _settings.Save();
        FitSpeakerPanel();
        e.Handled = true;
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        LeaveTextBox();
        if (_doc.Entries.Count == 0) return;
        SetStatus(await Dialogs.CopyAsync(this, _doc.ToPlainText(ExportOptions())) ? "議事録をコピーしました" : "クリップボードにアクセスできませんでした");
    }

    /// <summary>最近の議事録 (自動保存したもの) と「ファイルを選ぶ…」のメニューを出す。</summary>
    private void Open_Click(object? sender, RoutedEventArgs e)
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
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        var recent = RecentMinutes.List(AutoSaveDir, 8);
        if (recent.Count > 0)
        {
            menu.Items.Add(new MenuItem { Header = "最近の議事録", IsEnabled = false });
            foreach (var (path, label) in recent)
            {
                var item = new MenuItem { Header = label };
                ToolTip.SetTip(item, path);
                item.Click += async (_, _) => await OpenPathAsync(path);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
        }
        var choose = new MenuItem { Header = "ファイルを選ぶ… (⌘O)" };
        choose.Click += (_, _) => ChooseAndOpen();
        menu.Items.Add(choose);
        var folder = new MenuItem { Header = "自動保存のフォルダを開く" };
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(AutoSaveDir);
            try { Process.Start(new ProcessStartInfo("open", [AutoSaveDir]) { UseShellExecute = false }); } catch { }
        };
        menu.Items.Add(folder);
        menu.ShowAt(OpenButton);
    }

    private async void ChooseAndOpen()
    {
        if (_busy || _service.IsRecording || _service.IsTranscribingFile) return;
        var path = await Dialogs.OpenFileAsync(this, "開く議事録",
            [new Dialogs.FileKind("GetText の議事録", "gettext", "json", "md", "txt"), new Dialogs.FileKind("議事録プロジェクト", "gettext"),
             new Dialogs.FileKind("すべてのファイル", "*")], AutoSaveDir);
        if (path == null) return;
        await OpenPathAsync(path);
    }

    internal async Task OpenPathAsync(string path)
    {
        if (_doc.Entries.Count > 0 &&
            !await Dialogs.ConfirmAsync(this, "今の議事録を閉じて、選んだ議事録を開きますか？\n(今の内容は自動保存されています)"))
            return;
        if (_savingProject) return;
        AutoSave();
        try
        {
            bool project = MinutesProject.IsProject(path);
            MinutesFile? data = null;
            if (project)
            {
                StopPlayback();
                SetStatus("プロジェクトを開いています…");
                _savingProject = true; // 開き終わるまで保存・開くを重ねない
                try
                {
                    data = await Task.Run(() => MinutesProject.Open(path, ProjectDir));
                }
                finally
                {
                    _savingProject = false;
                }
                if (_busy || _service.IsRecording || _service.IsTranscribingFile) return; // 開いている間に記録が始まった
            }
            data ??= MinutesFile.Read(path); // 読めないファイルなら、今の議事録を消さずに知らせる
            ResetDocument();
            _doc.Load(data);
            _openedProject = project ? path : null;
            if (_doc.Summary is { } summary) ShowSummary(summary, "保存されていた要約です");
            foreach (var entry in _doc.Entries) QueueTranslation(entry);
            var md = project || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "" :
                " (Markdown / テキストから読んだので、同じ話者が続く発言は 1 つにまとまっています)";
            SetStatus($"開きました: {Path.GetFileName(path)} ・ {VisibleCount()} 発言 ・ 話者 {_doc.Speakers.Count} 人{md}。" +
                      "名前の書き換え・同じ名前でまとめる・右クリックで話者の変更ができます");
        }
        catch (Exception ex)
        {
            App.Log("MinutesOpen", ex);
            SetStatus("開けませんでした: " + ex.Message);
        }
    }

    private async void New_Click(object? sender, RoutedEventArgs e)
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
            !await Dialogs.ConfirmAsync(this, "今の議事録を閉じて新しく始めますか？\n(内容は自動保存されています)", "GetText"))
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

    internal void ResetDocument()
    {
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
        _playerPath = null;
        MarkedOnlyToggle.IsChecked = false;
        SearchBox.Text = "";
        ElapsedText.Text = "00:00:00";
        RecordLabel.Text = "記録を開始";
        if (_state == "ready" && !_service.IsRecording && !_service.IsTranscribingFile) SetState("ready"); // 「停止中」を残さない
        UpdateCorrectionStatus();
    }

    private bool _closeConfirmed;
    private bool _closeBusy; // 確かめている途中・止めている途中 (もう一度閉じようとしても重ねて処理しない)
    private bool _closing;   // 閉じる処理に入った (話者の名前をまとめるかを聞かない)

    /// <summary>記録中・ファイルの文字起こし中か (アプリを終える前の確認に使う)。</summary>
    internal bool IsBusy => _service.IsRecording || _service.IsTranscribingFile || _burning;

    /// <summary>記録中・文字起こし中なら、止めてよいかを聞く。止めてよければ true。</summary>
    internal async Task<bool> ConfirmStopAsync(string what)
    {
        if (!IsBusy || App.DemoMode) return true;
        var doing = _service.IsRecording ? "記録中" : _service.IsTranscribingFile ? "ファイルの文字起こし中" : "字幕つきの動画を作っている途中";
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        bool yes = await Dialogs.ConfirmAsync(this, $"{doing}です。止めて、{what}か？\n(ここまでの議事録は自動保存されています)", ok: "止める");
        if (yes)
        {
            _burnQueue.Clear(); // (続けて作る予定だった分も作らない)
            _burnCts?.Cancel(); // 字幕つきの動画は作りかけを消して中止する (録画と字幕のファイルは残る)
        }
        return yes;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeBusy)
        {
            e.Cancel = true;
            return;
        }
        // 記録中・ファイルの文字起こし中なら、止めてよいか確かめる
        if (!_closeConfirmed && !_closed && IsBusy && !App.DemoMode)
        {
            e.Cancel = true;
            _closeBusy = true;
            bool ok = await ConfirmStopAsync("議事録の画面を閉じます");
            _closeBusy = false;
            if (!ok) return;
            _closeConfirmed = true;
            Close();
            return;
        }
        _closing = true;
        SavePlacement();
        LeaveTextBox();
        if (IsStopping)
        {
            // 残りの音声を文字にしている途中なら、終わってから閉じる (最後の発言と音声を議事録に入れる)
            e.Cancel = true;
            _closeBusy = true;
            _closingWindow = true; // (閉じるので、字幕つきの動画は作らない)
            if (Content is Control stoppingContent) stoppingContent.IsEnabled = false;
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
            if (Content is Control projectContent) projectContent.IsEnabled = false;
            SetStatus("プロジェクトを保存しています… 終わると閉じます");
            while (_savingProject) await Task.Delay(200);
            _closeBusy = false;
            Close();
            return;
        }
        if (_encodes.Any(t => !t.IsCompleted) && !_closingAfterEncode)
        {
            e.Cancel = true;
            _closingAfterEncode = true;
            _closeBusy = true;
            if (Content is Control waitContent) waitContent.IsEnabled = false;
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
            if (Content is Control stopContent) stopContent.IsEnabled = false;
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
        MacHelper.Instance.EventReceived -= OnHelperEvent;
        _service.CancelSummary();
        StopPlayback();
        _loadTimer.Stop();
        AutoSave();
        _service.Dispose();
        DeleteBurnPart(); // 作りかけの字幕つきの動画は残さない
    }

    private bool _closed;
    private bool _dropLate;
    private MinutesEntry? _editingEntry;
    private int _docGeneration;

    // ───────── 要約 ─────────

    private bool _settingSummary;

    private async void Summarize_Click(object? sender, RoutedEventArgs e)
    {
        if (_service.IsSummarizing) return;
        var transcript = _doc.SummaryTranscript();
        if (transcript.Length == 0)
        {
            SetStatus("要約する発言がありません");
            return;
        }
        if (!string.IsNullOrWhiteSpace(_doc.Summary) && SummaryPanel.IsVisible && sender == SummarizeButton)
        {
            if (!SummaryBody.IsVisible) SummaryFold_Click(sender, e);
            if (!await Dialogs.ConfirmAsync(this, "今の発言で要約を作り直しますか？\n(書き直した要約は置き換わります)"))
                return;
        }
        var started = DateTime.Now;
        int generation = _docGeneration;
        LeaveTextBox();
        SummaryPanel.IsVisible = true;
        SummaryBody.IsVisible = true;
        SummaryFoldButton.Content = "▴";
        SummaryCancelButton.IsVisible = true;
        SummaryRedoButton.IsEnabled = SummarizeButton.IsEnabled = false;
        SummaryProgress.IsVisible = true;
        SummaryProgress.IsIndeterminate = true;
        SummaryState.Text = "AI を準備しています…";
        SetStatus("要約を作っています (Mac 内の AI。長い会議では数分かかります)");
        try
        {
            var text = await _service.SummarizeAsync(transcript, (done, total) => Dispatcher.UIThread.Post(() =>
            {
                if (total <= 0)
                {
                    SummaryState.Text = "AI を読み込んでいます…";
                    return;
                }
                SummaryProgress.IsIndeterminate = false;
                SummaryProgress.Value = (double)done / total;
                SummaryState.Text = total == 1 ? "要約を作っています…" : $"要約を作っています… ({done}/{total})";
            }));
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
            ShowSummary(text, $"Mac 内の AI が {took:m\\:ss} で作成 ・ 内容は確かめてから使ってください");
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
            SummaryCancelButton.IsVisible = false;
            SummaryRedoButton.IsEnabled = SummarizeButton.IsEnabled = true;
            SummaryProgress.IsVisible = false;
        }
    }

    private void ShowSummary(string text, string state)
    {
        _settingSummary = true;
        SummaryBox.Text = text.Replace("\r\n", "\n");
        _settingSummary = false;
        SetSummaryEditing(false);
        SummaryState.Text = state;
        SummaryPanel.IsVisible = true;
        SummaryBody.IsVisible = true;
        SummaryFoldButton.Content = "▴";
    }

    private void HideSummary()
    {
        if (_service.IsSummarizing) _service.CancelSummary();
        _settingSummary = true;
        SummaryBox.Text = "";
        _settingSummary = false;
        SetSummaryEditing(false);
        SummaryPanel.IsVisible = false;
    }

    private void SetSummaryEditing(bool editing)
    {
        SummaryBox.IsVisible = editing;
        SummaryViewScroll.IsVisible = !editing;
        SummaryEditButton.Content = editing ? "✓" : "✎";
        if (!editing) RenderSummary(SummaryBox.Text ?? "");
    }

    private void SummaryEdit_Click(object? sender, RoutedEventArgs e)
    {
        bool editing = !SummaryBox.IsVisible;
        if (!SummaryBody.IsVisible) SummaryFold_Click(sender, e);
        SetSummaryEditing(editing);
        if (editing)
        {
            SummaryBox.Focus();
            SummaryBox.CaretIndex = SummaryBox.Text?.Length ?? 0;
        }
    }

    /// <summary>要約の Markdown (### 見出し、- 箇条書き) を画面用に並べる。</summary>
    private void RenderSummary(string markdown)
    {
        SummaryView.Children.Clear();
        double size = Math.Max(9, Math.Clamp(_settings.MinutesFontSize, 9, 40) - 1);
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var heading = new TextBlock
                {
                    Text = line.TrimStart('#').Trim(),
                    FontWeight = FontWeight.SemiBold,
                    FontSize = size,
                    Margin = new Thickness(0, SummaryView.Children.Count == 0 ? 0 : 8, 0, 2),
                    TextWrapping = TextWrapping.Wrap,
                };
                heading.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Text1"));
                SummaryView.Children.Add(heading);
                continue;
            }
            bool bullet = line.StartsWith("- ") || line.StartsWith('・') || line.StartsWith("* ");
            var body = bullet ? line[(line.StartsWith('・') ? 1 : 2)..].Trim() : line;
            var text = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontSize = size };
            text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(body == "なし" ? "Text2" : "Text1"));
            if (!bullet)
            {
                text.Margin = new Thickness(0, 1, 0, 1);
                SummaryView.Children.Add(text);
                continue;
            }
            var row = new DockPanel { Margin = new Thickness(4, 1, 0, 1) };
            var dot = new TextBlock { Text = "•", Margin = new Thickness(0, 0, 8, 0), FontSize = size };
            dot.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AccentText"));
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(text);
            SummaryView.Children.Add(row);
        }
    }

    private void SummaryCancel_Click(object? sender, RoutedEventArgs e)
    {
        _service.CancelSummary();
        SummaryState.Text = "中止しています…";
    }

    // 書き直しを終えて要約の欄から出たら、整えた表示に戻す (要約の欄のボタンに移ったときは戻さない)
    private void SummaryBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var focused = FocusManager?.GetFocusedElement() as Visual;
            if (focused != null && (focused == SummaryEditButton || SummaryPanel.IsVisualAncestorOf(focused))) return;
            if (SummaryBox.IsVisible) SetSummaryEditing(false);
        });
    }

    private async void CopySummary_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SummaryBox.Text)) return;
        SetStatus(await Dialogs.CopyAsync(this, SummaryBox.Text) ? "要約をコピーしました" : "クリップボードにアクセスできませんでした");
    }

    private void SummaryFold_Click(object? sender, RoutedEventArgs e)
    {
        bool open = !SummaryBody.IsVisible;
        SummaryBody.IsVisible = open;
        SummaryFoldButton.Content = open ? "▴" : "▾";
    }

    private void SummaryBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_settingSummary) return;
        _doc.Summary = string.IsNullOrWhiteSpace(SummaryBox.Text) ? null : SummaryBox.Text;
        RestartAutoSave();
    }

    // ───────── 重要な発言 (★) ─────────

    private void ToggleMark(List<MinutesEntry> targets)
    {
        if (targets.Count == 0)
        {
            SetStatus("★ を付ける発言を選んでください (発言をクリックしてから ⌘B)");
            return;
        }
        bool mark = !targets.All(t => t.IsMarked);
        foreach (var t in targets) t.IsMarked = mark;
        SetStatus(mark ? $"{targets.Count} 件の発言に ★ を付けました (保存にも入り、要約で重視されます)" : $"{targets.Count} 件の発言の ★ を外しました");
    }

    private void UpdateMarkedCount()
    {
        int marked = _doc.Entries.Count(en => en.IsMarked);
        MarkedCount.Text = marked > 0 ? $"★ 重要 {marked}" : "★ 重要";
        if (marked == 0 && MarkedOnlyToggle.IsChecked == true)
        {
            MarkedOnlyToggle.IsChecked = false;
            _view.Refresh();
        }
    }

    private void MarkedOnly_Click(object? sender, RoutedEventArgs e)
    {
        if (MarkedOnlyToggle.IsChecked == true && !_doc.Entries.Any(en => en.IsMarked))
        {
            MarkedOnlyToggle.IsChecked = false;
            SetStatus("★ の付いた発言はまだありません。発言の右端の ☆ を押すか、発言を選んで ⌘B で付けられます");
            return;
        }
        _view.Refresh();
        SetStatus(MarkedOnlyToggle.IsChecked == true ? "★ の付いた重要な発言だけを表示しています" : "すべての発言を表示しています");
    }

    // ───────── 発言の音声を聞く ─────────

    private string? _playerPath;
    private MinutesEntry? _playing;
    private DateTime _playStarted;
    private double _playFromSeconds;

    private void Time_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if ((sender as Control)?.DataContext is not MinutesEntry entry || entry.IsNote) return;
        if (!_doc.FromFile && _doc.AudioAt(entry) == null)
        {
            SetStatus("この発言の音声は保存されていません (「音声も保存」をオンにして記録した発言だけ聞けます)");
            return;
        }
        PlayEntry(entry);
        e.Handled = true;
    }

    private void OnHelperEvent(JsonObject json)
    {
        var kind = json["event"]?.GetValue<string>();
        if (kind == "play_end")
            Dispatcher.UIThread.Post(() => StopPlayback(finished: true, sendStop: false));
        // 会議の画面の録画が止まった (この画面が始めた録画のときだけ)
        else if (kind == "record_error" && json["token"]?.GetValue<int>() is { } token)
            Dispatcher.UIThread.Post(async () =>
            {
                if (_videoPath == null || token != _videoToken) return;
                ShowNotice("会議の画面の録画が止まりました (" + (json["message"]?.GetValue<string>() ?? "") + ")。議事録の記録は続けています。");
                await StopVideoRecorderAsync();
            });
        else if (kind == "helper_exited")
            Dispatcher.UIThread.Post(() =>
            {
                if (_videoPath == null) return;
                _videoPath = null; // (補助プログラムが止まったので、録画のファイルは壊れている)
                ShowNotice("補助プログラムが止まったため、会議の画面の録画が止まりました (録画のファイルは再生できない可能性があります)。議事録の記録は続けています。");
            });
    }

    private async void PlayEntry(MinutesEntry entry)
    {
        if (_playing == entry)
        {
            StopPlayback();
            return;
        }
        if (entry.IsNote) return;
        if (!MacHelper.IsAvailable)
        {
            SetStatus("この環境では音声を再生できません");
            return;
        }
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
            var name = _doc.FromFile ? _doc.Source : Path.GetFileName(path);
            var chosen = await Dialogs.OpenFileAsync(this, $"音声のファイル ({name}) の場所を選んでください", Dialogs.MediaKinds);
            if (chosen == null) return;
            if (_doc.FromFile) _doc.SourcePath = chosen;
            else RelocateAudio(path, chosen);
            path = chosen;
            AutoSave();
        }
        StopPlayback();
        // この発言の範囲だけを流す (前後の発言の声は入れない)
        var range = _doc.PlayRange(entry);
        var from = range?.From ?? (audio.Position > TimeSpan.FromSeconds(0.05) ? audio.Position - TimeSpan.FromSeconds(0.05) : TimeSpan.Zero);
        var until = range?.Until ?? audio.Position + entry.Span + TimeSpan.FromSeconds(0.05);
        try
        {
            _playing = entry;
            _playerPath = path;
            _playFromSeconds = from.TotalSeconds;
            _playStarted = DateTime.Now;
            await MacHelper.Instance.RequestAsync("play", new JsonObject
            {
                ["path"] = path, ["from"] = from.TotalSeconds, ["until"] = until.TotalSeconds,
            });
            SetStatus($"▶ {entry.TimeText} の発言を再生しています (もう一度クリック、または Esc で止める)");
        }
        catch (Exception ex)
        {
            _playing = null;
            _playerPath = null;
            SetStatus("再生できませんでした: " + ex.Message);
        }
    }

    private void StopPlayback(bool finished = false, bool sendStop = true)
    {
        if (_playing == null) return;
        _playing = null;
        if (sendStop && MacHelper.IsAvailable) _ = MacHelper.Instance.RequestAsync("play_stop").ContinueWith(_ => { });
        SetStatus(finished ? "再生が終わりました" : "再生を止めました");
    }

    // ───────── 記録した音声の保存 ─────────

    private readonly List<Task> _encodes = [];
    private bool _closingAfterEncode;

    private void SaveAudio_Click(object? sender, RoutedEventArgs e)
    {
        _settings.MinutesSaveAudio = SaveAudioCheck.IsChecked == true;
        _settings.Save();
    }

    private string NewAudioPath(bool newSession)
    {
        var start = newSession ? DateTime.Now : _doc.StartedAt ?? DateTime.Now;
        int n = newSession ? 1 : _doc.AudioParts.Count + 1;
        return Path.Combine(AutoSaveDir, $"議事録_{start:yyyyMMdd_HHmmss}_音声{n}.wav");
    }

    private SessionRecorder? _addedRecording; // 議事録に入れた最後の記録の音声 (終了のときと止め終わったときに重ねて入れない)

    // ───────── 会議の画面の録画・字幕つきの動画 ─────────

    private string? _videoPath;     // 録画している動画 (補助プログラムで録画中)
    private int _videoToken;        // その録画の番号 (補助プログラムの録画を、画面の録画と取り違えない)
    private DateTime _videoStarted;
    private bool _burning;
    private bool _closingWindow;
    private readonly List<AudioPart> _sessionVideoParts = []; // この記録で録画したもの (止めたら字幕つきの動画を作る)
    private readonly Queue<(IReadOnlyList<AudioPart> Parts, string? Output)> _burnQueue = new();
    private CancellationTokenSource? _burnCts;
    private string? _burnTarget;    // 作っている字幕つきの動画 (中止したら作りかけを消す)

    private void RecordVideo_Click(object? sender, RoutedEventArgs e)
    {
        _settings.MinutesRecordVideo = RecordVideoCheck.IsChecked == true;
        _settings.MinutesSubtitledVideo = SubtitleVideoCheck.IsChecked == true;
        _settings.Save();
        SubtitleVideoCheck.IsEnabled = _settings.MinutesRecordVideo;
    }

    /// <summary>録画の保存先 (「画面の録画」と同じ。設定が空なら「ムービー/GetText」)。</summary>
    private string VideoFolder => string.IsNullOrWhiteSpace(_settings.RecordFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Movies", "GetText")
        : _settings.RecordFolder;

    /// <summary>「会議の画面も録画する」なら、選んだアプリのウィンドウ (すべての音ならメインの画面) を録画し始める。失敗しても議事録は続ける。</summary>
    private async Task StartVideoAsync(WindowInfo? window)
    {
        if (!_settings.MinutesRecordVideo || App.DemoMode || window == null || !MacHelper.IsAvailable) return;
        try
        {
            var targets = await MacHelper.Instance.RequestAsync("record_targets", timeout: TimeSpan.FromSeconds(10));
            int windowId = 0, displayId = 0;
            if (window.IsAll)
                displayId = targets["displays"]!.AsArray().Where(d => d!["main"]?.GetValue<bool>() == true)
                    .Select(d => d!["id"]!.GetValue<int>()).FirstOrDefault();
            else // そのアプリのいちばん大きいウィンドウ
                windowId = targets["windows"]!.AsArray().Where(w => w!["pid"]!.GetValue<int>() == window.ProcessId)
                    .OrderByDescending(w => w!["area"]?.GetValue<int>() ?? 0).Select(w => w!["id"]!.GetValue<int>()).FirstOrDefault();
            if (windowId == 0 && displayId == 0)
            {
                ShowNotice($"「{window.Title}」の録画できるウィンドウが見つかりませんでした。議事録の記録は続けます。");
                return;
            }
            Directory.CreateDirectory(VideoFolder);
            var path = Path.Combine(VideoFolder, $"議事録_{DateTime.Now:yyyyMMdd_HHmmss}_録画.mp4");
            int token = MacHelper.NewRecordingToken();
            try
            {
                await MacHelper.Instance.RequestAsync("record_start", new JsonObject
                {
                    ["window"] = windowId, ["display"] = displayId, ["audio"] = true,
                    ["fps"] = 30, ["hq"] = _settings.RecordHighQuality, ["path"] = path, ["token"] = token,
                }, TimeSpan.FromSeconds(20));
            }
            catch
            {
                // 待ちきれなかったなど: 補助プログラムが後から始めた録画を残さない (この番号の録画だけを止める)
                _ = MacHelper.Instance.RequestAsync("record_stop", new JsonObject { ["token"] = token }, TimeSpan.FromSeconds(60)).ContinueWith(_ => { });
                throw;
            }
            if (!_service.IsRecording || IsStopping || _closed)
            {
                // 録画を始めている間に、記録が止められた・画面が閉じられた
                try { await MacHelper.Instance.RequestAsync("record_stop", new JsonObject { ["token"] = token }, TimeSpan.FromSeconds(60)); } catch { }
                try { File.Delete(path); } catch { }
                return;
            }
            _videoPath = path;
            _videoToken = token;
            _videoStarted = DateTime.Now;
        }
        catch (Exception ex)
        {
            App.Log("MinutesVideo", ex);
            ShowNotice("会議の画面を録画できませんでした (" + ex.Message + ")。議事録の記録は続けます。");
        }
    }

    private Task _videoStopping = Task.CompletedTask; // 録画を書き終えている途中

    /// <summary>録画を止めて書き終え、保存できたら議事録に加える (できなければ知らせる)。止めている途中ならそれを待つ。</summary>
    private Task StopVideoRecorderAsync()
    {
        var path = _videoPath;
        if (path == null) return _videoStopping;
        _videoPath = null;
        int token = _videoToken;
        var started = _videoStarted;
        return _videoStopping = StopCoreAsync();

        async Task StopCoreAsync()
        {
            try
            {
                var reply = await MacHelper.Instance.RequestAsync("record_stop", new JsonObject { ["token"] = token }, TimeSpan.FromSeconds(60));
                double seconds = reply["seconds"]?.GetValue<double>() ?? (DateTime.Now - started).TotalSeconds;
                AddVideoPart(new AudioPart(path, started, seconds));
            }
            catch (Exception ex)
            {
                App.Log("MinutesVideoStop", ex);
                ShowNotice("会議の画面の録画を保存できませんでした: " + ex.Message);
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
        else ShowNotice($"会議の画面を録画しました: {Path.GetFileName(parts[^1].Path)}", "info", "Finder で表示", () => ShowInFinder(parts[^1].Path));
    }

    /// <summary>アプリの終了: 待たずに録画を止めて議事録に加える (字幕つきの動画は作らない)。</summary>
    private void StopVideoNow()
    {
        foreach (var part in _sessionVideoParts) WriteVideoSubtitles(part); // (途中で止まって保存した録画の字幕のファイル)
        _sessionVideoParts.Clear();
        var path = _videoPath;
        if (path == null) return;
        _videoPath = null;
        try
        {
            var reply = MacHelper.Instance.Request("record_stop", new JsonObject { ["token"] = _videoToken }, TimeSpan.FromSeconds(15));
            _doc.VideoParts.Add(new AudioPart(path, _videoStarted, reply["seconds"]?.GetValue<double>() ?? (DateTime.Now - _videoStarted).TotalSeconds));
            WriteVideoSubtitles(_doc.VideoParts[^1]);
        }
        catch (Exception ex)
        {
            App.Log("MinutesVideoShutdown", ex);
        }
    }

    private void DeleteBurnPart()
    {
        if (_burnTarget == null) return;
        try { File.Delete(_burnTarget + ".part"); } catch { }
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
    /// 録画した動画に、今の発言を字幕にして焼き込んだ動画を作る (Python の処理。Mac では VideoToolbox で書き出す)。
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
                    ShowNotice("録画のファイルが見つかりません: " + part.Path);
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
                    (done, total) => Dispatcher.UIThread.Post(() =>
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
                    "info", "Finder で表示", () => ShowInFinder(last));
            }
        }
        catch (Exception ex) when (_burnCts.IsCancellationRequested)
        {
            SetStatus("字幕つきの動画を作るのを中止しました (" + ex.Message + ")");
        }
        catch (Exception ex)
        {
            App.Log("MinutesBurn", ex);
            ShowNotice("字幕つきの動画を作れませんでした: " + ex.Message);
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

    private static void ShowInFinder(string path)
    {
        try { Process.Start(new ProcessStartInfo("open", ["-R", path]) { UseShellExecute = false }); } catch { }
    }

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
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    int index = _doc.AudioParts.IndexOf(part);
                    if (index >= 0) _doc.AudioParts[index] = part with { Path = m4a };
                    else if (ownerJson != null) RewriteAudioPath(ownerJson, wav, m4a);
                    if (_playerPath == wav)
                    {
                        StopPlayback();
                        _playerPath = null;
                    }
                    TryDelete(wav);
                    AutoSave();
                });
            }
            catch (Exception ex)
            {
                App.Log("MinutesAudioEncode", ex);
            }
        }
    }

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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ───────── メモ ─────────

    private void AddMemo_Click(object? sender, RoutedEventArgs e) => AddMemo();

    private void MemoBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddMemo();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            MemoBox.Text = "";
            e.Handled = true;
        }
    }

    internal void AddMemo()
    {
        var text = (MemoBox.Text ?? "").Trim();
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
        else if (_doc.FromFile && _playing != null && _doc.StartedAt is { } fileStart)
            time = fileStart + TimeSpan.FromSeconds(_playFromSeconds + (DateTime.Now - _playStarted).TotalSeconds);
        else if (last != null)
            time = last.Time + last.Duration + TimeSpan.FromMilliseconds(1);
        else
            time = DateTime.Now;
        var note = _doc.AddNote(time, text);
        MemoBox.Text = "";
        if (AutoScrollCheck.IsChecked == true) EntryList.ScrollIntoView(note);
        RestartAutoSave();
        SetStatus($"メモを追加しました ({note.TimeText})。本文はクリックで書き直せ、右クリックで削除できます");
    }

    // ───────── 用語の登録・人数・覚えた声 ─────────

    private readonly Dictionary<int, string> _voiceNames = [];
    private readonly Dictionary<int, string> _enrolled = [];

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

    private async void Terms_Click(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.EditTermsAsync(this, _settings.MinutesTerms) is not { } terms) return;
        _settings.MinutesTerms = terms;
        _settings.Save();
        UpdateTermsLabel();
        if (_service.IsReady) _service.SetTerms(terms);
        SetStatus(terms.Count > 0 ? $"用語を {terms.Count} 件登録しました (次の発言から使います)" : "用語の登録を消しました");
    }

    private void SpeakerCount_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || App.DemoMode) return;
        if (_service.IsReady) _service.SetExpectedSpeakers(SpeakerCountBox.SelectedIndex);
        SetStatus(SpeakerCountBox.SelectedIndex == 0
            ? "人数は声から自動で推定します"
            : $"ウィンドウの音声を {SpeakerCountBox.SelectedIndex} 人以内で聞き分けます (多く分かれたときは、その人数で分け直します)");
    }

    private async void Voices_Click(object? sender, RoutedEventArgs e)
    {
        await Dialogs.ManageVoicesAsync(this, name =>
        {
            if (_service.IsReady) _service.ForgetVoice(name);
            else Dialogs.ForgetInFile(name);
        });
        UpdateVoicesLabel(Dialogs.LoadVoices().Count);
    }

    private void RememberVoice(MinutesSpeaker? speaker)
    {
        if (speaker != null && _voiceNames.TryGetValue(speaker.Id, out var auto) && auto != speaker.Name)
            _voiceNames.Remove(speaker.Id);
        if (speaker == null || App.DemoMode || !_settings.MinutesRememberVoices || !_service.IsReady) return;
        if (speaker.Id is <= 0 or >= 1000 || speaker.IsUnnamed) return;
        if (!_doc.Speakers.Contains(speaker) || _enrolled.TryGetValue(speaker.Id, out var done) && done == speaker.Name) return;
        if (_doc.SameNameAs(speaker) != null) return; // ほかの話者と同じ名前 (まとめる前) の声は覚えない
        _service.EnrollVoice(speaker.Id, speaker.Name);
        _enrolled[speaker.Id] = speaker.Name;
        _voiceNames.Remove(speaker.Id);
    }

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
        if (speaker == null || !speaker.IsUnnamed || _doc.Speakers.Any(s => s != speaker && s.Name == name)) return;
        speaker.Name = name;
        SetStatus($"声から「{name}」さんと判断して名前を付けました (違うときは名前を書き換えてください)");
    }

    // ───────── 入力と選択の解除 ─────────

    /// <summary>
    /// 窓の中を押したとき: 入力中の文字の欄の外なら入力を終え、発言の一覧の行の外なら、選んでいた発言の選択を外す。
    /// </summary>
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        if (FocusManager?.GetFocusedElement() is TextBox focused && focused != source && !focused.IsVisualAncestorOf(source)
            && source.FindAncestorOfType<TextBox>(includeSelf: true) == null
            && !SummaryPanel.IsVisualAncestorOf(source)
            && source.FindAncestorOfType<ComboBox>(includeSelf: true) == null
            && source.FindAncestorOfType<ScrollBar>(includeSelf: true) == null)
            LeaveTextBox();
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true) == null
            && source.FindAncestorOfType<ScrollBar>(includeSelf: true) == null
            && source.FindAncestorOfType<MenuItem>(includeSelf: true) == null
            && EntryList.SelectedItems is { Count: > 0 } selected)
            selected.Clear();
    }

    /// <summary>入力中の欄から抜ける (話者の名前は確定する)。キーボードの操作が効くよう、発言の一覧にフォーカスを移す。</summary>
    internal void LeaveTextBox(TextBox? box = null)
    {
        box ??= FocusManager?.GetFocusedElement() as TextBox;
        if (box != null)
        {
            box.ClearSelection();
            if (box.DataContext is MinutesSpeaker speaker && box.Text != speaker.Name)
            {
                CommitSpeakerName(box, speaker);
                if (_closing || !MergeIfSameName(speaker)) RememberVoice(speaker); // 閉じる途中は、まとめるかを聞かない
            }
        }
        EntryList.Focus();
    }

    // ───────── 文字起こしの処理が止まったとき ─────────

    private async void OnProcessExited()
    {
        if (_closed) return;
        bool recording = _service.IsRecording;
        if (recording && !_busy) await StopWithBusyAsync();
        // 起動し直した処理は発言と話者の番号を 1 から数え直すので、今の発言を切り離す (前の発言・話者に当たらないように)
        _doc.Detach();
        _voiceNames.Clear();
        _enrolled.Clear();
        _loading = false;
        _loadTimer.Stop();
        LoadingOverlay.IsVisible = false;
        SetState("error");
        ShowNotice("文字起こしの処理が止まりました (ここまでの議事録は自動保存しています)。もう一度「記録を開始」を押すと、起動し直して続けられます。", "error");
        if (recording)
            await Dialogs.AlertAsync(this, "文字起こしの処理が止まったため、記録を止めました。\nここまでの議事録は自動保存しています。\n\nもう一度「記録を再開」を押すと、起動し直して続けられます。");
    }

    // ───────── 動作確認用 ─────────

    internal MinutesDocument Document => _doc;
    internal IReadOnlyList<MinutesEntry> VisibleEntries => _view;
}
