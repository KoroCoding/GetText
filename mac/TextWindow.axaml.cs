using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GetText.Plugins;

namespace GetText;

/// <summary>読み取った原文と日本語訳を表示する画面 (Mac 版)。枠の中の文字を Vision (または AI) で読む。</summary>
public partial class TextWindow : Window, IMinutesHost
{
    /// <summary>テキスト選択中は書き換えを保留し、選択解除時に反映する表示欄。空のときは案内文を出す。</summary>
    private sealed class Pane(TextBox box, Control placeholder)
    {
        public TextBox Box { get; } = box;
        public string? Pending { get; private set; }
        public DateTime LeftAt { get; set; } = DateTime.MinValue;
        private static readonly TimeSpan HoldAfterLeave = TimeSpan.FromSeconds(30);

        private bool HasSelection => Box.SelectionStart != Box.SelectionEnd;

        public bool Set(string text)
        {
            if (text == (Box.Text ?? ""))
            {
                Pending = null;
                return true;
            }
            if (HasSelection && (Box.IsFocused || DateTime.Now - LeftAt < HoldAfterLeave))
            {
                Pending = text;
                return false;
            }
            Apply(text);
            return true;
        }

        public void FlushIfFree()
        {
            if (Pending != null && !HasSelection) Apply(Pending);
        }

        /// <summary>表示を書き換えた (検索の印を付け直す)。</summary>
        public event Action? Applied;

        private void Apply(string text)
        {
            Box.Text = text;
            Pending = null;
            placeholder.IsVisible = text.Length == 0;
            Applied?.Invoke();
        }
    }

    private const double FollowGap = 8;

    private readonly CaptureWindow _capture;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new();
    private readonly AiOcr _aiOcr = new();
    private readonly Translator _translator = new();
    private readonly TextAccumulator _accumulator = new();
    private readonly Pane _ocrPane;
    private readonly Pane _translationPane;
    private SettingsWindow? _settingsWindow;
    private MinutesWindow? _minutesWindow;

    private bool _paused;
    private bool _busy;
    private bool _forceNext = true;
    private bool _aiOcrReady;
    private string? _aiOcrError;
    private DateTime _aiOcrErrorAt;
    private int _aiOcrFailures;
    private int _lastCaptureHeight;
    private IReadOnlyList<OcrLineData>? _lastLines;
    /// <summary>直前に読み取った画面の枠線 (枠・まとまりごとに表示するときだけ探す)。</summary>
    private IReadOnlyList<LayoutSegment> _lastSegments = [];
    private OcrGrouping _grouping = OcrGrouping.None;
    /// <summary>読み取った画像の 1 ポイントあたりのピクセル数 (枠の上に検索の印を描くとき)。</summary>
    private double _lastPixelScale = 2;
    private Task? _aiWarmup;
    private long _lastOcrMs;
    private bool _accumulating;
    private bool _lastFromAi;
    private OcrDocument? _doc;
    /// <summary>翻訳の単位 (範囲つき) と種類。段落ごと。</summary>
    private List<List<(TranslationUnit Unit, ScriptKind Kind)>> _units = [];
    /// <summary>直前に読み取った範囲の左上 (画面のピクセル) と、画像のピクセル → 画面のピクセルの倍率 (訳を重ねる位置の基準)。</summary>
    private (double X, double Y, double ToScreen)? _readOrigin;
    /// <summary>枠を動かした・大きさを変えた回数 (読み取りの途中で動かしたら、その回の位置は使わない)。</summary>
    private int _frameMoves;
    private const string OverlayOwner = "reading.translation";
    /// <summary>読み取った位置が今の画面と合わない (枠を動かした後、まだ読み直していない)。枠の上の印を出さない。</summary>
    private bool _positionsStale;
    /// <summary>読み取ったときの枠の位置と大きさ (同じ位置への知らせは「動かした」としない)。</summary>
    private (PixelPoint Position, Size Size)? _readFrame;
    private const string HoldNote = "選択中のため更新を保留中";

    private bool _translating;
    private DateTime _nextTranslateAt = DateTime.MinValue;
    private CancellationTokenSource _translateCts = new();
    private string? _translateError;

    public AiOcr AiOcr => _aiOcr;
    public Translator Translator => _translator;

    /// <summary>画面に文字を重ねる部品 (訳を画面に重ねる・拡張機能)。読み取りの画面を隠すと消える。</summary>
    public MacOverlayService Overlay { get; }

    /// <summary>利用者が選んだ読み取りの方式 (拡張機能・Quick OCR が方式を指定しないときに使う)。</summary>
    public string PreferredOcrProvider => UseAiOcr && _aiOcrError == null ? "ai" : "vision";

    /// <summary>設定 (開発者向けの API の入・切など、窓の外から読む)。</summary>
    public AppSettings Settings => _settings;

    /// <summary>開発者向けの API: 表示している原文と訳 (UI のスレッドで読む)。</summary>
    public (bool Open, string Text, string? Translation) ReadingTextForApi() =>
        Dispatcher.UIThread.Invoke(() => (IsOcrOpen, ResultBox.Text ?? "", _settings.Translate ? TranslationBox.Text : null));

    public TextWindow() : this(new CaptureWindow(), new AppSettings()) { }

    public TextWindow(CaptureWindow capture, AppSettings settings)
    {
        InitializeComponent();
        _capture = capture;
        _settings = settings;
        _ocrPane = new Pane(ResultBox, OcrPlaceholder);
        _translationPane = new Pane(TranslationBox, TranslationPlaceholder);
        // 読み取りの画面を隠している・しまっている間は、画面に何も重ねない (遅れて届いた分も)
        Overlay = new MacOverlayService(() => IsOcrVisible && WindowState != WindowState.Minimized);
        _ocrPane.Applied += RefreshSearch;
        _ocrPane.Applied += () => { if (OcrNote.Text == HoldNote) OcrNote.Text = ""; };
        _translationPane.Applied += () => { if (TranslationNote.Text == HoldNote) TranslationNote.Text = ""; };
        SearchBox.TextChanged += (_, _) => SearchBox_TextChanged();
        SearchBox.AddHandler(KeyDownEvent, SearchBox_KeyDown, RoutingStrategies.Tunnel);
        ResultBox.SizeChanged += (_, _) => DrawTextHighlights();
        ResultBox.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => DrawTextHighlights());
        SizeChanged += (_, _) => FitToolbar();
        ActualThemeVariantChanged += (_, _) => { DrawTextHighlights(); UpdateCaptureHighlights(); };
        foreach (var pane in new[] { _ocrPane, _translationPane })
        {
            var pn = pane;
            pn.Box.LostFocus += (_, _) => pn.LeftAt = DateTime.Now;
            pn.Box.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty)
                {
                    _ocrPane?.FlushIfFree();
                    _translationPane?.FlushIfFree();
                }
            };
            pn.Box.ContextRequested += Pane_ContextRequested;
            pn.Box.AddHandler(PointerWheelChangedEvent, Box_PointerWheel, RoutingStrategies.Tunnel);
        }

        Width = Math.Max(MinWidth, settings.TextWidth);
        Height = Math.Max(MinHeight, settings.TextHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (!settings.Follow && settings.TextPixel is [var x, var y]) Position = new PixelPoint(x, y);
        Opened += (_, _) =>
        {
            if (_settings.Follow) FollowCapture();
            else WindowPlacement.EnsureVisible(this);
        };

        ApplyAllSettings();

        _capture.PauseRequested += (_, _) => SetPaused(!_paused);
        _capture.CloseRequested += (_, _) => Close();
        _capture.PositionChanged += (_, _) =>
        {
            if (_settings.Follow) FollowCapture();
            ForgetReadPositions();
        };
        _capture.SizeChanged += (_, _) =>
        {
            if (_settings.Follow) FollowCapture();
            _forceNext = true; // 大きさが変わったら次回は必ず読み直す
            ForgetReadPositions();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                _capture.IsVisible = WindowState != WindowState.Minimized && !_ocrHidden;
                if (WindowState == WindowState.Minimized) Overlay.ClearAll(); // (拡張機能が重ねた文字も消す)
                else UpdateOverlay();
            }
        };
        Closing += OnClosing;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        if (App.DemoMode) return; // 見本の画面: 読み取りもショートカットも使わない
        Opened += (_, _) => _ = RegisterHotkeysAsync();
        MacHelper.Instance.EventReceived += OnHelperEvent;

        _timer.Tick += async (_, _) => await RunOcrAsync(force: false);
        _timer.Start();
        if (!MacHelper.IsAvailable)
            SetStatus("Mac の補助プログラムが見つからないため、画面の文字を読み取れません (GetText.app を入れ直してください)");
    }

    // ───────── 設定の反映 ─────────

    private void ApplyAllSettings()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(300, _settings.IntervalMs));
        Topmost = _settings.TextTopmost;
        PinToggle.IsChecked = _settings.TextTopmost;
        ApplyWrap();
        SetFontSize(_settings.FontSize);
        _translator.Engine = _settings.TranslationEngine;
        _ = LoadDeepLKeyAsync(); // キーチェーンの確認の画面が出ても、起動が止まらないように
        ApplyOcrEngineState();
        ApplyLayout();
        if (_settings.Translate && !App.DemoMode) _translator.WarmUp();
        UpdateChips();
    }

    /// <summary>見本の画面 (画面の画像用): 読み取り結果と訳を表示する。</summary>
    internal void ShowDemo(string ocr, string translation, string ocrChip, string translationChip, string status)
    {
        _ocrPane.Set(ocr);
        _translationPane.Set(translation);
        OcrChip.Text = ocrChip;
        TranslationChip.Text = translationChip;
        StatusText.Text = status;
    }

    /// <summary>設定画面で値が変わったときに呼ばれる。</summary>
    public void OnSettingChanged(string name)
    {
        switch (name)
        {
            case nameof(AppSettings.OcrEngine):
                ApplyOcrEngineState();
                _forceNext = true;
                break;
            case nameof(AppSettings.JoinCjk):
                UpdateDocument();
                break;
            case nameof(AppSettings.IntervalMs):
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(300, _settings.IntervalMs));
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
                if (_settings.OcrView == OcrView.Groups && _lastSegments.Count == 0) _forceNext = true; // 枠線を探すため、すぐに読み直す
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
                _ = LoadDeepLKeyAsync();
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
        UpdateOverlay(); // (翻訳・重ねる・文字変換を変えたとき)
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
        TranslationCard.IsVisible = Splitter.IsVisible = translate;
        TranslationChipBorder.IsVisible = translate;
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

    private static void Place(Control e, int row, int col, int rowSpan, int colSpan)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        Grid.SetRowSpan(e, rowSpan);
        Grid.SetColumnSpan(e, colSpan);
    }

    /// <summary>
    /// 下の右の小さな表示: 読み取りの方式と時間 (「AI OCR · 48 ms」)、翻訳の場所 (Mac 内 / オンライン)。詳しいことはマウスを乗せると出る。
    /// </summary>
    private void UpdateChips()
    {
        bool ai = UseAiOcr && _aiOcrError == null && _aiOcrReady;
        string name = ai ? "AI OCR" : "Mac の文字認識";
        OcrChip.Text = _lastOcrMs > 0 ? $"{name} · {_lastOcrMs} ms" : name;
        ToolTip.SetTip(OcrChip, ai
            ? $"AI OCR (RapidOCR + PP-OCRv6)\n{(_aiOcr.Device switch { "gpu" => "GPU", "cpu" => "CPU", _ => "準備中" })}\n確からしさ {_aiOcr.LastConfidence:P0}"
            : UseAiOcr ? "Mac の文字認識 (Vision。AI OCR を準備中・使えないため)" : "Mac の文字認識 (Vision)");
        bool local = _translator.Engine == TranslationEngine.Local;
        TranslationChip.Text = (local ? "翻訳: Mac 内" : $"翻訳: {(_translator.Engine == TranslationEngine.DeepL ? "DeepL" : "Google")} (オンライン)")
                               + (OverlayOn ? " ・ 画面に重ねる" : "");
        TranslationPlaceIcon.Icon = local ? AppIcon.Local : AppIcon.Cloud;
        ToolTip.SetTip(TranslationChipBorder, local
            ? $"{_translator.EngineName}\n文字は Mac の外に送りません"
            : $"{_translator.EngineName}\n外国語の文をインターネットに送って訳します");
        UpdateFallbackBar();
    }

    // ───────── 読み取り ─────────

    // AI OCR は入っていて選ばれているときだけ使う (入っていなければ Mac の文字認識 = Vision)
    private bool UseAiOcr => _settings.OcrEngine == OcrEngineKind.Ai && AiOcr.IsInstalled;

    private void ApplyOcrEngineState()
    {
        _aiOcrError = null;
        _aiOcrReady = false;
        _aiWarmup = null; // (前の準備が終わったままだと、準備をし直さずに読み取りが待たされる)
        _aiOcrFailures = 0;
        if (!UseAiOcr) _aiOcr.Release(); // (AI に戻したら起動し直す)
        if (UseAiOcr && !App.DemoMode)
            _ = _aiOcr.EnsureStartedAsync().ContinueWith(t =>
            {
                _ = t.Exception;
                Dispatcher.UIThread.Post(UpdateChips);
            }, TaskScheduler.Default);
    }

    private async Task RunOcrAsync(bool force)
    {
        if (_busy && force) _forceNext = true; // (読み取りの途中に押された: 終わったらすぐ読み直す)
        if (_busy || _ocrHidden || !MacHelper.IsAvailable) return;
        if (_paused && !force) return;
        if (WindowState == WindowState.Minimized && !force) return;
        var number = _capture.WindowNumber;
        var size = _capture.CaptureSize;
        if (number == 0 || size.Width < 4 || size.Height < 4) return;

        _busy = true;
        force |= _forceNext;
        _forceNext = false;
        int moves = _frameMoves;
        var origin = CaptureOrigin();
        try
        {
            var sw = Stopwatch.StartNew();
            if (UseAiOcr && _aiOcrError != null && DateTime.Now - _aiOcrErrorAt > TimeSpan.FromSeconds(60))
            {
                _aiOcrError = null;
                _aiOcrReady = false;
                _aiWarmup = null;
            }
            double scale = Math.Max(2, _capture.DesktopScaling);
            var args = new JsonObject
            {
                ["window"] = number,
                ["inset"] = new JsonArray(CaptureWindow.Inset.Select(v => (JsonNode)v).ToArray()),
                ["scale"] = scale,
                ["force"] = force,
                ["languages"] = new JsonArray("ja-JP", "en-US", "zh-Hans", "ko-KR"),
            };
            // AI OCR の準備中は待たせない: 裏で準備しながら Mac の文字認識で読み取る
            bool useAi = UseAiOcr && _aiOcrError == null;
            if (useAi && !_aiOcrReady)
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
                // 画面の取り込みは補助プログラムの仕事: 失敗しても (許可が無い・応答しない) AI OCR の失敗にしない (外で知らせ、AI OCR は止めない)
                var shot = await MacHelper.Instance.RequestAsync("capture", args, TimeSpan.FromSeconds(10));
                try
                {
                    // AI OCR: 画像を受け取って Python の RapidOCR で読む (変化は C# 側で判断する)
                    var pixels = Convert.FromBase64String(shot["data"]!.GetValue<string>());
                    int w = shot["w"]!.GetValue<int>(), h = shot["h"]!.GetValue<int>();
                    if (!force && _lastPixels != null && !Changed(_lastPixels, pixels))
                    {
                        // 画面が止まったら、確からしさの低い (小さい・ぼやけた) 文字を高精度の認識モデルで 1 回だけ読み直す
                        if (_accuratePending && _aiOcrReady && _aiOcr.AccurateAvailable)
                        {
                            _accuratePending = false;
                            var swa = Stopwatch.StartNew();
                            _lastLines = await _aiOcr.RecognizeAsync(pixels, w, h, CancellationToken.None, accurate: true);
                            _lastFromAi = true;
                            CommitReadOrigin(origin, scale, moves);
                            UpdateDocument(fromNewFrame: true);
                            SetStatus($"高精度で読み直しました {swa.Elapsed.TotalSeconds:0.0} 秒 ・ {_lastLines.Count} 行{AccumulateStatus}");
                        }
                        return;
                    }
                    _lastPixels = pixels;
                    _accuratePending = false;
                    _lastLines = await _aiOcr.RecognizeAsync(pixels, w, h, CancellationToken.None);
                    _lastFromAi = true;
                    _lastCaptureHeight = h;
                    _lastPixelScale = scale;
                    sw.Stop();
                    if (!_aiOcrReady)
                    {
                        _aiOcrReady = true;
                        ShowLoading(false);
                    }
                    _aiOcrFailures = 0;
                    _accuratePending = _aiOcr.LastConfidence < 0.9 && _lastLines.Count > 0;
                    await FindLayoutAsync(pixels, w, h);
                    _lastOcrMs = sw.ElapsedMilliseconds;
                    CommitReadOrigin(origin, scale, moves);
                    UpdateDocument(fromNewFrame: true);
                    UpdateChips();
                    SetStatus($"{_lastLines.Count} 行{GroupStatus}{AccumulateStatus}");
                    return;
                }
                catch (Exception ex) when (!_aiOcrReady || ++_aiOcrFailures >= 3)
                {
                    // AI OCR が使えないときは Mac の文字認識で続ける (60 秒後にまた試す)
                    _aiOcrError = ex.Message;
                    _aiOcrErrorAt = DateTime.Now;
                    _aiOcrFailures = 0;
                    _aiOcrReady = false;
                    _aiWarmup = null;
                    ShowLoading(false);
                    sw.Restart();
                    args["force"] = true;
                }
            }

            // Mac の文字認識 (Vision)。画面が変わっていなければ補助プログラムが "unchanged" を返す
            var reply = await MacHelper.Instance.RequestAsync("capture_ocr", args, TimeSpan.FromSeconds(15));
            if (reply["unchanged"]?.GetValue<bool>() == true) return;
            _lastCaptureHeight = reply["h"]!.GetValue<int>();
            // 単語 (日本語は 1 文字) ごとの位置 (Vision の boundingBox(for:))。数が合わなければ使わない
            _lastLines = VisionLines.Parse(reply["lines"]!.AsArray());
            _lastFromAi = false;
            _lastPixelScale = scale;
            sw.Stop();
            // 枠・まとまりごとに表示するときは、枠線を探すために画像も受け取る
            if (_settings.OcrView == OcrView.Groups && !Accumulating && _lastLines.Count >= 2)
            {
                args["force"] = true;
                var shot = await MacHelper.Instance.RequestAsync("capture", args, TimeSpan.FromSeconds(10));
                await FindLayoutAsync(Convert.FromBase64String(shot["data"]!.GetValue<string>()), shot["w"]!.GetValue<int>(), shot["h"]!.GetValue<int>());
            }
            else _lastSegments = [];
            _lastOcrMs = sw.ElapsedMilliseconds;
            CommitReadOrigin(origin, scale, moves);
            UpdateDocument(fromNewFrame: true);
            UpdateChips();
            SetStatus($"{_lastLines.Count} 行{GroupStatus}{AccumulateStatus}");
        }
        catch (Exception ex)
        {
            _forceNext = true;
            SetStatus("エラー: " + ex.Message + (ex.Message.Contains("許可") || ex.Message.Contains("TCC") || ex.Message.Contains("denied")
                ? "" : " (読み取れないときは、システム設定 → プライバシーとセキュリティ → 画面収録とシステムオーディオ録音 で GetText を許可してください)"));
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
        task.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            _forceNext = true;
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

    private string GroupStatus => _settings.OcrView != OcrView.Groups || Accumulating ? ""
        : _grouping.HasGroups ? $" ・ まとまり {_grouping.Groups.Count}" : " ・ まとまりなし (上から順)";
    private byte[]? _lastPixels;
    private bool _accuratePending; // 確からしさが低かったので、画面が止まったら高精度で読み直す

    /// <summary>動作確認用: 枠の中を今すぐ読み取り、状態欄の文字を返す。</summary>
    internal async Task<string> ReadNowForTestAsync()
    {
        await RunOcrAsync(force: true);
        return StatusText.Text ?? "";
    }

    // 画面が変わったか (文字入力カーソルの点滅くらいの変化は無視する。Windows 版の ScreenCapture.HasChanged と同じ)
    private static bool Changed(byte[] previous, byte[] current)
    {
        if (previous.Length != current.Length) return true;
        var a = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(previous);
        var b = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(current);
        if (a.SequenceEqual(b)) return false;
        int changed = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i] && ++changed > 64) return true;
        return false;
    }

    private bool Accumulating => _accumulating;

    private string AccumulateStatus => Accumulating ? $" ・ 蓄積 {_accumulator.LineCount} 行" : "";

    /// <summary>読み取った 1 回分を、受け取る拡張機能に渡す (別のスレッドで。読み取りは待たない)。画像は渡さない。</summary>
    private async void PublishToPlugins(OcrDocument frame)
    {
        // 読み取ったときの範囲 (読み取りの途中で枠を動かしていたら渡さない: 位置が合わないため)
        if (_readOrigin is not { } read) return;
        double s = _capture.DesktopScaling > 0 ? _capture.DesktopScaling : 1;
        var size = _capture.CaptureSize;
        var origin = new Point(read.X - CaptureWindow.Inset[0] * s, read.Y - CaptureWindow.Inset[1] * s);
        var region = ((int)read.X, (int)read.Y, (int)(size.Width * s), (int)(size.Height * s));
        double toScreen = read.ToScreen;
        // 読み取りの範囲の下のアプリ (ポイントの座標で調べる)。分からなければ渡さない
        (string App, string Title)? source = null;
        try
        {
            var at = await MacHelper.Instance.RequestAsync("window_at", new System.Text.Json.Nodes.JsonObject
            {
                ["x"] = origin.X / s + CaptureWindow.Inset[0] + size.Width / 2,
                ["y"] = origin.Y / s + CaptureWindow.Inset[1] + size.Height / 2,
            }, TimeSpan.FromSeconds(1));
            if (at["app"]?.GetValue<string>() is { Length: > 0 } app) source = (app, at["title"]?.GetValue<string>() ?? "");
        }
        catch (Exception ex) when (ex is MacHelperException or InvalidOperationException or IOException)
        {
            // (補助プログラムが無い・応答しない: アプリの名前なしで渡す)
        }
        PluginRuntime.PublishOcrFrame(PluginFrames.From(frame, region, toScreen: toScreen, source: source));
    }

    /// <param name="publish">拡張機能に渡す (蓄積を始めたときなど、同じ回をもう一度使うときは渡さない)。</param>
    private void UpdateDocument(bool fromNewFrame = false, bool publish = true)
    {
        if (_lastLines == null) return;
        var frame = OcrDocument.From(_lastLines, _settings.JoinCjk, correct: false);
        if (fromNewFrame && publish && !App.DemoMode && PluginRuntime.HasOcrSubscribers) PublishToPlugins(frame);
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
            _doc = _grouping.HasGroups ? OcrLayout.ToDocument(_grouping, _settings.JoinCjk, correct: false) : frame;
        }
        else
        {
            _doc = frame;
        }
        bool hasKana = _doc.HasKana;
        _units = _doc.TranslationUnits()
            .Select(p => p.Select(u => (u, TextScript.Classify(u.Text, hasKana))).ToList())
            .ToList();
        RenderOcr();
        RenderTranslation();
        PumpTranslation();
        UpdateCaptureHighlights();
    }

    private void RenderOcr()
    {
        if (_doc == null) return;
        if (_doc.IsEmpty)
        {
            // 空の状態: 読み取ったが文字が無い
            // 黒く写っている (保護された画面など) ときは、そう知らせる (回避はしない)。画像を受け取る AI OCR のときだけ見分けられる
            bool uniform = !Accumulating && _lastFromAi && _lastPixels is { } shot && _lastCaptureHeight > 0
                           && ProtectedContent.LooksProtected(shot, shot.Length / 4 / _lastCaptureHeight, _lastCaptureHeight);
            OcrEmptyTitle.Text = Accumulating ? "まだ集めた文字はありません" : uniform ? ProtectedContent.Title : "枠の中に文字が見つかりません";
            OcrEmptyDetail.Text = Accumulating
                ? "文書をスクロールすると、読んだ内容がここに追記されます。"
                : uniform ? ProtectedContent.Detail
                : "読みたい文字の上に枠を重ねるか、枠を広げてください。小さな文字は枠を大きくすると読みやすくなります。";
        }
        bool applied = _ocrPane.Set(TextConverter.Apply(_doc.ToDisplayText(), _settings.Convert));
        if (!applied) OcrNote.Text = HoldNote;
        else if (OcrNote.Text == HoldNote || _aiOcrReady || !UseAiOcr) OcrNote.Text = "";
    }

    // ───────── 翻訳 ─────────

    private List<string> MissingForeignUnits() =>
        _units.SelectMany(p => p)
            .Where(u => u.Kind == ScriptKind.Foreign && !_translator.TryGetCached(u.Unit.Text, out _))
            .Select(u => u.Unit.Text)
            .ToList();

    private void RenderTranslation()
    {
        UpdateOverlay();
        if (!_settings.Translate) return;
        int foreign = 0, missing = 0;
        var paragraphs = new List<string>();
        foreach (var paragraph in _units)
        {
            var lines = new List<string>();
            foreach (var (unit, kind) in paragraph)
            {
                if (kind != ScriptKind.Foreign)
                {
                    lines.Add(unit.Text);
                    continue;
                }
                foreign++;
                if (_translator.TryGetCached(unit.Text, out var translated)) lines.Add(translated);
                else { lines.Add("…"); missing++; }
            }
            paragraphs.Add(string.Join("\n", lines));
        }
        string note = "";
        if (foreign == 0)
        {
            _translationPane.Set("");
            if (_units.Count > 0) note = "外国語のテキストはありません";
        }
        else
        {
            var text = TextConverter.Apply(string.Join("\n\n", paragraphs), _settings.Convert);
            if (!_translationPane.Set(text)) note = HoldNote;
            else if (missing > 0) note = "翻訳中…";
        }
        if (_translateError != null) note = _translateError;
        TranslationNote.Text = note;
        UpdateChips();
    }

    private async void PumpTranslation()
    {
        if (_translating || !_settings.Translate || App.DemoMode) return;
        _translating = true;
        int failures = 0;
        try
        {
            while (_settings.Translate && MissingForeignUnits().Count > 0 && failures < 3)
            {
                var wait = _nextTranslateAt - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                var missing = MissingForeignUnits();
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
            if (failures >= 3 && !_shutDown)
                DispatcherTimer.RunOnce(() => { if (_settings.Translate && !_shutDown) PumpTranslation(); }, TimeSpan.FromSeconds(30));
        }
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

    // ───────── 訳を画面に重ねる ─────────

    /// <summary>訳を画面に重ねているか (翻訳がオンで、重ねる設定がオン)。</summary>
    private bool OverlayOn => _settings.Translate && _settings.TranslationOverlay;

    /// <summary>読み取る範囲の左上 (画面のピクセル。拡張機能に渡す範囲と同じ計算)。</summary>
    private (double X, double Y) CaptureOrigin()
    {
        double s = _capture.DesktopScaling > 0 ? _capture.DesktopScaling : 1;
        var origin = _capture.Position;
        return (origin.X + CaptureWindow.Inset[0] * s, origin.Y + CaptureWindow.Inset[1] * s);
    }

    /// <summary>読み取った範囲を、訳を重ねる位置の基準にする (読み取りの途中で枠を動かしていたら使わない)。</summary>
    private void CommitReadOrigin((double X, double Y) origin, double pixelScale, int moves)
    {
        double s = _capture.DesktopScaling > 0 ? _capture.DesktopScaling : 1;
        _positionsStale = moves != _frameMoves;
        _readOrigin = !_positionsStale ? (origin.X, origin.Y, pixelScale > 0 ? s / pixelScale : 1) : null;
        if (!_positionsStale) _readFrame = (_capture.Position, _capture.CaptureSize);
    }

    /// <summary>
    /// 訳した文を、その文の行が画面で占める位置に重ねる (表示 → 訳を画面に重ねる)。訳は日本語訳の欄と同じもの (訳し直さない)。
    /// 蓄積中 (画面の位置が無い)・隠している・しまっている・枠を動かした直後は重ねない。
    /// </summary>
    private void UpdateOverlay()
    {
        if (!OverlayOn || Accumulating || _ocrHidden || WindowState == WindowState.Minimized || _readOrigin is not { } o)
        {
            Overlay.Clear(OverlayOwner);
            return;
        }
        var labels = TranslationOverlay.Labels(_units.SelectMany(p => p),
            text => _translator.TryGetCached(text, out var ja) ? TextConverter.Apply(ja, _settings.Convert) : null,
            (o.X, o.Y), o.ToScreen);
        Overlay.Show(OverlayOwner, labels);
    }

    /// <summary>枠を動かした・大きさを変えた: 読み取った位置は今の画面と合わないので、重ねた訳を消す (次に読み取ったら出し直す)。</summary>
    private void ForgetReadPositions()
    {
        if (_readFrame is { } read && read == (_capture.Position, _capture.CaptureSize)) return; // (動いていない: 窓を出したときの知らせなど)
        _frameMoves++;
        _forceNext = true; // (動かした先がたまたま同じ画像でも、読み直して位置を決め直す)
        if (!_positionsStale)
        {
            _positionsStale = true;
            _capture.SetHighlights([], -1, _lastPixelScale); // (枠の上の検索の印も、読み直すまで消す)
        }
        if (_readOrigin == null) return;
        _readOrigin = null;
        UpdateOverlay();
    }

    /// <summary>訳を画面に重ねる / やめる。重ねるときは翻訳もオンにする。</summary>
    public void ToggleOverlay()
    {
        bool on = !OverlayOn;
        bool turnedOnTranslation = on && !_settings.Translate;
        _settings.TranslationOverlay = on;
        if (turnedOnTranslation)
        {
            _settings.Translate = true;
            OnSettingChanged(nameof(AppSettings.Translate));
        }
        OnSettingChanged(nameof(AppSettings.TranslationOverlay));
        _settingsWindow?.LoadTranslation();
        string engine = _translator.Engine == TranslationEngine.Local ? "Mac 内の翻訳" : $"{_translator.EngineName} (オンライン。外国語の文をインターネットに送ります)";
        SetStatus(!on ? "訳を画面に重ねるのをやめました"
            : (turnedOnTranslation ? $"翻訳もオンにしました ({engine})。" : "")
              + (Accumulating ? "訳を画面に重ねます (蓄積中は重ねません。蓄積を終えると重ねます)"
                 : "訳を画面に重ねます (読み取りの枠の中の外国語の文に。クリックは下の窓に通ります)"));
    }

    private void OverlayItem_Click(object? sender, RoutedEventArgs e) => ToggleOverlay();

    // ───────── 操作 ─────────

    private void SetStatus(string text) => StatusText.Text = (_paused ? "停止中 ・ " : "") + text;

    private void SetPaused(bool paused)
    {
        _paused = paused;
        PauseToggle.IsChecked = paused;
        PauseLabel.Text = paused ? "再開" : "一時停止";
        PauseIcon.Icon = paused ? AppIcon.Play : AppIcon.Pause;
        _capture.SetPaused(paused);
        SetStatus(paused ? "自動読み取りを停止しました" : "自動読み取りを再開しました");
        if (!paused) _forceNext = true;
    }

    private void PauseToggle_Click(object? sender, RoutedEventArgs e) => SetPaused(PauseToggle.IsChecked == true);

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RunOcrAsync(force: true);

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings(SettingsPage.ScreenOcr);

    /// <summary>設定を開く。owner を渡すとその窓の上に出す (読み取りを閉じているときなど)。</summary>
    public void OpenSettings(SettingsPage page = SettingsPage.Appearance, Window? owner = null)
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.SelectPage(page);
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this, _settings);
        _settingsWindow.SelectPage(page);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        // 隠れている窓は持ち主にできない (「議事録の間は読み取りの画面を隠す」・機能を選ぶ画面から使うとき)
        owner ??= IsVisible ? this : null;
        if (owner is { IsVisible: true }) _settingsWindow.Show(owner);
        else _settingsWindow.Show();
    }

    private void Minutes_Click(object? sender, RoutedEventArgs e) => OpenMinutes();

    public MinutesWindow OpenMinutes()
    {
        if (_minutesWindow is { IsVisible: true })
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

    private bool _ocrHidden;        // 今、読み取り枠とこの画面を隠しているか
    private bool _ocrClosed;        // 機能を選ぶ画面で「文字の読み取り」を閉じている
    private bool _hiddenForMinutes; // 議事録を開いている間は隠す設定で、議事録を開いている
    private bool _exitRequested;

    /// <summary>開いている機能が変わった (文字の読み取り・議事録を開いた・閉じた、記録を始めた・止めた)。</summary>
    public event Action? FeaturesChanged;

    public bool IsOcrOpen => !_ocrClosed;

    public void MinutesStateChanged() => FeaturesChanged?.Invoke();

    /// <summary>議事録の画面で表示や文字の大きさを変えたとき、開いている設定の画面にも反映する。</summary>
    public void MinutesDisplayChanged() => _settingsWindow?.LoadMinutesDisplay();

    public void SetOcrHidden(bool hidden)
    {
        _hiddenForMinutes = hidden;
        ApplyOcrVisibility();
    }

    /// <summary>機能を選ぶ画面から使う: 最初は読み取りを閉じておく (どのアプリでも効くキーは登録する)。</summary>
    public void PrepareForLauncher()
    {
        _ocrClosed = true;
        _ocrHidden = true;
        if (!App.DemoMode) _ = RegisterHotkeysAsync();
    }

    public void OpenOcr()
    {
        _ocrClosed = false;
        _hiddenForMinutes = false; // 自分で開いたときは、議事録の間に隠す設定より優先する
        ApplyOcrVisibility();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void CloseOcr()
    {
        _ocrClosed = true;
        ApplyOcrVisibility();
    }

    /// <summary>読み取りの画面 (枠) が出ているか (隠れている間は、画面に文字を重ねない)。</summary>
    public bool IsOcrVisible => !_ocrClosed && !_hiddenForMinutes;

    private void ApplyOcrVisibility()
    {
        bool hidden = _ocrClosed || _hiddenForMinutes;
        if (hidden) Overlay.ClearAll(); // (画面に重ねた訳・拡張機能の文字を消す)
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
                _forceNext = true;
                UpdateOverlay();
            }
        }
        FeaturesChanged?.Invoke();
    }

    /// <summary>GetText を終える (機能を選ぶ画面の「終了」)。minutesConfirmed なら、議事録を止めてよいかはもう聞いてある。</summary>
    public void RequestExit(bool minutesConfirmed = false)
    {
        _exitRequested = true;
        if (minutesConfirmed) _closeConfirmed = true;
        Close();
    }

    // ───────── コマンドの一覧から使う操作 ─────────

    public void ReadNow() => _ = RunOcrAsync(force: true);

    public void TogglePause() => SetPaused(!_paused);

    public void ToggleAccumulate() => SetAccumulating(!_accumulating);

    /// <summary>蓄積中か (蓄積中は上から順につなげるので、枠・まとまりごとにはできない)。</summary>
    public bool IsAccumulating => _accumulating;

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

    private void Palette_Click(object? sender, RoutedEventArgs e) => AppCommands.TogglePalette(this, CommandContext.ScreenOcr);

    private void PinToggle_Click(object? sender, RoutedEventArgs e) => TogglePin();

    private void ViewMenu_Opening(object? sender, EventArgs e)
    {
        ViewTextItem.IsChecked = _settings.OcrView == OcrView.Text;
        ViewGroupsItem.IsChecked = _settings.OcrView == OcrView.Groups;
        ViewGroupsItem.IsEnabled = !_accumulating; // 蓄積中は上から順につなげる
        LayoutBelowItem.IsChecked = _settings.Layout == PaneLayout.Below;
        LayoutRightItem.IsChecked = _settings.Layout == PaneLayout.Right;
        LayoutBelowItem.IsEnabled = LayoutRightItem.IsEnabled = _settings.Translate;
        OverlayItem.IsChecked = OverlayOn;
        AccumulateItem.IsChecked = _accumulating;
        if (sender is MenuFlyout menu) AddReadingFeatures(menu);
    }

    /// <summary>
    /// 読み取りの画面で使う拡張機能 (キーワードの見張り・読み取りの履歴など) を、表示メニューの終わりに切り替えとして出す
    /// (開くたびに作り直す。押すと始める / 止める)。
    /// </summary>
    private static void AddReadingFeatures(MenuFlyout menu)
    {
        foreach (var old in menu.Items.OfType<Control>().Where(i => Equals(i.Tag, "reading-feature")).ToList()) menu.Items.Remove(old);
        var features = AppCommands.Features.Where(f => f.InReadingWindow).ToList();
        if (features.Count == 0) return;
        menu.Items.Add(new Separator { Tag = "reading-feature" });
        foreach (var feature in features)
        {
            bool on = feature.Status().State != FeatureState.Closed;
            var item = new MenuItem { Header = feature.Name, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on, Tag = "reading-feature" };
            ToolTip.SetTip(item, feature.Description);
            var f = feature;
            item.Click += (_, _) => { if (on) f.Close?.Invoke(); else f.Open(); };
            menu.Items.Add(item);
        }
    }

    private void ViewText_Click(object? sender, RoutedEventArgs e) => SetView(OcrView.Text);

    private void ViewGroups_Click(object? sender, RoutedEventArgs e) => SetView(OcrView.Groups);

    private void LayoutBelow_Click(object? sender, RoutedEventArgs e)
    {
        _settings.Layout = PaneLayout.Below;
        OnSettingChanged(nameof(AppSettings.Layout));
    }

    private void LayoutRight_Click(object? sender, RoutedEventArgs e)
    {
        _settings.Layout = PaneLayout.Right;
        OnSettingChanged(nameof(AppSettings.Layout));
    }

    private void AccumulateItem_Click(object? sender, RoutedEventArgs e) => SetAccumulating(!_accumulating);

    private void AccumulateEnd_Click(object? sender, RoutedEventArgs e) => SetAccumulating(false);

    /// <summary>蓄積 (スクロールしながら読んだ内容を重複なしでつなげる) を始める / 終える。蓄積中は帯を出す。</summary>
    private void SetAccumulating(bool on)
    {
        _accumulating = on;
        _accumulator.Clear();
        AccumulateBar.IsVisible = on;
        if (!on && _settings.OcrView == OcrView.Groups) _forceNext = true; // (蓄積中は枠線を探していないので、すぐ読み直す)
        UpdateDocument(fromNewFrame: on, publish: false); // (拡張機能にはもう渡してある)
        UpdateAccumulateBar();
        SetStatus(on ? "蓄積中: スクロールすると読んだ内容を重複なしで追記します" + AccumulateStatus : "蓄積を終了しました");
    }

    private void UpdateAccumulateBar()
    {
        if (_accumulating) AccumulateText.Text = $"蓄積中 ・ {_accumulator.LineCount} 行: スクロールすると、読んだ内容を重複なしで追記します";
    }

    // ───────── 検索 (⌘F) ─────────

    private List<TextMatch> _matches = [];
    private int _activeMatch = -1;

    public void ShowSearch()
    {
        SearchBar.IsVisible = true;
        SearchToggle.IsChecked = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
        RefreshSearch();
    }

    private void CloseSearch()
    {
        SearchBar.IsVisible = false;
        SearchToggle.IsChecked = false;
        _matches = [];
        _activeMatch = -1;
        DrawTextHighlights();
        _capture.SetHighlights([], -1, _lastPixelScale);
        ResultBox.Focus();
    }

    private void SearchToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (SearchToggle.IsChecked == true) ShowSearch();
        else CloseSearch();
    }

    private void SearchClose_Click(object? sender, RoutedEventArgs e) => CloseSearch();

    private void SearchNext_Click(object? sender, RoutedEventArgs e) => MoveMatch(1);

    private void SearchPrev_Click(object? sender, RoutedEventArgs e) => MoveMatch(-1);

    private void SearchBox_TextChanged()
    {
        _activeMatch = -1;
        RefreshSearch();
        if (_matches.Count > 0) MoveMatch(0);
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            MoveMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
    }

    /// <summary>表示している原文から探し直す (読み取りが更新されたときも、今の一致の近くを選び続ける)。</summary>
    private void RefreshSearch()
    {
        if (!SearchBar.IsVisible) return;
        int previousStart = _activeMatch >= 0 && _activeMatch < _matches.Count ? _matches[_activeMatch].Start : -1;
        _matches = TextSearch.Find(ResultBox.Text ?? "", SearchBox.Text ?? "");
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
        if (TextPresenterOf(ResultBox) is { } presenter && ResultBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } sv)
        {
            var rect = presenter.TextLayout.HitTestTextPosition(_matches[_activeMatch].Start);
            double y = rect.Y;
            if (y < sv.Offset.Y || y + rect.Height > sv.Offset.Y + sv.Viewport.Height)
                sv.Offset = new Vector(sv.Offset.X, Math.Max(0, y - sv.Viewport.Height / 3));
        }
    }

    private void ShowMatches()
    {
        bool hasQuery = !string.IsNullOrWhiteSpace(SearchBox.Text);
        SearchCount.Text = !hasQuery ? "" : _matches.Count == 0 ? "0 件" : $"{(_activeMatch >= 0 ? _activeMatch + 1 : 0)}/{_matches.Count}";
        SearchCount.Foreground = MacTheme.BrushOf(p => hasQuery && _matches.Count == 0 ? p.Critical : p.TextSecondary);
        SearchPrevButton.IsEnabled = SearchNextButton.IsEnabled = _matches.Count > 0;
        DrawTextHighlights();
        UpdateCaptureHighlights();
    }

    private static Avalonia.Controls.Presenters.TextPresenter? TextPresenterOf(TextBox box) =>
        box.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().FirstOrDefault();

    /// <summary>原文の欄の上に、見つかった所の印を描く (今の一致は濃い色と太い枠。色だけでなく枠の太さでも区別する)。</summary>
    private void DrawTextHighlights()
    {
        SearchLayer.Children.Clear();
        if (_matches.Count == 0 || !SearchBar.IsVisible || TextPresenterOf(ResultBox) is not { } presenter) return;
        var p = MacTheme.Colors;
        int drawn = 0;
        for (int m = 0; m < _matches.Count && drawn < 400; m++)
        {
            bool active = m == _activeMatch;
            foreach (var rect in presenter.TextLayout.HitTestTextRange(_matches[m].Start, _matches[m].Length))
            {
                if (presenter.TranslatePoint(rect.TopLeft, SearchLayer) is not { } at) continue;
                if (at.Y + rect.Height < 0 || at.Y > SearchLayer.Bounds.Height) continue;
                var box = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = rect.Width + 2, Height = rect.Height, RadiusX = 2, RadiusY = 2,
                    Fill = new SolidColorBrush(Color.FromUInt32(active ? p.SearchHighlightActive : p.SearchHighlight), 0.35),
                    Stroke = new SolidColorBrush(Color.FromUInt32(active ? p.SearchHighlightActiveBorder : p.SearchHighlightBorder)),
                    StrokeThickness = active ? 2 : 1,
                };
                Canvas.SetLeft(box, at.X - 1);
                Canvas.SetTop(box, at.Y);
                SearchLayer.Children.Add(box);
                drawn++;
            }
        }
    }

    /// <summary>読み取り枠の上にも、見つかった所の印を付ける。</summary>
    private void UpdateCaptureHighlights()
    {
        if (_doc == null || _matches.Count == 0 || !SearchBar.IsVisible || Accumulating || _positionsStale)
        {
            _capture.SetHighlights([], -1, _lastPixelScale);
            return;
        }
        var shown = ResultBox.Text ?? "";
        int shownLines = shown.Split('\n').Count(l => l.TrimEnd('\r').Length > 0);
        if (shownLines != _doc.Lines.Count())
        {
            _capture.SetHighlights([], -1, _lastPixelScale);
            return;
        }
        _capture.SetHighlights(TextSearch.ToBoxes(_doc, shown, _matches), _activeMatch, _lastPixelScale);
    }

    // ───────── 知らせの帯 ─────────

    private bool _fallbackDismissed;

    private void ShowLoading(bool loading)
    {
        LoadingBar.IsVisible = loading && !App.DemoMode;
        UpdateFallbackBar();
    }

    private void UpdateFallbackBar()
    {
        bool fallback = UseAiOcr && _aiOcrError != null;
        if (!fallback) _fallbackDismissed = false;
        FallbackBar.IsVisible = fallback && !_fallbackDismissed && !App.DemoMode;
        FallbackText.Text = _aiOcrError ?? "";
    }

    private void FallbackClose_Click(object? sender, RoutedEventArgs e)
    {
        _fallbackDismissed = true;
        UpdateFallbackBar();
    }

    private void FallbackDetails_Click(object? sender, RoutedEventArgs e)
    {
        FallbackText.IsVisible = !FallbackText.IsVisible;
        FallbackDetails.Content = FallbackText.IsVisible ? "閉じる" : "詳細";
    }

    // 幅が狭いときは操作バーの文字を隠してアイコンだけにする (名前はマウスを乗せると出る)
    private void FitToolbar()
    {
        bool compact = Bounds.Width < 500;
        foreach (var label in this.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("toolLabel")))
            label.IsVisible = !compact;
    }

    // ───────── 見本の画面・動作確認 ─────────

    internal void ShowDemoLines(IReadOnlyList<OcrLineData> lines, byte[]? pixels, int width, int height, string? translation, double pixelScale = 1)
    {
        _accumulating = false;
        _lastLines = lines;
        _lastFromAi = true;
        _lastCaptureHeight = height;
        _lastPixelScale = pixelScale;
        _positionsStale = false; // (見本も、今の枠で読み取った 1 回として扱う)
        _readFrame = (_capture.Position, _capture.CaptureSize);
        _lastSegments = pixels != null && _settings.OcrView == OcrView.Groups ? OcrLayout.FindSegments(pixels, width, height, lines) : [];
        UpdateDocument(fromNewFrame: true);
        if (translation != null) _translationPane.Set(translation);
        TranslationNote.Text = "";
        _lastOcrMs = 52;
        UpdateChips();
        StatusText.Text = $"{lines.Count} 行{GroupStatus}";
    }

    internal void ShowDemoState(string state)
    {
        LoadingBar.IsVisible = state == "loading";
        FallbackBar.IsVisible = state == "fallback";
        if (state == "fallback")
        {
            FallbackText.Text = "ocr_server.py が起動しませんでした (Python が見つかりません)";
            OcrChip.Text = "Mac の文字認識 · 180 ms";
        }
        if (state == "loading") OcrChip.Text = "Mac の文字認識 · 160 ms";
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

    internal string SearchCountText => SearchCount.Text ?? "";

    internal void SearchNextForTest() => MoveMatch(1);

    internal string ResultText => ResultBox.Text ?? "";

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        _accumulator.Clear();
        _forceNext = true;
        _doc = _accumulator.ToDocument();
        _units = [];
        RenderOcr();
        RenderTranslation();
        SetStatus("蓄積した内容を消しました");
    }

    private void CopyOcr_Click(object? sender, RoutedEventArgs e) => CopyText(ResultBox, "原文");

    private void CopyTranslation_Click(object? sender, RoutedEventArgs e) => CopyText(TranslationBox, "日本語訳");

    private async void CopyText(TextBox box, string name, bool all = false)
    {
        if (box == TranslationBox && !_settings.Translate)
        {
            SetStatus("日本語訳はオフです (表示 または 設定 → 翻訳 でオンにできます)");
            return;
        }
        bool selection = !all && box.SelectionStart != box.SelectionEnd;
        var text = selection ? box.SelectedText : box.Text;
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("コピーするテキストがありません");
            return;
        }
        SetStatus(await Dialogs.CopyAsync(this, text)
            ? $"{name}の{(selection ? "選択範囲" : "全文")}をコピーしました ({text.Length} 文字)"
            : "クリップボードにアクセスできませんでした");
    }

    private void SaveOcr_Click(object? sender, RoutedEventArgs e) => SaveText(ResultBox.Text, "原文");

    private void SaveTranslation_Click(object? sender, RoutedEventArgs e) => SaveText(TranslationBox.Text, "日本語訳");

    private async void SaveText(string? text, string name)
    {
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("保存するテキストがありません");
            return;
        }
        var path = await Dialogs.SaveFileAsync(this, $"{name}の保存", $"GetText_{name}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            [new Dialogs.FileKind("テキスト", "txt")]);
        if (path == null) return;
        try
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
            SetStatus($"{name}を保存しました: {path}");
        }
        catch (Exception ex)
        {
            SetStatus("保存できませんでした: " + ex.Message);
        }
    }

    // 欄の右クリック: コピー・全文コピー・保存・文字変換
    private void Pane_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not TextBox box) return;
        var name = box == ResultBox ? "原文" : "日本語訳";
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }
        menu.Items.Add(Item("コピー", box.Copy, box.SelectionStart != box.SelectionEnd));
        menu.Items.Add(Item("すべて選択", box.SelectAll));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("全文をコピー", () => CopyText(box, name, all: true)));
        menu.Items.Add(Item("ファイルに保存…", () => SaveText(box.Text, name)));
        menu.Items.Add(new Separator());
        var convert = new MenuItem { Header = "文字変換" };
        foreach (var option in SettingsOptions.ConvertModes)
        {
            var value = option.Value;
            convert.Items.Add(Item((value == _settings.Convert ? "✓ " : "　 ") + option.Label, () =>
            {
                _settings.Convert = value;
                OnSettingChanged(nameof(AppSettings.Convert));
            }));
        }
        menu.Items.Add(convert);
        menu.Open(box);
        e.Handled = true;
    }

    private KeyModifiers CommandKey => TopLevel.GetTopLevel(this)?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    // ⌘ + ホイールで文字サイズを変える (両方の欄をそろえる)
    private void Box_PointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != CommandKey) return;
        SetFontSize(ResultBox.FontSize + (e.Delta.Y > 0 ? 1 : -1));
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var cmd = CommandKey;
        if (AppCommands.HandleKey(this, e, CommandContext.ScreenOcr)) return;
        if (e.Key == Key.F5 || (e.Key == Key.R && e.KeyModifiers == cmd))
            _ = RunOcrAsync(force: true);
        else if (e.Key == Key.F && e.KeyModifiers == cmd)
            ShowSearch();
        else if (e.Key == Key.G && SearchBar.IsVisible && (e.KeyModifiers == cmd || e.KeyModifiers == (cmd | KeyModifiers.Shift)))
            MoveMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        else if (e.Key == Key.Escape && SearchBar.IsVisible)
            CloseSearch();
        else if (e.Key == Key.P && e.KeyModifiers == cmd)
            SetPaused(!_paused);
        else if (e.Key == Key.OemComma && e.KeyModifiers == cmd)
            OpenSettings();
        else if (e.Key == Key.C && e.KeyModifiers == (cmd | KeyModifiers.Shift))
            CopyText(ResultBox, "原文");
        else if (e.Key == Key.T && e.KeyModifiers == (cmd | KeyModifiers.Shift))
            CopyText(TranslationBox, "日本語訳");
        else
            return;
        e.Handled = true;
    }

    // ───────── どのアプリを使っていても効くショートカット (⌃⌥ + キー) ─────────

    public IReadOnlyList<string> FailedHotkeys { get; private set; } = [];

    // Mac の仮想キーコード
    private static readonly (int Id, int Key, string Label)[] Hotkeys =
    [
        (1, 8, "⌃⌥C"),  // C: 原文をコピー
        (2, 17, "⌃⌥T"), // T: 日本語訳をコピー
        (3, 15, "⌃⌥R"), // R: 今すぐ読み取る
        (4, 35, "⌃⌥P"), // P: 一時停止 / 再開
        (5, 12, "⌃⌥Q"), // Q: 範囲を選んで文字をコピー (Quick OCR)
    ];

    private async Task RegisterHotkeysAsync()
    {
        if (!MacHelper.IsAvailable) return;
        try
        {
            var keys = new JsonArray(Hotkeys.Select(h => (JsonNode)new JsonObject
            {
                ["id"] = h.Id, ["key"] = h.Key, ["mods"] = new JsonArray("ctrl", "alt"),
            }).ToArray());
            var reply = await MacHelper.Instance.RequestAsync("hotkeys", new JsonObject { ["keys"] = keys });
            var failed = reply["failed"]!.AsArray().Select(f => f!.GetValue<int>()).ToHashSet();
            FailedHotkeys = Hotkeys.Where(h => failed.Contains(h.Id)).Select(h => h.Label).ToList();
            if (FailedHotkeys.Count > 0)
            {
                SetStatus($"ショートカット {string.Join(", ", FailedHotkeys)} はほかのアプリが使っているため登録できませんでした");
                OcrNote.Text = $"ショートカット {string.Join(", ", FailedHotkeys)} は使えません";
            }
        }
        catch (Exception ex)
        {
            App.Log("Hotkeys", ex);
        }
    }

    private bool _helperRestarted;

    /// <summary>DeepL のキーをキーチェーンから読む (UI のスレッドを止めない)。</summary>
    private async Task LoadDeepLKeyAsync()
    {
        try
        {
            var key = await Task.Run(() => _settings.GetDeepLKey());
            _translator.DeepLKey = key;
        }
        catch (Exception ex)
        {
            App.Log("DeepLKey", ex);
        }
    }

    private void OnHelperEvent(JsonObject json)
    {
        switch (json["event"]?.GetValue<string>())
        {
            case "helper_exited":
                _helperRestarted = true; // 次に起動したら、どのアプリでも効くキーを登録し直す
                return;
            case "ready" when _helperRestarted:
                _helperRestarted = false;
                Dispatcher.UIThread.Post(() => _ = RegisterHotkeysAsync());
                return;
        }
        if (json["event"]?.GetValue<string>() != "hotkey") return;
        int id = json["id"]!.GetValue<int>();
        Dispatcher.UIThread.Post(() =>
        {
            switch (id)
            {
                case 1: if (OcrOpenFor("原文をコピー")) CopyText(ResultBox, "原文"); break;
                case 2: if (OcrOpenFor("日本語訳をコピー")) CopyText(TranslationBox, "日本語訳"); break;
                case 3:
                    if (_ocrHidden) OpenOcr(); // (閉じていれば開いて読む)
                    _ = RunOcrAsync(force: true);
                    break;
                case 4: if (OcrOpenFor("一時停止を切り替え")) SetPaused(!_paused); break;
                case 5: MacQuickOcr.Run(); break;
            }
        });
    }

    /// <summary>どのアプリでも効くキー: 読み取りを閉じている間は、見えないまま動かさずに知らせる (開くボタンつき)。</summary>
    private bool OcrOpenFor(string what)
    {
        if (!_ocrHidden) return true;
        PluginToast.Show("GetText", new PluginNotification(new LocalizedText($"文字の読み取りを開いていないため、{what}できません"),
            new LocalizedText("ホームの「文字の読み取り」で開くと使えます。"), PluginNotificationKind.Info, new LocalizedText("開く"), OpenOcr));
        return false;
    }

    // ───────── 位置 ─────────

    /// <summary>枠の隣に移動する。画面の右端などで入らないときは左や下に回り、画面の外には出さない。</summary>
    private void FollowCapture()
    {
        if (!IsVisible && !App.DemoMode) return;
        var anchor = WindowPlacement.PixelBounds(_capture);
        var size = WindowPlacement.PixelBounds(this).Size;
        double scale = _capture.DesktopScaling > 0 ? _capture.DesktopScaling : 1;
        var p = WindowPlacement.Beside(anchor, size, FollowGap * scale, WindowPlacement.WorkArea(_capture));
        Position = new PixelPoint((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    private bool _shutDown;

    /// <summary>アプリを終えるとき (メニューの「終了」など) にも、設定と議事録を保存する。</summary>
    public void SaveOnShutdown() => Shutdown();

    /// <summary>開いている議事録の画面 (無ければ null)。</summary>
    internal MinutesWindow? Minutes => _minutesWindow;

    private bool _closeConfirmed;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // 機能を選ぶ画面から使っているときは、この画面の ✕ は「文字の読み取りを閉じる」 (GetText は終わらない)
        if (App.Home != null && !_exitRequested && !_shutDown && e.CloseReason != WindowCloseReason.ApplicationShutdown)
        {
            e.Cancel = true;
            CloseOcr();
            return;
        }
        // 議事録を記録中・文字起こし中なら、止めてよいか確かめる (この窓を閉じるとアプリが終わるため)
        if (!_shutDown && !_closeConfirmed && _minutesWindow is { IsBusy: true } minutes)
        {
            e.Cancel = true;
            if (!await minutes.ConfirmStopAsync("GetText を終了します"))
            {
                _exitRequested = false; // 「止めない」を選んだ
                return;
            }
            _closeConfirmed = true;
            Close();
            return;
        }
        // 記録中・記録を止めて残りの音声を文字にしている途中なら、止めて終わってから閉じる (最後の発言を失わない)
        if (!_shutDown && _minutesWindow is { } stopping && (stopping.HasPendingWork || stopping.IsRecordingNow))
        {
            e.Cancel = true;
            await stopping.StopForExitAsync();
            Close();
            return;
        }
        Shutdown();
    }

    private void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        _timer.Stop();
        _translateCts.Cancel();
        Overlay.ClearAll();
        MacHelper.Instance.EventReceived -= OnHelperEvent;
        _minutesWindow?.ShutdownNow();
        _translator.Shutdown();
        _aiOcr.Dispose();
        StorePlacement();
        _settings.Save();
        _capture.AllowClose = true;
        _capture.Close();
    }

    internal void StorePlacement()
    {
        _settings.CaptureWidth = _capture.Bounds.Width > 0 ? _capture.Bounds.Width : _capture.Width;
        _settings.CaptureHeight = _capture.Bounds.Height > 0 ? _capture.Bounds.Height : _capture.Height;
        _settings.CapturePixel = [_capture.Position.X, _capture.Position.Y];
        if (WindowState == WindowState.Normal)
        {
            _settings.TextWidth = Bounds.Width > 0 ? Bounds.Width : Width;
            _settings.TextHeight = Bounds.Height > 0 ? Bounds.Height : Height;
            _settings.TextPixel = [Position.X, Position.Y];
        }
    }
}
