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

        private void Apply(string text)
        {
            Box.Text = text;
            Pending = null;
            placeholder.IsVisible = text.Length == 0;
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
    private OcrDocument? _doc;
    private List<List<(string Text, ScriptKind Kind)>> _units = [];

    private bool _translating;
    private DateTime _nextTranslateAt = DateTime.MinValue;
    private CancellationTokenSource _translateCts = new();
    private string? _translateError;

    public AiOcr AiOcr => _aiOcr;
    public Translator Translator => _translator;

    public TextWindow() : this(new CaptureWindow(), new AppSettings()) { }

    public TextWindow(CaptureWindow capture, AppSettings settings)
    {
        InitializeComponent();
        _capture = capture;
        _settings = settings;
        _ocrPane = new Pane(ResultBox, OcrPlaceholder);
        _translationPane = new Pane(TranslationBox, TranslationPlaceholder);
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
        _capture.PositionChanged += (_, _) => { if (_settings.Follow) FollowCapture(); };
        _capture.SizeChanged += (_, _) =>
        {
            if (_settings.Follow) FollowCapture();
            _forceNext = true; // 大きさが変わったら次回は必ず読み直す
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                _capture.IsVisible = WindowState != WindowState.Minimized && !_ocrHidden;
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
        Topmost = _settings.Topmost;
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
            case nameof(AppSettings.Topmost):
                Topmost = _settings.Topmost;
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

    private void UpdateChips()
    {
        OcrChip.Text = UseAiOcr && _aiOcrError == null && _aiOcrReady
            ? "AI OCR" + (_aiOcr.Device switch { "gpu" => " ・ GPU", "cpu" => " ・ CPU", _ => "" })
            : "Mac の文字認識";
        TranslationChip.Text = _translator.EngineName;
    }

    // ───────── 読み取り ─────────

    // AI OCR は入っていて選ばれているときだけ使う (入っていなければ Mac の文字認識 = Vision)
    private bool UseAiOcr => _settings.OcrEngine == OcrEngineKind.Ai && AiOcr.IsInstalled;

    private void ApplyOcrEngineState()
    {
        _aiOcrError = null;
        _aiOcrReady = false;
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
        if (_busy || _ocrHidden || !MacHelper.IsAvailable) return;
        if (_paused && !force) return;
        if (WindowState == WindowState.Minimized && !force) return;
        var number = _capture.WindowNumber;
        var size = _capture.CaptureSize;
        if (number == 0 || size.Width < 4 || size.Height < 4) return;

        _busy = true;
        force |= _forceNext;
        _forceNext = false;
        try
        {
            var sw = Stopwatch.StartNew();
            if (UseAiOcr && _aiOcrError != null && DateTime.Now - _aiOcrErrorAt > TimeSpan.FromSeconds(60))
            {
                _aiOcrError = null;
                _aiOcrReady = false;
            }
            var args = new JsonObject
            {
                ["window"] = number,
                ["inset"] = new JsonArray(CaptureWindow.Inset.Select(v => (JsonNode)v).ToArray()),
                ["scale"] = Math.Max(2, _capture.DesktopScaling),
                ["force"] = force,
                ["languages"] = new JsonArray("ja-JP", "en-US", "zh-Hans", "ko-KR"),
            };
            if (UseAiOcr && _aiOcrError == null)
            {
                try
                {
                    // AI OCR: 画像を受け取って Python の RapidOCR で読む (変化は C# 側で判断する)
                    var shot = await MacHelper.Instance.RequestAsync("capture", args, TimeSpan.FromSeconds(10));
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
                            UpdateDocument(fromNewFrame: true);
                            SetStatus($"高精度で読み直しました {swa.Elapsed.TotalSeconds:0.0} 秒 ・ {_lastLines.Count} 行{AccumulateStatus}");
                        }
                        return;
                    }
                    _lastPixels = pixels;
                    _accuratePending = false;
                    if (!_aiOcrReady) OcrNote.Text = "AI OCR を準備中…";
                    _lastLines = await _aiOcr.RecognizeAsync(pixels, w, h, CancellationToken.None);
                    _lastCaptureHeight = h;
                    sw.Stop();
                    if (!_aiOcrReady)
                    {
                        _aiOcrReady = true;
                        OcrNote.Text = "";
                        UpdateChips();
                    }
                    _aiOcrFailures = 0;
                    _accuratePending = _aiOcr.LastConfidence < 0.9 && _lastLines.Count > 0;
                    UpdateDocument(fromNewFrame: true);
                    SetStatus($"{DateTime.Now:HH:mm:ss} 更新 ・ {_lastLines.Count} 行 ・ {sw.ElapsedMilliseconds} ms{AccumulateStatus}");
                    return;
                }
                catch (Exception ex) when (!_aiOcrReady || ++_aiOcrFailures >= 3)
                {
                    // AI OCR が使えないときは Mac の文字認識で続ける (60 秒後にまた試す)
                    _aiOcrError = ex.Message;
                    _aiOcrErrorAt = DateTime.Now;
                    _aiOcrFailures = 0;
                    _aiOcrReady = false;
                    OcrNote.Text = "";
                    UpdateChips();
                    sw.Restart();
                    args["force"] = true;
                }
            }

            // Mac の文字認識 (Vision)。画面が変わっていなければ補助プログラムが "unchanged" を返す
            var reply = await MacHelper.Instance.RequestAsync("capture_ocr", args, TimeSpan.FromSeconds(15));
            if (reply["unchanged"]?.GetValue<bool>() == true) return;
            _lastCaptureHeight = reply["h"]!.GetValue<int>();
            _lastLines = reply["lines"]!.AsArray().Select(l => new OcrLineData(
                [l!["text"]!.GetValue<string>()], l["left"]!.GetValue<double>(), l["top"]!.GetValue<double>(),
                l["right"]!.GetValue<double>(), l["bottom"]!.GetValue<double>())).ToList();
            sw.Stop();
            UpdateDocument(fromNewFrame: true);
            var details = $"{DateTime.Now:HH:mm:ss} 更新 ・ {_lastLines.Count} 行 ・ {sw.ElapsedMilliseconds} ms";
            if (_aiOcrError != null) details += " ・ AI OCR を使えないため Mac の文字認識で読み取り: " + _aiOcrError;
            SetStatus(details + AccumulateStatus);
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

    private bool Accumulating => AccumulateToggle.IsChecked == true;

    private string AccumulateStatus => Accumulating ? $" ・ 蓄積 {_accumulator.LineCount} 行" : "";

    private void UpdateDocument(bool fromNewFrame = false)
    {
        if (_lastLines == null) return;
        var frame = OcrDocument.From(_lastLines, _settings.JoinCjk, correct: false);
        if (Accumulating)
        {
            if (fromNewFrame) _accumulator.Add(frame, _lastCaptureHeight);
            _doc = _accumulator.ToDocument();
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

    private void RenderOcr()
    {
        if (_doc == null) return;
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
                    lines.Add(text);
                    continue;
                }
                foreign++;
                if (_translator.TryGetCached(text, out var translated)) lines.Add(translated);
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
            if (!_translationPane.Set(text)) note = "選択中のため更新を保留中";
            else if (missing > 0) note = "翻訳中…";
        }
        if (_translateError != null) note = _translateError;
        TranslationNote.Text = note;
        TranslationChip.Text = _translator.EngineName;
    }

    private async void PumpTranslation()
    {
        if (_translating || !_settings.Translate) return;
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

    // ───────── 操作 ─────────

    private void SetStatus(string text) => StatusText.Text = (_paused ? "停止中 ・ " : "") + text;

    private void SetPaused(bool paused)
    {
        _paused = paused;
        PauseToggle.IsChecked = paused;
        PauseLabel.Text = paused ? "▶ 再開" : "❚❚ 一時停止";
        _capture.SetPaused(paused);
        SetStatus(paused ? "自動読み取りを停止しました" : "自動読み取りを再開しました");
        if (!paused) _forceNext = true;
    }

    private void PauseToggle_Click(object? sender, RoutedEventArgs e) => SetPaused(PauseToggle.IsChecked == true);

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RunOcrAsync(force: true);

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>設定を開く。owner を渡すとその窓の上に出す (読み取りを閉じているときなど)。</summary>
    public void OpenSettings(int tab = 0, Window? owner = null)
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.SelectTab(tab);
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this, _settings);
        _settingsWindow.SelectTab(tab);
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

    private void ApplyOcrVisibility()
    {
        bool hidden = _ocrClosed || _hiddenForMinutes;
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

    private void AccumulateToggle_Click(object? sender, RoutedEventArgs e)
    {
        _accumulator.Clear();
        ClearButton.IsVisible = Accumulating;
        UpdateDocument(fromNewFrame: Accumulating);
        SetStatus(Accumulating
            ? "蓄積中: スクロールすると読んだ内容を重複なしで追記します" + AccumulateStatus
            : "蓄積を終了しました");
    }

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
        if (e.Key == Key.F5 || (e.Key == Key.R && e.KeyModifiers == cmd))
            _ = RunOcrAsync(force: true);
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
                case 1: CopyText(ResultBox, "原文"); break;
                case 2: CopyText(TranslationBox, "日本語訳"); break;
                case 3: _ = RunOcrAsync(force: true); break;
                case 4: SetPaused(!_paused); break;
            }
        });
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
        // 記録を止めて残りの音声を文字にしている途中なら、終わってから閉じる (最後の発言を失わない)
        if (!_shutDown && _minutesWindow is { HasPendingWork: true } stopping)
        {
            e.Cancel = true;
            await stopping.WaitPendingAsync();
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
