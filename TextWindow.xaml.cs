using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Windows.Media.Ocr;

namespace GetText;

public partial class TextWindow : Window
{
    /// <summary>テキスト選択中は書き換えを保留し、選択解除時に反映する表示欄。空のときは案内文を出す。</summary>
    private sealed class Pane(TextBox box, UIElement placeholder)
    {
        public TextBox Box { get; } = box;
        public string? Pending { get; private set; }
        /// <summary>欄から離れた時刻 (選んだまま離れても、しばらくは書き換えずに選択を残す)。</summary>
        public DateTime LeftAt { get; set; } = DateTime.MinValue;
        private static readonly TimeSpan HoldAfterLeave = TimeSpan.FromSeconds(30);

        public bool Set(string text)
        {
            if (text == Box.Text)
            {
                Pending = null;
                return true;
            }
            if (Box.SelectionLength > 0 && (Box.IsKeyboardFocusWithin || DateTime.Now - LeftAt < HoldAfterLeave))
            {
                Pending = text;
                return false;
            }
            Apply(text);
            return true;
        }

        public void FlushIfFree()
        {
            if (Pending != null && Box.SelectionLength == 0) Apply(Pending);
        }

        /// <summary>表示を書き換えた (検索の印を付け直す)。</summary>
        public event Action? Applied;

        private void Apply(string text)
        {
            double offset = Box.VerticalOffset;
            Box.Text = text;
            Box.ScrollToVerticalOffset(offset);
            Pending = null;
            placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Applied?.Invoke();
        }
    }

    private const double FollowGap = 8;

    private readonly CaptureWindow _capture;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new();
    private readonly OcrPipeline _pipeline = new();
    private readonly AiOcr _aiOcr = new();
    private readonly Translator _translator = new();
    private readonly TextAccumulator _accumulator = new();
    private readonly Pane _ocrPane;
    private readonly Pane _translationPane;
    private GlobalHotkeys? _hotkeys;
    private SettingsWindow? _settingsWindow;
    private MinutesWindow? _minutesWindow;

    private OcrEngine? _engine;
    private OcrEngine? _latinEngine;
    private bool _paused;
    private bool _busy;
    private bool _aiOcrReady;
    private string? _aiOcrError;
    private DateTime _aiOcrErrorAt;
    private int _aiOcrFailures;
    private byte[]? _lastPixels;
    private int _lastCaptureHeight;
    private IReadOnlyList<OcrLineData>? _lastLines;
    /// <summary>直前に読み取った画面の枠線 (枠・まとまりごとに表示するときだけ探す)。</summary>
    private IReadOnlyList<LayoutSegment> _lastSegments = [];
    private OcrGrouping _grouping = OcrGrouping.None;
    /// <summary>AI OCR の準備 (その間は Windows OCR で読み取る)。</summary>
    private Task? _aiWarmup;
    private long _lastOcrMs;
    private bool _accumulating;
    private OcrDocument? _doc;
    private List<List<(string Text, ScriptKind Kind)>> _units = [];

    private bool _translating;
    private DateTime _nextTranslateAt = DateTime.MinValue;
    private CancellationTokenSource _translateCts = new();
    private string? _translateError;

    /// <summary>設定画面に出す Windows OCR の言語 (Tag は言語タグか "auto")。</summary>
    public IReadOnlyList<Option<string>> OcrLanguages { get; }

    public AiOcr AiOcr => _aiOcr;
    public Translator Translator => _translator;

    /// <summary>利用者が選んだ読み取りの方式 (拡張機能・Quick OCR が方式を指定しないときに使う)。</summary>
    public string PreferredOcrProvider => UseAiOcr ? "ai" : "windows";

    /// <summary>設定 (開発者向けの API の入・切など、窓の外から読む)。</summary>
    public AppSettings Settings => _settings;

    /// <summary>開発者向けの API: 表示している原文と訳 (UI のスレッドで読む)。</summary>
    public (bool Open, string Text, string? Translation) ReadingTextForApi() =>
        Dispatcher.Invoke(() => (IsOcrOpen, ResultBox.Text ?? "", _settings.Translate ? TranslationBox.Text : null));

    public TextWindow(CaptureWindow capture, AppSettings settings)
    {
        InitializeComponent();
        _capture = capture;
        _settings = settings;
        _ocrPane = new Pane(ResultBox, OcrPlaceholder);
        _translationPane = new Pane(TranslationBox, TranslationPlaceholder);
        _ocrPane.Applied += RefreshSearch;
        ResultBox.Loaded += (_, _) =>
        {
            // 検索で見つかった所の印 (原文の欄の上に重ねて描く)
            if (_highlights != null) return;
            var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(ResultBox);
            if (layer == null) return;
            _highlights = new SearchHighlightAdorner(ResultBox);
            layer.Add(_highlights);
            ResultBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => _highlights.InvalidateVisual()));
        };
        ResultBox.SizeChanged += (_, _) => _highlights?.InvalidateVisual();
        Theme.Changed += () => { _highlights?.InvalidateVisual(); UpdateCaptureHighlights(); };
        foreach (var pane in new[] { _ocrPane, _translationPane })
        {
            var pn = pane;
            pn.Box.IsInactiveSelectionHighlightEnabled = true; // 離れても選んだ所が見えるように
            pn.Box.LostKeyboardFocus += (_, _) => pn.LeftAt = DateTime.Now;
        }

        Width = settings.TextWidth;
        Height = settings.TextHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ScreenUtil.EnsureVisible(_capture); // 保存した位置がモニターの外 (モニターを外したときなど) なら戻す
        if (!settings.Follow && settings.TextLeft is double l && settings.TextTop is double t)
        {
            Left = l;
            Top = t;
            ScreenUtil.EnsureVisible(this);
        }
        else
        {
            FollowCapture();
        }

        OcrLanguages = BuildLanguageOptions();
        BuildConvertMenu();
        CreateEngine();
        ApplyAllSettings();

        _capture.PauseRequested += (_, _) => SetPaused(!_paused);
        _capture.CloseRequested += (_, _) => Close();
        _capture.LocationChanged += (_, _) => { if (_settings.Follow) FollowCapture(); };
        _capture.SizeChanged += (_, _) =>
        {
            if (_settings.Follow) FollowCapture();
            _lastPixels = null; // サイズが変わったら次回は必ず再認識
        };
        // 拡大率の違うモニターへ移った枠は、WPF の大きさ (DIP) は同じまま実際の大きさが変わる (SizeChanged が来ない)。
        // Windows が枠の大きさを変え終わってから隣に置き直す
        _capture.DpiChanged += (_, _) =>
        {
            if (_settings.Follow) Dispatcher.BeginInvoke(FollowCapture, DispatcherPriority.Background);
        };

        StateChanged += (_, _) =>
            _capture.Visibility = WindowState == WindowState.Minimized ? Visibility.Hidden : Visibility.Visible;
        Closing += OnClosing;
        SizeChanged += (_, _) => FitToolbar();
        Loaded += (_, _) => { FitToolbar(); UpdateSetupHint(); };
        Activated += (_, _) => UpdateSetupHint(); // セットアップから戻ったとき
        // 窓ができたら物理ピクセルで置き直す (拡大率の違うモニターでも、枠の隣・前回の場所に正しく置く)
        SourceInitialized += (_, _) =>
        {
            if (_settings.Follow) FollowCapture();
            else ScreenUtil.RestorePixelPosition(this, _settings.TextPixel);
            ScreenUtil.EnsureVisible(this);
            _normalPixel = ScreenUtil.PixelPosition(this);
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(DisplayHook);
        };
        LocationChanged += (_, _) => _normalPixel = ScreenUtil.PixelPosition(this) ?? _normalPixel;
        _displayDelay.Tick += (_, _) =>
        {
            _displayDelay.Stop();
            OnDisplayChanged();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        if (App.DemoMode) return; // 見本の画面: 読み取りもホットキーも使わない
        SourceInitialized += (_, _) => RegisterHotkeys();

        _timer.Tick += async (_, _) =>
        {
            ReleaseIdleOcr();
            await RunOcrAsync(force: false);
        };
        _timer.Start();

        if (OcrEngine.AvailableRecognizerLanguages.Count == 0 && !AiOcr.IsInstalled)
            SetStatus("文字を読み取れません。上の「セットアップ」で AI の機能を入れるか、Windows の設定 (時刻と言語 → 言語と地域) で日本語の「光学式文字認識」を追加してください。");
    }

    // ───────── 設定の反映 ─────────

    private void ApplyAllSettings()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(_settings.IntervalMs);
        Topmost = _settings.TextTopmost;
        PinToggle.IsChecked = _settings.TextTopmost;
        ApplyWrap();
        SetFontSize(_settings.FontSize);
        _translator.Engine = _settings.TranslationEngine;
        _translator.DeepLKey = _settings.GetDeepLKey();
        ApplyOcrEngineState();
        ApplyLayout();
        if (_settings.Translate && !App.DemoMode) _translator.WarmUp();
        UpdateChips();
    }

    /// <summary>見本の画面 (操作手順の動画用): 読み取り結果と訳を表示する。</summary>
    internal void ShowDemo(string ocr, string translation, string ocrChip, string translationChip, string status)
    {
        _ocrPane.Set(ocr);
        _translationPane.Set(translation);
        OcrChip.Text = ocrChip;
        TranslationChip.Text = translationChip;
        StatusText.Text = status;
    }

    /// <summary>見本の画面: 読み取った行 (位置つき) を表示する。pixels があれば枠線も探す (枠・まとまりごとの見本)。translation は訳の欄に出す文。</summary>
    internal void ShowDemoLines(IReadOnlyList<OcrLineData> lines, byte[]? pixels, int width, int height, string? translation)
    {
        _accumulating = false;
        _lastLines = lines;
        _lastFromAi = true;
        _lastCaptureHeight = height;
        _lastSegments = pixels != null && _settings.OcrView == OcrView.Groups ? OcrLayout.FindSegments(pixels, width, height, lines) : [];
        UpdateDocument(fromNewFrame: true);
        if (translation != null) _translationPane.Set(translation);
        TranslationNote.Text = "";
        _lastOcrMs = 52;
        UpdateChips();
        StatusText.Text = $"{lines.Count} 行{GroupStatus}";
    }

    /// <summary>見本の画面: 知らせの帯の状態 (loading = AI OCR の準備中、fallback = AI OCR を使えない、empty = 文字が見つからない、"" = 戻す)。</summary>
    internal void ShowDemoState(string state)
    {
        LoadingBar.Visibility = state == "loading" ? Visibility.Visible : Visibility.Collapsed;
        FallbackBar.Visibility = state == "fallback" ? Visibility.Visible : Visibility.Collapsed;
        if (state == "fallback")
        {
            FallbackText.Text = "ocr_server.py が起動しませんでした (Python が見つかりません)";
            OcrChip.Text = "Windows OCR · 180 ms";
        }
        if (state == "loading") OcrChip.Text = "Windows OCR · 160 ms";
        if (state == "empty")
        {
            _lastLines = [];
            UpdateDocument();
            _translationPane.Set("");
            TranslationNote.Text = "";
            StatusText.Text = "0 行";
        }
    }

    internal void ShowDemoSearch(string query)
    {
        ShowSearch();
        SearchBox.Text = query;
    }

    internal void CloseDemoSearch() => CloseSearch();

    /// <summary>設定画面で値が変わったときに呼ばれる。</summary>
    public void OnSettingChanged(string name)
    {
        switch (name)
        {
            case nameof(AppSettings.OcrEngine):
                ApplyOcrEngineState();
                ResetRecognition();
                break;
            case nameof(AppSettings.Language):
                CreateEngine();
                break;
            case nameof(AppSettings.Scale):
            case nameof(AppSettings.HighAccuracy):
                ResetRecognition();
                break;
            case nameof(AppSettings.JoinCjk):
                UpdateDocument();
                break;
            case nameof(AppSettings.IntervalMs):
                _timer.Interval = TimeSpan.FromMilliseconds(_settings.IntervalMs);
                break;
            case nameof(AppSettings.MinutesTopmost):
                if (_minutesWindow != null) _minutesWindow.Topmost = _settings.MinutesTopmost;
                break;
            case nameof(AppSettings.RecorderTopmost):
                if (App.Home?.Recorder is { } recorder) recorder.Topmost = _settings.RecorderTopmost;
                break;
            case nameof(AppSettings.TextTopmost):
                Topmost = _settings.TextTopmost;
                PinToggle.IsChecked = _settings.TextTopmost;
                break;
            case nameof(AppSettings.OcrView):
                if (_settings.OcrView == OcrView.Groups && _lastSegments.Count == 0) _lastPixels = null; // 枠線を探すため、すぐに読み直す
                UpdateDocument();
                break;
            case nameof(AppSettings.Follow):
                if (_settings.Follow) FollowCapture();
                break;
            case nameof(AppSettings.Wrap):
                ApplyWrap();
                break;
            case nameof(AppSettings.FontSize):
                SetFontSize(_settings.FontSize);
                break;
            case nameof(AppSettings.Convert):
                RenderOcr();
                RenderTranslation();
                break;
            case nameof(AppSettings.Layout):
                ApplyLayout();
                break;
            case nameof(AppSettings.Translate):
                ApplyLayout();
                if (_settings.Translate)
                {
                    _translator.WarmUp();
                    RestartTranslation();
                }
                else
                {
                    _translateCts.Cancel();
                }
                break;
            case nameof(AppSettings.TranslationEngine):
                _translator.Engine = _settings.TranslationEngine;
                _translator.ClearCache();
                _translator.WarmUp();
                RestartTranslation();
                break;
            case nameof(AppSettings.DeepLKeyProtected):
                _translator.DeepLKey = _settings.GetDeepLKey();
                _translator.ClearCache();
                RestartTranslation();
                break;
            case nameof(AppSettings.Theme):
                App.ApplyTheme(_settings.Theme);
                break;
            case nameof(AppSettings.MinutesFontSize):
                _minutesWindow?.ApplyFontSize();
                break;
            case nameof(AppSettings.MinutesShowTime):
            case nameof(AppSettings.MinutesTranslate):
            case nameof(AppSettings.MinutesHideFillers):
            case nameof(AppSettings.MinutesAutoScroll):
                _minutesWindow?.ApplyDisplaySetting(name);
                break;
        }
        UpdateChips();
        _settings.Save();
    }

    private void ApplyWrap()
    {
        var wrap = _settings.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ResultBox.TextWrapping = wrap;
        TranslationBox.TextWrapping = wrap;
    }

    private void SetFontSize(double size)
    {
        size = Math.Clamp(size, 9, 40);
        ResultBox.FontSize = TranslationBox.FontSize = size;
        _settings.FontSize = size;
    }

    /// <summary>日本語訳の欄を原文の下 / 右に置く。翻訳しないときは原文だけ。</summary>
    private void ApplyLayout()
    {
        var rows = PaneGrid.RowDefinitions;
        var cols = PaneGrid.ColumnDefinitions;
        var star = new GridLength(1, GridUnitType.Star);
        var zero = new GridLength(0);
        var gap = new GridLength(8);
        foreach (var r in rows) { r.Height = zero; r.MinHeight = 0; }
        foreach (var c in cols) { c.Width = zero; c.MinWidth = 0; }

        bool translate = _settings.Translate;
        TranslationCard.Visibility = Splitter.Visibility = translate ? Visibility.Visible : Visibility.Collapsed;
        TranslationChipBorder.Visibility = translate ? Visibility.Visible : Visibility.Collapsed;
        Place(OcrCard, 0, 0, 3, 3);

        if (!translate)
        {
            rows[0].Height = star;
            cols[0].Width = star;
        }
        else if (_settings.Layout == PaneLayout.Right)
        {
            rows[0].Height = star;
            cols[0].Width = star; cols[1].Width = gap; cols[2].Width = star;
            cols[0].MinWidth = cols[2].MinWidth = 120;
            Place(OcrCard, 0, 0, 3, 1);
            Place(Splitter, 0, 1, 3, 1);
            Place(TranslationCard, 0, 2, 3, 1);
            Splitter.ResizeDirection = GridResizeDirection.Columns;
        }
        else
        {
            cols[0].Width = star;
            rows[0].Height = star; rows[1].Height = gap; rows[2].Height = star;
            rows[0].MinHeight = rows[2].MinHeight = 70;
            Place(OcrCard, 0, 0, 1, 3);
            Place(Splitter, 1, 0, 1, 3);
            Place(TranslationCard, 2, 0, 1, 3);
            Splitter.ResizeDirection = GridResizeDirection.Rows;
        }
        Title = translate ? "GetText — 原文 / 日本語訳" : "GetText — 原文";
    }

    private static void Place(UIElement e, int row, int col, int rowSpan, int colSpan)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        Grid.SetRowSpan(e, rowSpan);
        Grid.SetColumnSpan(e, colSpan);
    }

    /// <summary>
    /// 下の右の小さな表示: 読み取りの方式と時間 (「AI OCR · 48 ms」)、翻訳の場所 (PC 内 / オンライン)。
    /// 詳しいこと (モデル・GPU・確からしさ) はマウスを乗せると出る。
    /// </summary>
    private void UpdateChips()
    {
        bool ai = UseAiOcr && _aiOcrError == null && _aiOcrReady;
        string name = ai ? "AI OCR" : "Windows OCR";
        OcrChip.Text = _lastOcrMs > 0 ? $"{name} · {_lastOcrMs} ms" : name;
        OcrChipPanel.ToolTip = ai
            ? $"AI OCR (RapidOCR + PP-OCRv6)\n{(_aiOcr.Device switch { "gpu" => "GPU (DirectML / CUDA)", "cpu" => "CPU", _ => "準備中" })}\n確からしさ {_aiOcr.LastConfidence:P0}"
            : UseAiOcr ? "Windows OCR (AI OCR を準備中・使えないため)" : "Windows OCR (Windows の標準の文字認識)";
        bool local = _translator.Engine == TranslationEngine.Local;
        TranslationChip.Text = local ? "翻訳: PC 内" : $"翻訳: {(_translator.Engine == TranslationEngine.DeepL ? "DeepL" : "Google")} (オンライン)";
        TranslationPlaceIcon.Text = WindowsIcons.Glyph(local ? AppIcon.Local : AppIcon.Cloud);
        TranslationChipBorder.ToolTip = local
            ? $"{_translator.EngineName}\n文字は PC の外に送りません"
            : $"{_translator.EngineName}\n外国語の文をインターネットに送って訳します";
        UpdateFallbackBar();
    }

    // ───────── OCR エンジン ─────────

    private static IReadOnlyList<Option<string>> BuildLanguageOptions()
    {
        var installed = OcrEngine.AvailableRecognizerLanguages;
        var options = installed.Select(x => new Option<string>(x.DisplayName, x.LanguageTag)).ToList();
        bool hasJa = installed.Any(x => x.LanguageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase));
        bool hasEn = installed.Any(x => x.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        if (hasJa && hasEn) options.Insert(0, new Option<string>("自動 (日本語 + 英語)", SettingsOptions.AutoLanguage));
        return options;
    }

    private void CreateEngine()
    {
        var langs = OcrEngine.AvailableRecognizerLanguages;
        var ja = langs.FirstOrDefault(x => x.LanguageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase));
        var en = langs.FirstOrDefault(x => x.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        _latinEngine = null;

        // 未設定は「自動 (日本語 + 英語)」(設定画面でもそう表示している)
        var language = string.IsNullOrEmpty(_settings.Language) ? SettingsOptions.AutoLanguage : _settings.Language;
        if (language == SettingsOptions.AutoLanguage && ja != null && en != null)
        {
            _engine = OcrEngine.TryCreateFromLanguage(ja);
            _latinEngine = OcrEngine.TryCreateFromLanguage(en);
        }
        else
        {
            var lang = langs.FirstOrDefault(x => x.LanguageTag == language) ?? ja ?? langs.FirstOrDefault();
            _engine = lang != null ? OcrEngine.TryCreateFromLanguage(lang) : OcrEngine.TryCreateFromUserProfileLanguages();
        }
        ResetRecognition();
    }

    private bool UseAiOcr => _settings.OcrEngine == OcrEngineKind.Ai && AiOcr.IsInstalled;

    private void ApplyOcrEngineState()
    {
        _aiOcrError = null;
        _aiOcrReady = false;
        _aiOcrFailures = 0;
        if (!UseAiOcr) _aiOcr.Release(); // Windows OCR にしたら AI OCR の補助プロセスを止める (メモリ・GPU を空ける。AI に戻したら起動し直す)
        if (UseAiOcr && !App.DemoMode)
            _ = _aiOcr.EnsureStartedAsync().ContinueWith(t =>
            {
                _ = t.Exception; // 失敗は読み取り時に扱うので、ここでは握りつぶす
                Dispatcher.BeginInvoke(UpdateChips);
            }, TaskScheduler.Default);
    }

    private void ResetRecognition()
    {
        _lastPixels = null;
        _pipeline.ResetAdaptive();
    }

    // 値は合成画像 408 枚でのベンチマークで最も誤りが少なかった組み合わせ
    private void ApplyTuning()
    {
        var t = _pipeline.Tuning;
        bool high = _settings.HighAccuracy;
        t.Enhance = high;
        t.Stretch = false; // コントラストの引き伸ばしは逆効果だった
        t.Padding = high ? 16 : 0;
        t.Bicubic = high;
        t.Sharpen = high ? 0.8 : 0;
        t.EnsembleFactors = high ? [1.3, 0.77] : [];
    }

    // ───────── OCR ─────────

    private DateTime _lastActive = DateTime.Now;
    private bool _accuratePending;
    private bool _accurateFailed;
    private const double AccurateBelow = 0.9; // 読み取りの確からしさがこれより低ければ、画面が止まったときに高精度で読み直す

    /// <summary>
    /// 一時停止・最小化・読み取りの画面を隠したまま 10 分たったら、AI OCR の補助プロセスを止めてメモリ (GPU のメモリも) を空ける
    /// (ゲームなどほかのアプリに回せるように)。読み取りを再開すると自動で起動し直す。
    /// </summary>
    private void ReleaseIdleOcr()
    {
        if (_busy) return; // 読み取りの途中では止めない
        bool idle = _paused || _ocrHidden || WindowState == WindowState.Minimized;
        if (!idle)
        {
            _lastActive = DateTime.Now;
            return;
        }
        // (起動しただけで読み取りに使っていないとき (機能を選ぶ画面から議事録だけを使うなど) も止める)
        if (UseAiOcr && _aiOcr.IsStarted && DateTime.Now - _lastActive > TimeSpan.FromMinutes(10))
        {
            _aiOcr.Release();
            _aiOcrReady = false;
            UpdateChips();
        }
    }

    /// <summary>
    /// 次の読み取りまでの間隔。読み取りに時間がかかる PC (GPU の無い PC の AI OCR など) では、かかった時間の 2 倍以上あけ、
    /// バッテリーで動いているときは 2 倍にする (画面が変わり続けると CPU を使い続けるため)。
    /// </summary>
    private void AdjustInterval(long ocrMs)
    {
        double ms = Math.Max(_settings.IntervalMs, ocrMs * 2);
        if (SystemParameters.PowerLineStatus == PowerLineStatus.Offline) ms *= 2;
        var interval = TimeSpan.FromMilliseconds(Math.Min(ms, 10_000));
        if (_timer.Interval != interval) _timer.Interval = interval;
    }

    private async Task RunOcrAsync(bool force)
    {
        if (_busy || _ocrHidden || (_engine == null && !UseAiOcr)) return;
        if (_paused && !force) return;
        if (WindowState == WindowState.Minimized && !force) return;
        if (force) _lastActive = DateTime.Now; // 止めている間に手で読み取ったときも、すぐに補助プロセスを止めない

        _busy = true;
        try
        {
            var rect = _capture.GetCaptureRectPixels();
            if (rect.IsEmpty || rect.Width < 4 || rect.Height < 4) return;

            var pixels = ScreenCapture.Capture(rect, rect.Width, rect.Height);
            // 画面が変わっていなければ OCR を省略 (文字入力カーソルの点滅くらいの変化も無視する)
            if (!force && _lastPixels != null && !ScreenCapture.HasChanged(_lastPixels, pixels))
            {
                // 画面が止まったら、小さい・ぼやけた文字 (確からしさが低い) は高精度の認識モデルで 1 回だけ読み直す
                if (_accuratePending && UseAiOcr && _aiOcrReady && _aiOcr.AccurateAvailable && !Accumulating && !_accurateFailed)
                {
                    _accuratePending = false;
                    var swa = Stopwatch.StartNew();
                    List<OcrLineData> lines;
                    try
                    {
                        lines = await _aiOcr.RecognizeAsync(pixels, rect.Width, rect.Height, CancellationToken.None, accurate: true);
                    }
                    catch (Exception ex)
                    {
                        // 読み込めないモデルを何度も試さない (アプリを起動し直すまで)。読み直しの途中で方式を変えたときは除く
                        if (UseAiOcr && _aiOcrReady) _accurateFailed = true;
                        App.Log("AccurateOcr", ex);
                        return;
                    }
                    // (読み直している間に枠の大きさを変えた・一時停止したなどで、前の画面が無くなっていることがある)
                    if (_lastPixels is { } before && !ScreenCapture.HasChanged(before, ScreenCapture.Capture(rect, rect.Width, rect.Height)))
                    {
                        _lastLines = lines;
                        _lastFromAi = true;
                        UpdateDocument(fromNewFrame: true);
                        SetStatus($"高精度で読み直しました {swa.Elapsed.TotalSeconds:0.0} 秒 ・ {lines.Count} 行{AccumulateStatus}",
                            $"画面が止まったので、小さい・ぼやけた文字を高精度の認識モデル (PP-OCRv6 medium) で読み直しました (確からしさ {_aiOcr.LastConfidence:P0})");
                    }
                }
                return;
            }
            _lastPixels = pixels;
            _lastCaptureHeight = rect.Height;
            _accuratePending = false;

            var sw = Stopwatch.StartNew();
            if (UseAiOcr && _aiOcrError != null && DateTime.Now - _aiOcrErrorAt > TimeSpan.FromSeconds(60))
            {
                _aiOcrError = null; // しばらくしたら AI OCR をもう一度試す (初回の準備が遅かった・セットアップした直後など)
                _aiOcrReady = false;
                _aiWarmup = null;
            }
            // AI OCR の準備中は待たせない: 裏で準備しながら Windows OCR で読み取る (Windows OCR の言語が無い PC は準備を待つ)
            bool useAi = UseAiOcr && _aiOcrError == null;
            if (useAi && !_aiOcrReady && _engine != null)
            {
                _aiWarmup ??= StartAiWarmup();
                if (!_aiWarmup.IsCompleted) useAi = false;
                else if (_aiWarmup.Exception is { } failed)
                {
                    _aiOcrError = failed.InnerException?.Message ?? failed.Message;
                    _aiOcrErrorAt = DateTime.Now;
                    useAi = false;
                }
            }
            ShowLoading(UseAiOcr && _aiOcrError == null && !_aiOcrReady);
            if (useAi)
            {
                try
                {
                    bool wasReady = _aiOcrReady;
                    _lastLines = await _aiOcr.RecognizeAsync(pixels, rect.Width, rect.Height, CancellationToken.None);
                    _lastFromAi = true;
                    sw.Stop();
                    if (!_aiOcrReady)
                    {
                        _aiOcrReady = true;
                        ShowLoading(false);
                    }
                    _aiOcrFailures = 0;
                    // 確からしさが低ければ (小さい・ぼやけた文字)、画面が止まったときに高精度で読み直す
                    _accuratePending = _aiOcr.LastConfidence < AccurateBelow && _lastLines.Count > 0;
                    if (wasReady) AdjustInterval(sw.ElapsedMilliseconds);
                    await FindLayoutAsync(pixels, rect.Width, rect.Height);
                    _lastOcrMs = sw.ElapsedMilliseconds;
                    UpdateDocument(fromNewFrame: true);
                    UpdateChips();
                    SetStatus($"{_lastLines.Count} 行{GroupStatus}{AccumulateStatus}",
                        $"{DateTime.Now:HH:mm:ss} 更新 ・ AI OCR ・ {sw.ElapsedMilliseconds} ms (確からしさ {_aiOcr.LastConfidence:P0})");
                    return;
                }
                catch (Exception) when (_engine != null && !UseAiOcr)
                {
                    sw.Restart();
                }
                catch (Exception ex) when (_engine == null && (!AiOcr.IsInstalled || !_aiOcrReady || ++_aiOcrFailures >= 3))
                {
                    // Windows OCR の言語も無い PC: 60 秒たってから試し直す (毎回 AI OCR の処理を起動し直さない)
                    _aiOcrError = ex.Message;
                    _aiOcrErrorAt = DateTime.Now;
                    _aiOcrFailures = 0;
                    _aiOcrReady = false;
                    ShowLoading(false);
                }
                catch (Exception ex) when (_engine != null && (!AiOcr.IsInstalled || !_aiOcrReady || ++_aiOcrFailures >= 3))
                {
                    // AI OCR が使えない (未セットアップ・起動失敗・続けて失敗) ときは Windows OCR で続ける (60 秒後にまた試す)
                    _aiOcrError = ex.Message;
                    _aiOcrErrorAt = DateTime.Now;
                    _aiOcrFailures = 0;
                    _aiOcrReady = false;
                    _aiWarmup = null;
                    ShowLoading(false);
                    sw.Restart();
                }
            }

            if (_engine == null)
            {
                _lastPixels = null; // 60 秒たったら画面が変わっていなくても試し直す
                UpdateChips();
                SetStatus(_aiOcrError != null ? "AI OCR を使えません (しばらくしてもう一度試します)" : "AI OCR を準備しています…",
                    _aiOcrError ?? "認識モデルを読み込んでいます");
                return;
            }
            ApplyTuning();
            double scale = _settings.Scale;
            if (scale <= 0 && !_settings.HighAccuracy) scale = 2;

            var output = await _pipeline.RecognizeAsync(_engine!, _latinEngine, pixels, rect.Width, rect.Height, scale);
            sw.Stop();

            _lastLines = output.Lines;
            _lastFromAi = false;
            AdjustInterval(sw.ElapsedMilliseconds);
            await FindLayoutAsync(pixels, rect.Width, rect.Height);
            _lastOcrMs = sw.ElapsedMilliseconds;
            UpdateDocument(fromNewFrame: true);
            UpdateChips();

            var details = new StringBuilder($"{DateTime.Now:HH:mm:ss} 更新 ・ Windows OCR ・ {sw.ElapsedMilliseconds} ms ・ 拡大 ×{output.Scale:0.#}");
            if (output.Passes > 1) details.Append($" ・ {output.Passes} 回読み取って多数決");
            if (output.Inverted) details.Append(" ・ 白黒を反転して読み取り");
            if (_aiOcrError != null) details.Append("\nAI OCR を使えないため Windows OCR で読み取りました: " + _aiOcrError);
            else if (UseAiOcr && !_aiOcrReady) details.Append("\nAI OCR を準備している間は Windows OCR で読み取ります");
            SetStatus($"{output.Lines.Count} 行{GroupStatus}{AccumulateStatus}", details.ToString());
        }
        catch (Exception ex)
        {
            _lastPixels = null; // 次の周期で読み直す
            SetStatus("エラー: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>AI OCR の補助プロセスを裏で起動する。終わったらすぐに読み直す (準備ができれば AI OCR で、失敗なら知らせる)。</summary>
    private Task StartAiWarmup()
    {
        var task = _aiOcr.EnsureStartedAsync();
        task.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            _lastPixels = null;
            _ = RunOcrAsync(force: false);
        }), TaskScheduler.Default);
        return task;
    }

    /// <summary>枠・まとまりごとに表示するときだけ、画面の枠線を探す (時間がかかるので裏で)。</summary>
    private async Task FindLayoutAsync(byte[] pixels, int width, int height)
    {
        if (_settings.OcrView != OcrView.Groups || Accumulating || _lastLines is not { Count: >= 2 } lines)
        {
            _lastSegments = [];
            return;
        }
        try
        {
            _lastSegments = await Task.Run(() => OcrLayout.FindSegments(pixels, width, height, lines));
        }
        catch (Exception ex)
        {
            App.Log("Layout", ex);
            _lastSegments = [];
        }
    }

    private bool Accumulating => _accumulating;

    private string AccumulateStatus => Accumulating ? $" ・ 蓄積 {_accumulator.LineCount} 行" : "";

    private string GroupStatus => _settings.OcrView != OcrView.Groups || Accumulating ? ""
        : _grouping.HasGroups ? $" ・ まとまり {_grouping.Groups.Count}" : " ・ まとまりなし (上から順)";

    /// <summary>直前の行を AI OCR で読んだか (文脈補正は Windows OCR の結果にだけ使う)。</summary>
    private bool _lastFromAi;

    /// <param name="fromNewFrame">新しく読み取った画面なら true (蓄積モードで追記する)。</param>
    private void UpdateDocument(bool fromNewFrame = false)
    {
        if (_lastLines == null) return;
        // 文脈補正は Windows OCR の癖に合わせたものなので AI OCR には使わない
        bool correct = !_lastFromAi && _settings.HighAccuracy;
        var frame = OcrDocument.From(_lastLines, _settings.JoinCjk, correct);
        if (fromNewFrame && !App.DemoMode && PluginRuntime.HasOcrSubscribers) PublishToPlugins(frame);
        _grouping = OcrGrouping.None;
        if (Accumulating)
        {
            if (fromNewFrame) _accumulator.Add(frame, _lastCaptureHeight);
            _doc = _accumulator.ToDocument();
            UpdateAccumulateBar();
        }
        else if (_settings.OcrView == OcrView.Groups)
        {
            // 枠・まとまりごと: 見つかれば【1】【2】… の見出しを付けて分ける。無ければ上から順のまま
            _grouping = OcrLayout.Group(_lastLines, _lastSegments);
            _doc = _grouping.HasGroups ? OcrLayout.ToDocument(_grouping, _settings.JoinCjk, correct) : frame;
        }
        else
        {
            _doc = frame;
        }
        bool hasKana = _doc.HasKana;
        _units = _doc.ToTranslationUnits()
            .Select(p => p.Select(u => (u, TextScript.Classify(u, hasKana))).ToList())
            .ToList();

        RenderOcr();
        RenderTranslation();
        PumpTranslation();
    }

    /// <summary>読み取った 1 回分を、受け取る拡張機能に渡す (別のスレッドで。読み取りは待たない)。</summary>
    private void PublishToPlugins(OcrDocument frame)
    {
        var r = _capture.GetCaptureRectPixels();
        if (r.IsEmpty) return;
        var source = WindowList.WindowAt(r.X + r.Width / 2, r.Y + r.Height / 2); // (読み取りの範囲の下のアプリ)
        PluginRuntime.PublishOcrFrame(PluginFrames.From(frame, (r.X, r.Y, r.Width, r.Height),
            image: _lastPixels is { } pixels ? (pixels, r.Width, r.Height) : null, source: source));
    }

    private void RenderOcr()
    {
        if (_doc == null) return;
        if (_doc.IsEmpty)
        {
            // 空の状態: 読み取ったが文字が無い
            // 黒く写っている (保護された画面など) ときは、そう知らせる (回避はしない)
            bool uniform = !Accumulating && _lastPixels is { } shot && _lastCaptureHeight > 0
                           && ProtectedContent.LooksProtected(shot, shot.Length / 4 / _lastCaptureHeight, _lastCaptureHeight);
            OcrEmptyTitle.Text = Accumulating ? "まだ集めた文字はありません" : uniform ? ProtectedContent.Title : "枠の中に文字が見つかりません";
            OcrEmptyDetail.Text = Accumulating
                ? "文書をスクロールすると、読んだ内容がここに追記されます。"
                : uniform ? ProtectedContent.Detail
                : "読みたい文字の上に枠を重ねるか、枠を広げてください。小さな文字は枠を大きくすると読みやすくなります。";
        }
        bool applied = _ocrPane.Set(TextConverter.Apply(_doc.ToDisplayText(), _settings.Convert));
        if (_aiOcrReady || !UseAiOcr) OcrNote.Text = applied ? "" : "選択中のため更新を保留中";
    }

    // ───────── 翻訳 ─────────

    private List<string> MissingForeignUnits() =>
        _units.SelectMany(p => p)
            .Where(u => u.Kind == ScriptKind.Foreign && !_translator.TryGetCached(u.Text, out _))
            .Select(u => u.Text)
            .ToList();

    private void RenderTranslation()
    {
        if (!_settings.Translate) return;

        int foreign = 0, missing = 0;
        var paragraphs = new List<string>();
        foreach (var paragraph in _units)
        {
            var lines = new List<string>();
            foreach (var (text, kind) in paragraph)
            {
                if (kind != ScriptKind.Foreign)
                {
                    lines.Add(text); // 日本語や数字だけの行はそのまま
                    continue;
                }
                foreign++;
                if (_translator.TryGetCached(text, out var translated)) lines.Add(translated);
                else { lines.Add("…"); missing++; }
            }
            paragraphs.Add(string.Join("\r\n", lines));
        }

        string note = "";
        if (foreign == 0)
        {
            _translationPane.Set("");
            if (_units.Count > 0) note = "外国語のテキストはありません";
        }
        else
        {
            var text = TextConverter.Apply(string.Join("\r\n\r\n", paragraphs), _settings.Convert);
            if (!_translationPane.Set(text)) note = "選択中のため更新を保留中";
            else if (missing > 0) note = "翻訳中…";
        }
        if (_translateError != null) note = _translateError;
        TranslationNote.Text = note;
        UpdateChips();
    }

    /// <summary>
    /// 翻訳リクエストは常に 1 本だけ。送信中に画面が変わったら、終わった後に最新の内容だけを送る。
    /// 無料エンドポイントに送りすぎないよう、送信間隔にも下限を設ける。
    /// </summary>
    private async void PumpTranslation()
    {
        if (_translating || !_settings.Translate || App.DemoMode) return;
        if (_translator.Engine == TranslationEngine.Local && !LocalTranslator.IsInstalled)
        {
            _translateError = "PC 内の翻訳は未セットアップです (上の「セットアップ」から入れられます。設定 → 翻訳 で Google 翻訳にもできます)";
            RenderTranslation();
            return;
        }
        _translating = true;
        int failures = 0;
        try
        {
            while (!_shutDown && _settings.Translate && MissingForeignUnits().Count > 0 && failures < 3)
            {
                var wait = _nextTranslateAt - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait);

                var missing = MissingForeignUnits(); // 待っている間に画面が変わっているかもしれない
                if (!_settings.Translate || missing.Count == 0) break;

                var ct = _translateCts.Token;
                try
                {
                    await _translator.TranslateMissingAsync(missing, ct);
                    _translateError = null;
                    _nextTranslateAt = DateTime.UtcNow + _translator.MinInterval;
                    failures = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // エンジン切り替えなら新しい取り消しの印で続ける。終了のときは止める (同じ印のまま回り続けないように)
                    if (_shutDown || ct == _translateCts.Token) break;
                }
                catch (TranslationBlockedException ex)
                {
                    _translateError = ex.Message;
                    _nextTranslateAt = DateTime.UtcNow + ex.RetryAfter;
                }
                catch (Exception ex)
                {
                    _translateError = "翻訳エラー: " + (ex is TaskCanceledException ? "タイムアウトしました" : ex.Message);
                    _nextTranslateAt = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                    failures++;
                }
                RenderTranslation();
            }
        }
        finally
        {
            _translating = false;
            // 続けて失敗したら、画面が変わらなくても 30 秒後にもう一度試す (回線が戻ったときに訳が出るように)
            if (failures >= 3) ScheduleTranslationRetry();
        }
    }

    private DispatcherTimer? _translateRetry;

    private void ScheduleTranslationRetry()
    {
        _translateRetry ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _translateRetry.Stop();
        _translateRetry.Tick -= OnTranslateRetry;
        _translateRetry.Tick += OnTranslateRetry;
        _translateRetry.Start();
    }

    private void OnTranslateRetry(object? sender, EventArgs e)
    {
        _translateRetry?.Stop();
        if (_settings.Translate && !_shutDown) PumpTranslation();
    }

    private void RestartTranslation()
    {
        _translateCts.Cancel();
        _translateCts = new CancellationTokenSource();
        _translateError = null;
        _nextTranslateAt = DateTime.MinValue;
        RenderTranslation();
        PumpTranslation();
    }

    // ───────── 操作 ─────────

    private void SetStatus(string text, string? details = null)
    {
        StatusText.Text = (_paused ? "停止中 ・ " : "") + text;
        StatusText.ToolTip = details ?? text;
    }

    private void SetPaused(bool paused)
    {
        _paused = paused;
        PauseToggle.IsChecked = paused;
        PauseIcon.Text = WindowsIcons.Glyph(paused ? AppIcon.Play : AppIcon.Pause);
        PauseLabel.Text = paused ? "再開" : "一時停止";
        _capture.SetPaused(paused);
        SetStatus(paused ? "自動読み取りを停止しました" : "自動読み取りを再開しました");
        if (!paused) _lastPixels = null;
    }

    private void PauseToggle_Click(object sender, RoutedEventArgs e) => SetPaused(PauseToggle.IsChecked == true);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunOcrAsync(force: true);

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsPage.ScreenOcr);

    /// <summary>設定を開く。owner を渡すとその窓の上に出す (読み取りを閉じているときなど)。</summary>
    public void OpenSettings(SettingsPage page = SettingsPage.Appearance, Window? owner = null)
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.SelectPage(page);
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        owner ??= IsVisible ? this : null; // 隠している窓は持ち主にできない
        _settingsWindow = new SettingsWindow(this, _settings) { Owner = owner };
        _settingsWindow.SelectPage(page);
        _settingsWindow.Show();
    }

    // ───────── コマンドの一覧から使う操作 ─────────

    public void ReadNow() => _ = RunOcrAsync(force: true);

    public void TogglePause() => SetPaused(!_paused);

    public void ToggleAccumulate() => SetAccumulating(!_accumulating);

    public void CopyOriginal() => CopyText(ResultBox, "原文");

    public void CopyTranslation() => CopyText(TranslationBox, "日本語訳");

    public void TogglePin()
    {
        _settings.TextTopmost = !_settings.TextTopmost;
        OnSettingChanged(nameof(AppSettings.TextTopmost));
    }

    /// <summary>並べ方を変える (上から順 / 枠・まとまりごと)。</summary>
    public void SetView(OcrView view)
    {
        if (_settings.OcrView == view) return;
        _settings.OcrView = view;
        OnSettingChanged(nameof(AppSettings.OcrView));
        SetStatus(view == OcrView.Groups ? "枠・まとまりごとに表示します" : "上から順に表示します");
        if (view == OcrView.Groups) _ = RunOcrAsync(force: true);
    }

    private void Palette_Click(object sender, RoutedEventArgs e) => AppCommands.TogglePalette(this, CommandContext.ScreenOcr);

    private void PinToggle_Click(object sender, RoutedEventArgs e) => TogglePin();

    // 「表示」「その他」のボタン: 押すとメニューを開く (メニューを閉じたらボタンの押した印を戻す)
    private void ViewButton_Click(object sender, RoutedEventArgs e) => OpenMenu(ViewButton, "ViewMenu");

    private void MoreButton_Click(object sender, RoutedEventArgs e) => OpenMenu(MoreButton, "MoreMenu");

    private void OpenMenu(System.Windows.Controls.Primitives.ToggleButton button, string key)
    {
        var menu = (ContextMenu)Resources[key];
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        button.IsChecked = true;
    }

    private void Menu_Closed(object sender, RoutedEventArgs e)
    {
        ViewButton.IsChecked = false;
        MoreButton.IsChecked = false;
    }

    private MenuItem MenuItemNamed(string menu, string name) =>
        (MenuItem)LogicalTreeHelper.FindLogicalNode((ContextMenu)Resources[menu], name);

    private void ViewMenu_Opened(object sender, RoutedEventArgs e)
    {
        MenuItemNamed("ViewMenu", "ViewTextItem").IsChecked = _settings.OcrView == OcrView.Text;
        MenuItemNamed("ViewMenu", "ViewGroupsItem").IsChecked = _settings.OcrView == OcrView.Groups;
        MenuItemNamed("ViewMenu", "ViewGroupsItem").IsEnabled = !_accumulating; // 蓄積中は上から順につなげる
        var below = MenuItemNamed("ViewMenu", "LayoutBelowItem");
        var right = MenuItemNamed("ViewMenu", "LayoutRightItem");
        below.IsChecked = _settings.Layout == PaneLayout.Below;
        right.IsChecked = _settings.Layout == PaneLayout.Right;
        below.IsEnabled = right.IsEnabled = _settings.Translate;
        MenuItemNamed("ViewMenu", "AccumulateItem").IsChecked = _accumulating;
    }

    private void ViewText_Click(object sender, RoutedEventArgs e) => SetView(OcrView.Text);

    private void ViewGroups_Click(object sender, RoutedEventArgs e) => SetView(OcrView.Groups);

    private void LayoutBelow_Click(object sender, RoutedEventArgs e)
    {
        _settings.Layout = PaneLayout.Below;
        OnSettingChanged(nameof(AppSettings.Layout));
    }

    private void LayoutRight_Click(object sender, RoutedEventArgs e)
    {
        _settings.Layout = PaneLayout.Right;
        OnSettingChanged(nameof(AppSettings.Layout));
    }

    private void AccumulateItem_Click(object sender, RoutedEventArgs e) => SetAccumulating(!_accumulating);

    private void AccumulateEnd_Click(object sender, RoutedEventArgs e) => SetAccumulating(false);

    private void Minutes_Click(object sender, RoutedEventArgs e) => OpenMinutes();

    private bool _ocrHidden;        // 今、読み取り枠とこの画面を隠しているか
    private bool _ocrClosed;        // 機能を選ぶ画面で「文字の読み取り」を閉じている
    private bool _hiddenForMinutes; // 議事録を開いている間は隠す設定で、議事録を開いている

    /// <summary>開いている機能が変わった (文字の読み取りを開いた・閉じた、議事録を開いた・閉じた)。</summary>
    public event Action? FeaturesChanged;

    /// <summary>文字の読み取りを開いているか (機能を選ぶ画面の表示用)。</summary>
    public bool IsOcrOpen => !_ocrClosed;

    /// <summary>開いている議事録の画面 (無ければ null)。</summary>
    public MinutesWindow? Minutes => _minutesWindow;

    /// <summary>議事録の状態 (記録中など) が変わった (機能を選ぶ画面の表示を変える)。</summary>
    internal void MinutesStateChanged() => FeaturesChanged?.Invoke();

    /// <summary>議事録の画面で表示や文字の大きさを変えたとき、開いている設定の画面にも反映する。</summary>
    public void MinutesDisplayChanged() => _settingsWindow?.LoadMinutesDisplay();

    /// <summary>議事録だけを使うときに、読み取り枠とこの画面を隠して読み取りを止める。</summary>
    public void SetOcrHidden(bool hidden)
    {
        _hiddenForMinutes = hidden;
        ApplyOcrVisibility();
    }

    /// <summary>機能を選ぶ画面から使う: 最初は読み取りを閉じておく (窓は作っておき、どのアプリでも効くキーは登録する)。</summary>
    public void PrepareForLauncher()
    {
        _ocrClosed = true;
        _ocrHidden = true;
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>文字の読み取りを開く (読み取り枠とこの画面を出して読み取りを始める)。</summary>
    public void OpenOcr()
    {
        _ocrClosed = false;
        _hiddenForMinutes = false; // 自分で開いたときは、議事録の間に隠す設定より優先する
        ApplyOcrVisibility();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>文字の読み取りを閉じる (隠して読み取りを止める。GetText は終わらない)。</summary>
    public void CloseOcr()
    {
        _ocrClosed = true;
        ApplyOcrVisibility();
    }

    /// <summary>読み取りの画面 (枠) を隠した・閉じた (画面に重ねた訳などを消す)。</summary>
    public event Action? OcrHidden;

    /// <summary>読み取りの画面 (枠) が出ているか (隠れている間は、画面に文字を重ねない)。</summary>
    public bool IsOcrVisible => !_ocrClosed && !_hiddenForMinutes;

    private void ApplyOcrVisibility()
    {
        bool hidden = _ocrClosed || _hiddenForMinutes;
        if (hidden) OcrHidden?.Invoke();
        if (_ocrHidden != hidden && !_shutDown)
        {
            _ocrHidden = hidden;
            if (hidden)
            {
                _capture.Hide();
                Hide();
            }
            else
            {
                _capture.Show();
                Show();
                _lastPixels = null; // 戻したらすぐ読み直す
            }
        }
        FeaturesChanged?.Invoke();
    }

    /// <summary>議事録を開く (開いていれば前に出す)。</summary>
    public MinutesWindow OpenMinutes()
    {
        if (_minutesWindow is { IsLoaded: true })
        {
            if (_minutesWindow.WindowState == WindowState.Minimized) _minutesWindow.WindowState = WindowState.Normal;
            _minutesWindow.Activate();
            return _minutesWindow;
        }
        _minutesWindow = new MinutesWindow(this, _settings);
        _minutesWindow.Closed += (_, _) =>
        {
            _minutesWindow = null;
            FeaturesChanged?.Invoke();
        };
        _minutesWindow.Show();
        FeaturesChanged?.Invoke();
        return _minutesWindow;
    }

    private bool _exitRequested;

    /// <summary>GetText を終える (機能を選ぶ画面の「終了」)。minutesConfirmed なら、議事録を止めてよいかはもう聞いてある。</summary>
    public void RequestExit(bool minutesConfirmed = false)
    {
        _exitRequested = true;
        _minutesConfirmed = minutesConfirmed;
        Close();
        if (!_shutDown && !ClosingAfterStop) _exitRequested = _minutesConfirmed = false; // 「止めない」を選んだ
    }

    private bool _minutesConfirmed;

    /// <summary>蓄積 (スクロールしながら読んだ内容を重複なしでつなげる) を始める / 終える。蓄積中は帯を出す。</summary>
    private void SetAccumulating(bool on)
    {
        _accumulating = on;
        _accumulator.Clear();
        AccumulateBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateDocument(fromNewFrame: on); // 今の画面から集め始める
        UpdateAccumulateBar();
        SetStatus(on ? "蓄積中: スクロールすると読んだ内容を重複なしで追記します" + AccumulateStatus : "蓄積を終了しました");
    }

    private void UpdateAccumulateBar()
    {
        if (_accumulating) AccumulateText.Text = $"蓄積中 ・ {_accumulator.LineCount} 行: スクロールすると、読んだ内容を重複なしで追記します";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _accumulator.Clear();
        _lastPixels = null; // すぐに今の画面から集め直す
        _doc = _accumulator.ToDocument();
        _units = [];
        RenderOcr();
        RenderTranslation();
        SetStatus("蓄積した内容を消しました");
    }

    private void CopyOcr_Click(object sender, RoutedEventArgs e) => CopyText(ResultBox, "原文");

    private void CopyTranslation_Click(object sender, RoutedEventArgs e) => CopyText(TranslationBox, "日本語訳");

    private void CopyText(TextBox box, string name, bool all = false)
    {
        bool selection = !all && box.SelectionLength > 0;
        var text = selection ? box.SelectedText : box.Text;
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("コピーするテキストがありません");
            return;
        }

        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetText(text);
                SetStatus($"{name}の{(selection ? "選択範囲" : "全文")}をコピーしました ({text.Length} 文字)");
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(50); // クリップボードが他アプリに使用中
            }
        }
        SetStatus("クリップボードにアクセスできませんでした");
    }

    private void SaveOcr_Click(object sender, RoutedEventArgs e) => SaveText(ResultBox.Text, "原文");

    private void SaveTranslation_Click(object sender, RoutedEventArgs e) => SaveText(TranslationBox.Text, "日本語訳");

    private void SaveText(string text, string name)
    {
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("保存するテキストがありません");
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"GetText_{name}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            Filter = "テキスト ファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, text, new UTF8Encoding(false));
            SetStatus($"{name}を保存しました: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus("保存できませんでした: " + ex.Message);
        }
    }

    // ───────── 右クリックメニュー ─────────

    // リソース内のメニュー項目はフィールドにならないので名前で探す
    private MenuItem ConvertMenu =>
        (MenuItem)LogicalTreeHelper.FindLogicalNode((ContextMenu)Resources["PaneMenu"], "ConvertMenu");

    private void BuildConvertMenu()
    {
        foreach (var option in SettingsOptions.ConvertModes)
        {
            var item = new MenuItem { Header = option.Label, IsCheckable = true, Tag = option.Value };
            item.Click += (_, _) =>
            {
                _settings.Convert = option.Value;
                OnSettingChanged(nameof(AppSettings.Convert));
            };
            ConvertMenu.Items.Add(item);
        }
    }

    private void PaneMenu_Opened(object sender, RoutedEventArgs e)
    {
        foreach (MenuItem item in ConvertMenu.Items)
            item.IsChecked = (ConvertMode)item.Tag == _settings.Convert;
    }

    private TextBox? MenuTarget(object sender) =>
        ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget as TextBox;

    private void MenuCopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } box) CopyText(box, box == ResultBox ? "原文" : "日本語訳", all: true);
    }

    private void MenuSave_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } box) SaveText(box.Text, box == ResultBox ? "原文" : "日本語訳");
    }

    private void Box_SelectionChanged(object sender, RoutedEventArgs e)
    {
        _ocrPane.FlushIfFree();
        _translationPane.FlushIfFree();
    }

    // Ctrl+ホイールで文字サイズを変える (両方の欄をそろえる)
    private void Box_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        SetFontSize(ResultBox.FontSize + (e.Delta > 0 ? 1 : -1));
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (AppCommands.HandleKey(this, e, CommandContext.ScreenOcr)) return;
        if (e.Key == Key.F5)
            _ = RunOcrAsync(force: true);
        else if (e.Key == Key.F && mods == ModifierKeys.Control)
            ShowSearch();
        else if (e.Key == Key.F3 && SearchBar.IsVisible)
            MoveMatch(mods == ModifierKeys.Shift ? -1 : 1);
        else if (e.Key == Key.Escape && SearchBar.IsVisible)
            CloseSearch();
        else if (e.Key == Key.P && mods == ModifierKeys.Control)
            SetPaused(!_paused);
        else if (e.Key == Key.OemComma && mods == ModifierKeys.Control)
            OpenSettings(SettingsPage.ScreenOcr);
        else if (e.Key == Key.C && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            CopyText(ResultBox, "原文");
        else if (e.Key == Key.T && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            CopyText(TranslationBox, "日本語訳");
        else
            return;
        e.Handled = true;
    }

    // ───────── 検索 (Ctrl+F) ─────────

    private SearchHighlightAdorner? _highlights;
    private List<TextMatch> _matches = [];
    private int _activeMatch = -1;

    /// <summary>検索の欄を出して、入力できるようにする。</summary>
    public void ShowSearch()
    {
        SearchBar.Visibility = Visibility.Visible;
        SearchToggle.IsChecked = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
        RefreshSearch();
    }

    private void CloseSearch()
    {
        SearchBar.Visibility = Visibility.Collapsed;
        SearchToggle.IsChecked = false;
        _matches = [];
        _activeMatch = -1;
        _highlights?.SetMatches([], -1);
        _capture.SetHighlights([], -1);
        ResultBox.Focus();
    }

    private void SearchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SearchToggle.IsChecked == true) ShowSearch();
        else CloseSearch();
    }

    private void SearchClose_Click(object sender, RoutedEventArgs e) => CloseSearch();

    private void SearchNext_Click(object sender, RoutedEventArgs e) => MoveMatch(1);

    private void SearchPrev_Click(object sender, RoutedEventArgs e) => MoveMatch(-1);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _activeMatch = -1;
        RefreshSearch();
        if (_matches.Count > 0) MoveMatch(0);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            MoveMatch(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 表示している原文から探し直す (読み取りが更新されたときも)。今の一致は、前と同じ位置の近くのものを選び続ける
    /// (更新のたびに 1 件目に戻らない)。
    /// </summary>
    private void RefreshSearch()
    {
        if (SearchBar.Visibility != Visibility.Visible) return;
        int previousStart = _activeMatch >= 0 && _activeMatch < _matches.Count ? _matches[_activeMatch].Start : -1;
        _matches = TextSearch.Find(ResultBox.Text, SearchBox.Text);
        if (_matches.Count == 0) _activeMatch = -1;
        else if (previousStart >= 0)
            _activeMatch = _matches.Select((m, i) => (Distance: Math.Abs(m.Start - previousStart), i)).MinBy(x => x.Distance).i;
        else if (_activeMatch >= _matches.Count) _activeMatch = _matches.Count - 1;
        ShowMatches();
    }

    private void MoveMatch(int step)
    {
        if (_matches.Count == 0)
        {
            ShowMatches();
            return;
        }
        _activeMatch = _activeMatch < 0 ? (step < 0 ? _matches.Count - 1 : 0) : ((_activeMatch + step) % _matches.Count + _matches.Count) % _matches.Count;
        ShowMatches();
        // 今の一致が見えるようにスクロールする (選択はしない: 選択中は読み取りの更新を止めてしまうため)
        var match = _matches[_activeMatch];
        int line = ResultBox.GetLineIndexFromCharacterIndex(Math.Min(match.Start, Math.Max(0, ResultBox.Text.Length - 1)));
        if (line >= 0)
        {
            int first = ResultBox.GetFirstVisibleLineIndex(), last = ResultBox.GetLastVisibleLineIndex();
            if (line < first || line > last) ResultBox.ScrollToLine(line);
        }
    }

    private void ShowMatches()
    {
        bool hasQuery = !string.IsNullOrWhiteSpace(SearchBox.Text);
        SearchCount.Text = !hasQuery ? "" : _matches.Count == 0 ? "0 件" : $"{(_activeMatch >= 0 ? _activeMatch + 1 : 0)}/{_matches.Count}";
        SearchCount.Foreground = (System.Windows.Media.Brush)FindResource(hasQuery && _matches.Count == 0 ? "Gt.Critical" : "Gt.TextSecondary");
        SearchPrevButton.IsEnabled = SearchNextButton.IsEnabled = _matches.Count > 0;
        _highlights?.SetMatches(_matches, _activeMatch);
        UpdateCaptureHighlights();
    }

    /// <summary>読み取り枠の上にも、見つかった所の印を付ける (今の一致は太い枠)。</summary>
    private void UpdateCaptureHighlights()
    {
        if (_doc == null || _matches.Count == 0 || SearchBar.Visibility != Visibility.Visible || Accumulating)
        {
            _capture.SetHighlights([], -1);
            return;
        }
        // 文字変換で行の数が変わったとき (改行をつなぐなど) は、位置が合わないので枠の上には出さない
        int shownLines = ResultBox.Text.Split('\n').Count(l => l.TrimEnd('\r').Length > 0);
        if (shownLines != _doc.Lines.Count())
        {
            _capture.SetHighlights([], -1);
            return;
        }
        _capture.SetHighlights(TextSearch.ToBoxes(_doc, ResultBox.Text, _matches), _activeMatch);
    }

    // ───────── 知らせの帯 ─────────

    private bool _fallbackDismissed;

    /// <summary>AI OCR の準備中の帯 (その間も Windows OCR で読み取る)。</summary>
    private void ShowLoading(bool loading)
    {
        LoadingBar.Visibility = loading && !App.DemoMode ? Visibility.Visible : Visibility.Collapsed;
        LoadingDetail.Text = _engine != null
            ? "認識モデルを読み込んでいます。その間は Windows OCR で読み取ります。"
            : "認識モデルを読み込んでいます。終わると読み取りを始めます。";
        UpdateFallbackBar();
    }

    /// <summary>AI OCR を使えず Windows OCR で読んでいるときの帯 (閉じたら、次に使えなくなるまで出さない)。</summary>
    private void UpdateFallbackBar()
    {
        bool fallback = UseAiOcr && _aiOcrError != null;
        if (!fallback) _fallbackDismissed = false;
        FallbackBar.Visibility = fallback && !_fallbackDismissed && !App.DemoMode ? Visibility.Visible : Visibility.Collapsed;
        FallbackText.Text = _aiOcrError ?? "";
    }

    private void FallbackClose_Click(object sender, RoutedEventArgs e)
    {
        _fallbackDismissed = true;
        UpdateFallbackBar();
    }

    private void FallbackDetails_Click(object sender, RoutedEventArgs e)
    {
        FallbackText.Visibility = FallbackText.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        FallbackDetails.Content = FallbackText.Visibility == Visibility.Visible ? "閉じる" : "詳細";
    }

    // ───────── セットアップの案内 ─────────

    /// <summary>AI の機能が入っていなければ、欄の上に案内を出す (閉じたら次からは出さない)。</summary>
    public void UpdateSetupHint() =>
        SetupHint.Visibility = !App.DemoMode && !AiOcr.IsInstalled && !_settings.SetupHintDismissed ? Visibility.Visible : Visibility.Collapsed;

    private void SetupHintOpen_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsPage.Models);

    private void HotkeyHintOpen_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsPage.Shortcuts);

    private void HotkeyHintClose_Click(object sender, RoutedEventArgs e) => HotkeyHint.Visibility = Visibility.Collapsed;

    private void SetupHintClose_Click(object sender, RoutedEventArgs e)
    {
        _settings.SetupHintDismissed = true;
        _settings.Save();
        UpdateSetupHint();
    }

    // 幅が狭いときは操作バーの文字を隠してアイコンだけにする (ボタンが切れて押せなくならないように。名前はマウスを乗せると出る)
    private void FitToolbar()
    {
        bool compact = ActualWidth < 500;
        foreach (var label in FindLabels(Toolbar))
            label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
    private static IEnumerable<TextBlock> FindLabels(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { Tag: "Label" } label) yield return label;
            foreach (var nested in FindLabels(child)) yield return nested;
        }
    }

    // ───────── グローバルホットキー ─────────

    /// <summary>設定でキーを変えたときに登録し直す。</summary>
    public void ReregisterHotkeys()
    {
        if (!App.DemoMode) RegisterHotkeys();
    }

    /// <summary>他のアプリが使っていて登録できなかったホットキー (設定のショートカットの一覧に出す)。</summary>
    public IReadOnlyList<string> FailedHotkeys { get; private set; } = [];

    private void RegisterHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = new GlobalHotkeys(this);
        var failed = new List<string>();
        var actions = new Dictionary<string, Action>
        {
            ["copy-ocr"] = () => { CopyText(ResultBox, "原文"); _capture.Flash(StatusText.Text); },
            ["copy-translation"] = () => { CopyText(TranslationBox, "日本語訳"); _capture.Flash(StatusText.Text); },
            ["refresh"] = () => _ = RunOcrAsync(force: true),
            ["pause"] = () => SetPaused(!_paused),
            ["quick-ocr"] = QuickOcr.Run,
        };
        foreach (var (id, _, _) in HotkeyText.Actions)
        {
            var keys = HotkeyText.For(_settings, id);
            if (!HotkeyText.TryParse(keys, out var modifiers, out var key) || !_hotkeys.Register(modifiers, key, actions[id])) failed.Add(keys);
        }
        FailedHotkeys = failed;
        // 使えないキーは、閉じるまで消えない案内で知らせる (状態欄や欄の注意書きはすぐ書き換わるため)
        HotkeyHintText.Text = $"どのアプリでも効くキー {string.Join("、", failed.Select(HotkeyText.Display))} は、ほかのアプリが使っているため使えません。";
        HotkeyHint.Visibility = failed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PauseToggle.ToolTip = $"自動読み取りを一時停止 / 再開 (Ctrl+P ・ どこからでも {HotkeyText.For(_settings, "pause")})";
        RefreshButton.ToolTip = $"枠の中を今すぐ読み取る (F5 ・ どこからでも {HotkeyText.For(_settings, "refresh")})";
        CopyOcrButton.ToolTip = $"コピー: 選択範囲があればその部分、なければ全文 (Ctrl+Shift+C ・ どこからでも {HotkeyText.For(_settings, "copy-ocr")})";
        CopyTranslationButton.ToolTip = $"コピー: 選択範囲があればその部分、なければ全文 (Ctrl+Shift+T ・ どこからでも {HotkeyText.For(_settings, "copy-translation")})";
    }

    // ───────── ウィンドウ ─────────

    /// <summary>枠の隣に移動する。画面の右端などで入らないときは左や下に回り、画面の外には出さない。</summary>
    private void FollowCapture()
    {
        // 窓ができていれば物理ピクセルで計算する (枠と文字の画面が拡大率の違うモニターにあっても、正しく隣に置ける)
        if (ScreenUtil.PixelBounds(_capture) is { } frame && ScreenUtil.PixelBounds(this) is not null)
        {
            var (work, scale) = ScreenUtil.MonitorAt(frame);
            var pixels = new Size((ActualWidth > 0 ? ActualWidth : Width) * scale, (ActualHeight > 0 ? ActualHeight : Height) * scale);
            ScreenUtil.MoveTo(this, ScreenUtil.Beside(frame, pixels, FollowGap * scale, work));
            return;
        }
        var anchor = new Rect(_capture.Left, _capture.Top,
            _capture.ActualWidth > 0 ? _capture.ActualWidth : _capture.Width,
            _capture.ActualHeight > 0 ? _capture.ActualHeight : _capture.Height);
        var size = new Size(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        var p = ScreenUtil.Beside(anchor, size, FollowGap, ScreenUtil.WorkArea(anchor, _capture));
        Left = p.X;
        Top = p.Y;
    }

    private bool _shutDown;
    /// <summary>最大化・最小化する前の位置 (物理ピクセル)。閉じるときに保存する。</summary>
    private int[]? _normalPixel;
    private readonly DispatcherTimer _displayDelay = new() { Interval = TimeSpan.FromSeconds(1.5) };

    private const int WM_SETTINGCHANGE = 0x001A, WM_DISPLAYCHANGE = 0x007E, SPI_SETWORKAREA = 0x002F;

    // モニターの抜き差し・解像度や拡大率の変更・タスクバーの移動を知る
    private IntPtr DisplayHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DISPLAYCHANGE || (msg == WM_SETTINGCHANGE && wParam.ToInt32() == SPI_SETWORKAREA))
        {
            // Windows が窓を並べ直し終わってから確かめる (変更の通知は続けて何度か来る)
            _displayDelay.Stop();
            _displayDelay.Start();
        }
        return IntPtr.Zero;
    }

    /// <summary>モニターの構成が変わったとき、つかめない場所 (外したモニターの上など) に残った窓を画面の中に戻す。</summary>
    internal void OnDisplayChanged()
    {
        if (_shutDown) return;
        ScreenUtil.EnsureVisible(_capture);
        if (_settings.Follow) FollowCapture();
        else ScreenUtil.EnsureVisible(this);
        if (_minutesWindow != null) ScreenUtil.EnsureVisible(_minutesWindow);
    }

    /// <summary>枠と文字の画面の位置・大きさを設定に書く (保存はしない)。</summary>
    internal void StorePlacement()
    {
        // 機能を選ぶ画面から議事録・録画だけを使ったときは、枠も文字の画面も一度も出ていない (大きさが 0) ので、前の位置と大きさのままにする
        if (_capture.IsLoaded)
        {
            _settings.CaptureLeft = _capture.Left;
            _settings.CaptureTop = _capture.Top;
            _settings.CaptureWidth = _capture.ActualWidth;
            _settings.CaptureHeight = _capture.ActualHeight;
            _settings.CapturePixel = ScreenUtil.PixelPosition(_capture) ?? _settings.CapturePixel;
        }
        if (!IsLoaded) return;

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        _settings.TextLeft = bounds.Left;
        _settings.TextTop = bounds.Top;
        _settings.TextWidth = bounds.Width;
        _settings.TextHeight = bounds.Height;
        _settings.TextPixel = ScreenUtil.PixelPosition(this) ?? _normalPixel ?? _settings.TextPixel;
    }

    /// <summary>Windows のログオフ・シャットダウンのとき (窓の Closing が来ないことがある) にも、設定と議事録を保存する。</summary>
    public void SaveOnSessionEnd()
    {
        if (_shutDown) return;
        _sessionEnding = true;
        OnClosing(this, new CancelEventArgs());
    }

    private bool _sessionEnding;

    /// <summary>議事録の残りの音声を文字にし終えてから閉じる途中。</summary>
    public bool ClosingAfterStop { get; private set; }

    private async void CloseAfterStopped(MinutesWindow minutes)
    {
        ClosingAfterStop = true;
        minutes.SetStatus("記録の残り・プロジェクトの保存が終わったら GetText を終了します…");
        await minutes.WaitPendingAsync();
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutDown) return;
        // 機能を選ぶ画面から使っているときは、この画面・読み取り枠の ✕ は「文字の読み取りを閉じる」 (GetText は終わらない)
        if (App.Home != null && !_exitRequested && !_sessionEnding)
        {
            e.Cancel = true;
            CloseOcr();
            return;
        }
        // 議事録を記録中・文字起こし中なら、止めてよいか確かめる (Windows の終了のときは聞かない)
        if (!_sessionEnding && !_minutesConfirmed && _minutesWindow is { IsBusy: true } minutes && !minutes.ConfirmStop("、GetText を終了します"))
        {
            e.Cancel = true;
            return;
        }
        // 議事録の記録を止めて残りの音声を文字にしている途中なら、終わってから終了する (最後の発言を失わない)
        if (!_sessionEnding && _minutesWindow is { HasPendingWork: true } stopping)
        {
            e.Cancel = true;
            CloseAfterStopped(stopping);
            return;
        }
        _shutDown = true;
        _timer.Stop();
        _translateCts.Cancel();
        _hotkeys?.Dispose();
        _minutesWindow?.ShutdownNow();
        _translator.Shutdown();
        _aiOcr.Dispose();

        _displayDelay.Stop();
        StorePlacement();
        _settings.Save();

        _capture.AllowClose = true;
        _capture.Close();
    }
}
