namespace GetText.Plugins.Presentation;

/// <summary>
/// スライドの記録: 選んだ画面・窓を一定の間隔で取り込み、止まった新しいスライドだけを PNG で残し、止めたら PPTX にまとめる。
/// 動画・アニメーション・スクロールの途中は残さない。保護された画面 (黒く写る) は残さず、知らせるだけ (回避しない)。
/// 画像はこの PC の拡張機能のフォルダと、利用者が選んだ保存先にだけ書く。送信しない。
/// </summary>
public sealed class PresentationPlugin : IGetTextPlugin
{
    private const string SensitivityKey = "sensitivity";
    private const string IntervalKey = "interval";

    private IPluginContext? _context;
    private CancellationTokenSource? _run;
    private Task? _loop;
    private CaptureSource? _source;
    private string? _session;
    private readonly List<string> _slides = [];
    private readonly object _lock = new();

    // 止まっている → 始めている (画面を選んでいる) → 記録中 → 止めている (保存先を選んでいる) → 止まっている。
    // 始める・止めるを続けて押しても、同時に 2 つ動かない・記録中のフォルダを消さない
    private const int Idle = 0, Starting = 1, Running = 2, Stopping = 3;
    private int _phase;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.AddSettings(new PluginSettingsPage
        {
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "スライドの記録", ["en"] = "Presentation Capture" }),
            Items =
            [
                new PluginSetting
                {
                    Key = SensitivityKey,
                    Label = "新しいスライドとみなす変化",
                    Description = "ゆるめにすると、箇条書きが 1 行増えたくらいでも新しいスライドとして残します。",
                    Kind = PluginSettingKind.Choice,
                    Default = "normal",
                    Choices =
                    [
                        ("strict", "厳しめ (大きく変わったときだけ)"),
                        ("normal", "普通"),
                        ("loose", "ゆるめ (小さな変化も残す)"),
                    ],
                },
                new PluginSetting
                {
                    Key = IntervalKey,
                    Label = "画面を見る間隔 (秒)",
                    Description = "短いほど早く気づきますが、PC の負担が増えます。",
                    Kind = PluginSettingKind.Number,
                    Default = "1",
                    Minimum = 0.5,
                    Maximum = 10,
                    Advanced = true,
                },
            ],
        });
        context.AddFeature(new PluginFeature
        {
            Id = "capture",
            Name = new LocalizedText(new Dictionary<string, string> { ["ja"] = "スライドの記録", ["en"] = "Presentation Capture" }),
            Description = "発表のスライドが変わるたびに残し、PPTX にまとめます",
            Icon = "Presentation",
            Status = Status,
            Open = _ => StartAsync(),
            Close = _ => StopAsync(),
        });
        context.AddCommand(new PluginCommand
        {
            Id = "start",
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "スライドの記録を始める", ["en"] = "Start presentation capture" }),
            Keywords = ["presentation", "slide", "pptx", "スライド", "発表", "プレゼン"],
            Icon = "Presentation",
            IsAvailable = () => Volatile.Read(ref _phase) == Idle,
            Execute = _ => StartAsync(),
        });
        context.AddCommand(new PluginCommand
        {
            Id = "stop",
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "スライドの記録を止めて PPTX に保存", ["en"] = "Stop and save as PPTX" }),
            Keywords = ["presentation", "slide", "pptx", "スライド", "保存"],
            Icon = "Save",
            IsAvailable = () => Volatile.Read(ref _phase) == Running,
            Execute = _ => StopAsync(),
        });
    }

    private PluginFeatureStatus Status()
    {
        int phase = Volatile.Read(ref _phase);
        if (phase == Stopping) return new PluginFeatureStatus(PluginFeatureState.Active, "保存しています");
        if (phase != Running) return new PluginFeatureStatus(PluginFeatureState.Closed);
        int count;
        lock (_lock) count = _slides.Count;
        return new PluginFeatureStatus(PluginFeatureState.Active, $"記録中 ・ {count} 枚");
    }

    private SlideSensitivity Sensitivity => _context!.Settings.Get(SensitivityKey) switch
    {
        "strict" => SlideSensitivity.Strict,
        "loose" => SlideSensitivity.Loose,
        _ => SlideSensitivity.Normal,
    };

    private async Task StartAsync()
    {
        if (Interlocked.CompareExchange(ref _phase, Starting, Idle) != Idle) return; // (始めている・記録中・止めている途中)
        bool started = false;
        try
        {
            started = await StartCoreAsync();
        }
        finally
        {
            Volatile.Write(ref _phase, started ? Running : Idle);
        }
    }

    private async Task<bool> StartCoreAsync()
    {
        var context = _context!;
        var capture = context.GetService<IScreenCaptureService>();
        var ui = context.GetService<IPluginUi>();
        if (capture == null || ui == null)
        {
            context.Notify(new PluginNotification("スライドの記録を始められません", "この GetText では画面を取り込めません。", PluginNotificationKind.Error));
            return false;
        }
        IReadOnlyList<CaptureSource> sources;
        try
        {
            sources = await capture.ListSourcesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            context.Log.Error("取り込める画面の一覧を取れませんでした", ex);
            context.Notify(new PluginNotification("取り込める画面の一覧を取れませんでした", ex.Message, PluginNotificationKind.Error));
            return false;
        }
        if (sources.Count == 0)
        {
            context.Notify(new PluginNotification("取り込める画面がありません", "画面の取り込みの許可を確かめてください。", PluginNotificationKind.Warning));
            return false;
        }
        var choice = await ui.ChooseAsync("記録する画面", "発表のスライドを映している画面または窓を選んでください。",
            sources.Select(s => new LocalizedText((s.Kind == CaptureSourceKind.Window ? "窓: " : "") + s.Name)).ToList(), CancellationToken.None);
        if (choice is not { } index || index < 0 || index >= sources.Count) return false;

        _source = sources[index];
        _session = Path.Combine(context.DataDirectory, "sessions", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_session);
        lock (_lock) _slides.Clear();
        var run = new CancellationTokenSource();
        _run = run;
        var interval = TimeSpan.FromSeconds(Math.Clamp(context.Settings.GetNumber(IntervalKey, 1), 0.5, 10));
        _loop = Task.Run(() => LoopAsync(capture, _source, _session, Sensitivity, interval, run.Token));
        context.Log.Info($"記録を始めました ({_source.Kind}・{Sensitivity}・{interval.TotalSeconds} 秒ごと)");
        context.Notify(new PluginNotification("スライドの記録を始めました", $"{_source.Name}\nスライドが止まるたびに残します。止めると PPTX に保存できます。",
            PluginNotificationKind.Success, ActionLabel: "止めて保存", Action: () => _ = StopAsync()));
        return true;
    }

    private async Task LoopAsync(IScreenCaptureService capture, CaptureSource source, string folder, SlideSensitivity sensitivity, TimeSpan interval, CancellationToken ct)
    {
        var detector = new SlideDetector(sensitivity);
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                var image = await capture.CaptureAsync(source, ct);
                if (image == null)
                {
                    // 窓を閉じた・最小化した: 少し続けて取れなければ止める
                    if (++failures >= 10)
                    {
                        _context!.Notify(new PluginNotification("記録している窓が見つかりません", "窓を閉じたか、最小化した可能性があります。ここまでのスライドを保存できます。",
                            PluginNotificationKind.Warning, ActionLabel: "止めて保存", Action: () => _ = StopAsync()));
                        failures = -1000; // (1 度だけ知らせる)
                    }
                }
                else
                {
                    failures = 0;
                    Observe(detector, image, folder);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or UnauthorizedAccessException)
            {
                _context!.Log.Error("画面を取り込めませんでした", ex);
                failures++;
            }
            var wait = interval - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void Observe(SlideDetector detector, CapturedImage image, string folder)
    {
        var thumbnail = ImageHash.Thumbnail(image.Bgra, image.Width, image.Height);
        // 暗い一色だけを「保護された画面」とみなす (OS は保護された窓・動画を黒く写す)。白い一色は白紙のスライドとして扱う
        bool protectedFrame = ImageHash.IsUniform(image.Bgra, image.Width, image.Height) && ImageHash.Brightness(thumbnail) < 40;
        var decision = detector.Observe(ImageHash.DHash(image.Bgra, image.Width, image.Height),
            protectedFrame ? 0 : ImageHash.PHash(thumbnail), thumbnail, protectedFrame);
        switch (decision)
        {
            case SlideDecision.Protected:
                _context!.Notify(new PluginNotification("画面が黒く写っています",
                    "保護された画面 (動画の配信など) や、隠れた窓は記録できません。そのあいだは残しません。", PluginNotificationKind.Warning));
                break;
            case SlideDecision.NewSlide:
                int number;
                lock (_lock) number = _slides.Count + 1;
                var path = Path.Combine(folder, $"slide-{number:000}.png");
                File.WriteAllBytes(path, PngEncoder.Encode(image.Bgra, image.Width, image.Height));
                lock (_lock) _slides.Add(path);
                break;
        }
    }

    private async Task StopAsync()
    {
        if (Interlocked.CompareExchange(ref _phase, Stopping, Running) != Running) return; // (記録中のときだけ)
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            Volatile.Write(ref _phase, Idle);
        }
    }

    private async Task StopCoreAsync()
    {
        var context = _context!;
        var run = _run;
        var session = _session!;
        if (run == null) return;
        _run = null;
        run.Cancel();
        if (_loop != null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
        }
        run.Dispose();
        List<string> slides;
        lock (_lock) slides = [.. _slides];
        context.Log.Info($"記録を止めました ({slides.Count} 枚)");
        if (slides.Count == 0)
        {
            context.Notify(new PluginNotification("スライドはありませんでした", "画面が止まったスライドが見つかりませんでした。"));
            return;
        }
        var ui = context.GetService<IPluginUi>();
        var name = $"スライド {DateTime.Now:yyyy-MM-dd HH.mm}.pptx";
        var target = ui == null ? null : await ui.PickSaveFileAsync("PPTX の保存先", name, [("PowerPoint のプレゼンテーション", ["pptx"])], CancellationToken.None);
        if (target == null)
        {
            context.Notify(new PluginNotification($"スライド {slides.Count} 枚を残しました", "PPTX には保存しませんでした。画像は拡張機能のフォルダにあります。",
                PluginNotificationKind.Info, ActionLabel: "フォルダを開く", Action: () => ui?.Reveal(session)));
            return;
        }
        try
        {
            await Task.Run(() =>
            {
                var pages = slides.Select(p =>
                {
                    var png = File.ReadAllBytes(p);
                    var (w, h) = PngSize(png);
                    return new PptxSlide(png, w, h);
                }).ToList();
                var temp = target + ".tmp";
                using (var stream = File.Create(temp)) PptxWriter.Write(stream, pages, Path.GetFileNameWithoutExtension(target));
                File.Move(temp, target, overwrite: true);
            });
            // PPTX に入れたので、画面の画像は残さない (画面の画像を溜め込まない)
            try { Directory.Delete(session, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { context.Log.Warn("記録の画像のフォルダを消せませんでした: " + ex.Message); }
            context.Notify(new PluginNotification($"スライド {slides.Count} 枚を保存しました", Path.GetFileName(target), PluginNotificationKind.Success,
                ActionLabel: "保存した場所を開く", Action: () => ui!.Reveal(target)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Log.Error("PPTX を保存できませんでした", ex);
            context.Notify(new PluginNotification("PPTX を保存できませんでした", ex.Message, PluginNotificationKind.Error,
                ActionLabel: "画像のフォルダを開く", Action: () => ui!.Reveal(session)));
        }
    }

    /// <summary>PNG の幅と高さ (IHDR)。</summary>
    public static (int Width, int Height) PngSize(byte[] png) =>
        (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

    public void Shutdown()
    {
        // 終わるときは、取り込みだけ止める (画像は拡張機能のフォルダに残っている)
        _run?.Cancel();
    }
}
