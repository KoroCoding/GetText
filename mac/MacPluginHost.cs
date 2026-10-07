using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using GetText.Plugins;

namespace GetText;

/// <summary>
/// 拡張機能のための Mac の部品 (知らせ・ファイルを選ぶ・進み具合・Finder で表示・クリップボード・URL)。
/// 拡張機能は自分で画面を描かず、ここを通して GetText の部品を使う。
/// </summary>
public sealed class MacPluginHost : IPluginHostUi
{
    /// <summary>
    /// 起動したとき: 記録を準備し、ホームを出した後 (手が空いたとき) に読み込む。
    /// Safe Mode (--safe-mode・環境変数 GETTEXT_SAFE_MODE=1) では拡張機能を読み込まない。
    /// </summary>
    public static void Start(string[] args, TextWindow text, Action? openExtensions)
    {
        bool safeMode = args.Contains("--safe-mode", StringComparer.OrdinalIgnoreCase)
            || Environment.GetEnvironmentVariable("GETTEXT_SAFE_MODE") is "1" or "true";
        var host = new MacPluginHost();
        PluginRuntime.RegisterService<IPluginUi>(host);
        PluginRuntime.RegisterService<IScreenCaptureService>(new MacScreenCapture());
        // 読み取り・翻訳は、読み取りの画面と同じ方式・補助プロセスを使う (この Mac の中だけ)
        PluginRuntime.RegisterService<IOcrService>(new HostOcrService([new AiOcrProvider(text.AiOcr), new VisionOcrProvider()], () => text.PreferredOcrProvider));
        PluginRuntime.RegisterService<ITranslationService>(new LocalTranslationService(text.Translator.Local));
        PluginRuntime.RegisterService<IDocumentService>(new MacDocumentService());
        var overlay = new MacOverlayService(() => text.IsOcrVisible);
        text.OcrHidden += overlay.ClearAll;
        PluginRuntime.RegisterService<IOverlayService>(overlay);
        // 開発者向けの API (既定でオフ。設定でオンにしたときだけ 127.0.0.1 で受ける)
        DeveloperApiControl.Host = new DeveloperApiHost
        {
            ReadingText = text.ReadingTextForApi,
            Ocr = PluginRuntime.GetService<IOcrService>(),
            Translation = PluginRuntime.GetService<ITranslationService>(),
            Documents = PluginRuntime.GetService<IDocumentService>(),
        };
        DeveloperApiControl.Apply(text.Settings);
        PluginRuntime.AddBuiltInCapability("ocr");
        PluginRuntime.AddBuiltInCapability("ocr.local");
        PluginRuntime.AddBuiltInCapability("meeting.transcript");
        PluginRuntime.AddBuiltInCapability("screen.record");
        PluginRuntime.Prepare(host, safeMode);
        Dispatcher.UIThread.Post(() =>
        {
            PluginRuntime.LoadAll();
            if (PluginRuntime.CrashedPlugin is { } crashed)
            {
                var name = PluginRuntime.Plugins.FirstOrDefault(p => p.Record.Id == crashed)?.Manifest?.Name.For("ja") ?? crashed;
                PluginToast.Show("GetText", new PluginNotification(
                    $"拡張機能「{name}」を止めました",
                    "前回、この拡張機能を読み込んでいる途中で GetText が終了しました。設定 → 拡張機能 で、有効に戻すか消すかを選べます。",
                    PluginNotificationKind.Warning, openExtensions != null ? new LocalizedText("拡張機能を開く") : null, openExtensions));
            }
            else if (PluginRuntime.SafeMode)
            {
                PluginToast.Show("GetText", new PluginNotification("Safe Mode で起動しました",
                    "拡張機能は読み込んでいません。普通に起動し直すと、元に戻ります。", PluginNotificationKind.Info));
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    public void ShowNotification(string pluginName, PluginNotification notification) => PluginToast.Show(pluginName, notification);

    public void ContributionsChanged() => Post(() => App.Home?.ReloadFeatures());

    public void Post(Action action) => Dispatcher.UIThread.Post(() =>
    {
        try { action(); }
        catch (Exception ex) { App.Log("Plugin", ex); }
    });

    public Task<IReadOnlyList<string>> PickFilesAsync(LocalizedText title, IReadOnlyList<(string Name, string[] Extensions)> filters, bool multiple,
        CancellationToken cancellationToken) => OnUi<IReadOnlyList<string>>(async () =>
    {
        if (Owner() is not { } owner) return [];
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title.For("ja"),
            AllowMultiple = multiple,
            FileTypeFilter = Filter(filters),
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    });

    public Task<string?> PickFolderAsync(LocalizedText title, CancellationToken cancellationToken) => OnUi(async () =>
    {
        if (Owner() is not { } owner) return null;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title.For("ja") });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    });

    public Task<string?> PickSaveFileAsync(LocalizedText title, string suggestedName, IReadOnlyList<(string Name, string[] Extensions)> filters,
        CancellationToken cancellationToken) => OnUi(async () =>
    {
        if (Owner() is not { } owner) return null;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title.For("ja"),
            SuggestedFileName = Path.GetFileName(suggestedName),
            FileTypeChoices = Filter(filters),
        });
        return file?.TryGetLocalPath();
    });

    public Task<CapturedImage?> SelectScreenRegionAsync(LocalizedText title, CancellationToken cancellationToken) => MacQuickOcr.SelectAndCaptureAsync();

    /// <summary>一覧から 1 つ選んでもらう (GetText の選択の画面)。</summary>
    public Task<int?> ChooseAsync(LocalizedText title, LocalizedText? message, IReadOnlyList<LocalizedText> options, CancellationToken cancellationToken) =>
        OnUi(async () =>
        {
            if (options.Count == 0 || Owner() is not { } owner) return (int?)null;
            return await Dialogs.ChooseAsync(owner, "GetText — " + title.For("ja"), message?.For("ja") ?? "",
                options.Select(o => (o.For("ja"), "", true)).ToList(), 0, "選ぶ");
        });

    public IPluginProgress BeginProgress(LocalizedText title) =>
        Dispatcher.UIThread.CheckAccess()
            ? PluginToast.Progress("GetText", title.For("ja"))
            : Dispatcher.UIThread.InvokeAsync(() => PluginToast.Progress("GetText", title.For("ja"))).GetAwaiter().GetResult();

    /// <summary>Finder で表示する (ファイルなら選んだ状態で)。引数は 1 つずつ渡し、シェルの文字列にはしない。</summary>
    public void Reveal(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var info = new ProcessStartInfo("open") { UseShellExecute = false };
            if (File.Exists(full)) info.ArgumentList.Add("-R");
            else if (!Directory.Exists(full)) return;
            info.ArgumentList.Add(full);
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            App.Log("PluginReveal", ex);
        }
    }

    public Task<bool> CopyTextAsync(string text) => OnUi(async () =>
    {
        if (Owner()?.Clipboard is not { } clipboard) return false;
        await clipboard.SetTextAsync(text);
        return true;
    });

    public void OpenUrl(Uri url)
    {
        if (!PluginUrls.IsAllowed(url)) return;
        Dispatcher.UIThread.Post(async () =>
        {
            if (Owner()?.Launcher is { } launcher) await launcher.LaunchUriAsync(url);
        });
    }

    private static Window? Owner()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return null;
        return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
    }

    private static List<FilePickerFileType> Filter(IReadOnlyList<(string Name, string[] Extensions)> filters) =>
        filters
            .Where(f => f.Extensions.Length > 0)
            .Select(f => new FilePickerFileType(f.Name) { Patterns = f.Extensions.Select(e => "*." + e.TrimStart('.', '*')).ToList() })
            .Append(FilePickerFileTypes.All)
            .ToList();

    private static Task<T> OnUi<T>(Func<Task<T>> func) => Dispatcher.UIThread.InvokeAsync(func);
}

/// <summary>
/// 拡張機能のための画面の取り込み (Mac)。補助プログラム (ScreenCaptureKit) で窓・画面を 1 枚ずつ取り込む。
/// 取り込みを禁止された窓・保護された動画は OS が黒くし、それを回避しない。
/// </summary>
public sealed class MacScreenCapture : IScreenCaptureService
{
    public async Task<IReadOnlyList<CaptureSource>> ListSourcesAsync(CancellationToken cancellationToken)
    {
        var list = new List<CaptureSource>();
        if (!MacHelper.IsAvailable) return list;
        JsonObject reply;
        try
        {
            reply = await MacHelper.Instance.RequestAsync("record_targets", timeout: TimeSpan.FromSeconds(15));
        }
        catch (MacHelperException ex)
        {
            throw new InvalidOperationException(ex.Message, ex); // (拡張機能が受け止められる種類にする)
        }
        int n = 0;
        foreach (var d in reply["displays"]?.AsArray() ?? [])
        {
            if (d == null) continue;
            n++;
            bool main = d["main"]?.GetValue<bool>() == true;
            list.Add(new CaptureSource(CaptureSourceKind.Monitor, d["id"]!.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"画面 {n}{(main ? " (メイン)" : "")} ・ {d["w"]?.GetValue<int>()}×{d["h"]?.GetValue<int>()}"));
        }
        foreach (var w in reply["windows"]?.AsArray() ?? [])
        {
            if (w == null) continue;
            var title = w["title"]?.GetValue<string>() ?? "";
            var app = w["app"]?.GetValue<string>() ?? "";
            list.Add(new CaptureSource(CaptureSourceKind.Window, w["id"]!.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.IsNullOrEmpty(title) || title == app ? app : $"{title} ({app})"));
        }
        return list;
    }

    public async Task<CapturedImage?> CaptureAsync(CaptureSource source, CancellationToken cancellationToken)
    {
        if (!MacHelper.IsAvailable || !int.TryParse(source.Id, out var id)) return null;
        var args = new System.Text.Json.Nodes.JsonObject { ["max"] = 2560 };
        if (source.Kind == CaptureSourceKind.Window) args["window"] = id;
        else if (source.Kind == CaptureSourceKind.Monitor) args["display"] = id;
        else return null;
        JsonObject reply;
        try
        {
            reply = await MacHelper.Instance.RequestAsync("capture_target", args, TimeSpan.FromSeconds(15));
        }
        catch (MacHelperException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (reply["data"]?.GetValue<string>() is not { } data) return null;
        int width = reply["w"]!.GetValue<int>(), height = reply["h"]!.GetValue<int>();
        var bgra = Convert.FromBase64String(data);
        return bgra.Length == width * height * 4 ? new CapturedImage(bgra, width, height, DateTimeOffset.Now) : null;
    }
}

/// <summary>
/// 画面の右下に出す GetText の知らせ (拡張機能の知らせ・進み具合)。色・字・角はデザイントークン、アイコンは線のアイコン。
/// 前に出ても、作業中の窓からフォーカスを奪わない。知らせは 3 つまで重ねる (古いものから消す)。
/// </summary>
public sealed class PluginToast : Window
{
    private static readonly List<PluginToast> Open = [];
    private const int MaxOpen = 3;

    private readonly GtIcon _icon = new() { Width = 18, Height = 18, Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, FontSize = DesignTokens.MacType.BodyStrong };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), FontSize = DesignTokens.MacType.Body };
    private readonly TextBlock _source = new() { Margin = new Thickness(0, 6, 0, 0), FontSize = DesignTokens.MacType.Caption };
    private readonly ProgressBar _progress = new() { Height = 4, MinHeight = 4, Margin = new Thickness(0, 8, 0, 0), IsVisible = false, Minimum = 0, Maximum = 1 };
    private readonly Button _action = new() { Margin = new Thickness(0, 10, 0, 0), IsVisible = false };
    private readonly DispatcherTimer _timer = new();
    private Action? _onAction;
    private bool _closed;

    private PluginToast()
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        var close = new Button
        {
            Content = new GtIcon { Icon = AppIcon.Close, Width = 12, Height = 12 },
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        ToolTip.SetTip(close, "閉じる");
        Avalonia.Automation.AutomationProperties.SetName(close, "知らせを閉じる");
        close.Bind(ForegroundProperty, close.GetResourceObservable("Gt.TextSecondary"));
        close.Click += (_, _) => Close();
        _action.Click += (_, _) =>
        {
            var action = _onAction;
            Close();
            try { action?.Invoke(); } catch (Exception ex) { App.Log("PluginToastAction", ex); }
        };

        _title.Bind(TextBlock.ForegroundProperty, _title.GetResourceObservable("Gt.TextPrimary"));
        _message.Bind(TextBlock.ForegroundProperty, _message.GetResourceObservable("Gt.TextSecondary"));
        _source.Bind(TextBlock.ForegroundProperty, _source.GetResourceObservable("Gt.TextTertiary"));
        _progress.Bind(ForegroundProperty, _progress.GetResourceObservable("Gt.Accent"));

        var text = new StackPanel();
        text.Children.Add(_title);
        text.Children.Add(_message);
        text.Children.Add(_progress);
        text.Children.Add(_source);
        text.Children.Add(_action);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(close, 2);
        grid.Children.Add(_icon);
        grid.Children.Add(text);
        grid.Children.Add(close);

        var card = new Border
        {
            Padding = new Thickness(16, 12, 8, 14),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(DesignTokens.MacRadii.Dialog),
            Child = grid,
            Margin = new Thickness(8),
            BoxShadow = BoxShadows.Parse("0 2 16 0 #30000000"),
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("Gt.Surface"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("Gt.Border"));
        Content = card;

        _timer.Tick += (_, _) => Close();
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            Open.Remove(this);
            Arrange();
        };
        SizeChanged += (_, _) => Arrange();
    }

    /// <summary>知らせを出す (UI のスレッドで呼ぶ)。</summary>
    public static void Show(string source, PluginNotification n)
    {
        var toast = Create();
        toast.Set(n.Title.For("ja"), n.Message?.For("ja"), n.Kind);
        toast._source.Text = source;
        if (n.ActionLabel != null && n.Action != null)
        {
            toast._action.Content = n.ActionLabel.For("ja");
            toast._onAction = n.Action;
            toast._action.IsVisible = true;
        }
        // 操作のボタンがある・問題の知らせは長めに出す
        toast.CloseAfter(TimeSpan.FromSeconds(n.Kind is PluginNotificationKind.Error or PluginNotificationKind.Warning || n.Action != null ? 15 : 6));
        toast.Display();
    }

    /// <summary>進み具合を出す (Complete で結果に変わり、少しして消える)。</summary>
    public static IPluginProgress Progress(string source, string title)
    {
        var toast = Create();
        toast.Set(title, null, PluginNotificationKind.Info);
        toast._source.Text = source;
        toast._progress.IsVisible = true;
        toast._progress.IsIndeterminate = true;
        var handle = new ProgressHandle(toast);
        toast._action.Content = "中止";
        toast._onAction = handle.Cancel;
        toast._action.IsVisible = true;
        toast.Display();
        return handle;
    }

    private static PluginToast Create()
    {
        while (Open.Count >= MaxOpen) Open[0].Close();
        var toast = new PluginToast();
        Open.Add(toast);
        return toast;
    }

    private void Set(string title, string? message, PluginNotificationKind kind)
    {
        _title.Text = title;
        _message.Text = message ?? "";
        _message.IsVisible = !string.IsNullOrEmpty(message);
        var (icon, color) = kind switch
        {
            PluginNotificationKind.Success => (AppIcon.Success, "Gt.Success"),
            PluginNotificationKind.Warning => (AppIcon.Warning, "Gt.Warning"),
            PluginNotificationKind.Error => (AppIcon.Error, "Gt.Critical"),
            _ => (AppIcon.Info, "Gt.AccentText"),
        };
        _icon.Icon = icon;
        _icon.Bind(GtIcon.ForegroundProperty, _icon.GetResourceObservable(color));
        Avalonia.Automation.AutomationProperties.SetName(this, string.IsNullOrEmpty(message) ? title : $"{title}: {message}");
    }

    private void CloseAfter(TimeSpan time)
    {
        if (_closed) return; // (閉じた知らせのタイマーを動かさない)
        _timer.Stop();
        _timer.Interval = time;
        _timer.Start();
    }

    private void Display()
    {
        Show();
        Arrange();
    }

    /// <summary>右下から上へ積む。</summary>
    private static void Arrange()
    {
        if (Open.Count == 0 || Open[^1].Screens.Primary is not { } screen) return;
        var area = screen.WorkingArea;
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        int bottom = area.Bottom - (int)(8 * scaling);
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var t = Open[i];
            int height = (int)((t.Bounds.Height > 0 ? t.Bounds.Height : 120) * scaling);
            int width = (int)(t.Width * scaling);
            t.Position = new PixelPoint(area.Right - width - (int)(8 * scaling), bottom - height);
            bottom -= height;
        }
    }

    private sealed class ProgressHandle(PluginToast toast) : IPluginProgress
    {
        private readonly CancellationTokenSource _cancel = new();
        private bool _done;

        public CancellationToken CancellationToken => _cancel.Token;

        public void Cancel() => _cancel.Cancel();

        public void Report(double? value, LocalizedText? detail = null) => Dispatcher.UIThread.Post(() =>
        {
            if (_done || toast._closed) return;
            toast._progress.IsIndeterminate = value == null;
            if (value is { } v) toast._progress.Value = Math.Clamp(v, 0, 1);
            if (detail != null)
            {
                toast._message.Text = detail.For("ja");
                toast._message.IsVisible = true;
            }
        });

        public void Complete(LocalizedText? message = null, bool failed = false) => Dispatcher.UIThread.Post(() =>
        {
            if (_done || toast._closed) return;
            _done = true;
            toast._progress.IsVisible = false;
            toast._action.IsVisible = false;
            toast.Set(toast._title.Text ?? "", message?.For("ja") ?? (failed ? "できませんでした" : "終わりました"),
                failed ? PluginNotificationKind.Error : PluginNotificationKind.Success);
            toast.CloseAfter(TimeSpan.FromSeconds(failed ? 15 : 4));
        });

        public void Dispose()
        {
            if (!_done) Complete();
        }
    }
}
