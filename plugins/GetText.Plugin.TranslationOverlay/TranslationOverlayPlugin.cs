namespace GetText.Plugins.TranslationOverlay;

/// <summary>
/// 訳を重ねる: 読み取りの枠の中の外国語の行に、日本語の訳を重ねて出す (画面の上の、その行の位置に)。
/// 訳はこの PC の中の翻訳だけを使う (オンラインには送らない)。重ねる文字は GetText が描き、クリックは下の窓に通る。
/// 読み取りの画面を開いている間だけ動き、閉じると消える。読み取った文字・訳はログに書かない。
/// </summary>
public sealed class TranslationOverlayPlugin : IGetTextPlugin
{
    private const string Owner = "translation-overlay";
    private const string ActiveKey = "active";

    private readonly TranslationCache _cache = new();
    private IPluginContext? _context;
    private bool _active;
    private bool _unavailableReported;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.AddFeature(new PluginFeature
        {
            Id = "overlay",
            Name = new LocalizedText(new Dictionary<string, string> { ["ja"] = "訳を重ねる", ["en"] = "Translation Overlay" }),
            Description = "読み取りの枠の中の外国語に、日本語の訳を重ねて出します (この PC の中で訳します)",
            Icon = "Translate",
            Status = () => _active ? new PluginFeatureStatus(PluginFeatureState.Open, "重ねています") : new PluginFeatureStatus(PluginFeatureState.Closed),
            Open = _ => SetActive(true),
            Close = _ => SetActive(false),
        });
        context.AddCommand(new PluginCommand
        {
            Id = "toggle",
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "訳を画面に重ねる / やめる", ["en"] = "Toggle translation overlay" }),
            Keywords = ["overlay", "translate", "翻訳", "重ねる", "訳"],
            Icon = "Translate",
            Execute = _ => SetActive(!_active),
        });
        if (context.Settings.GetBool(ActiveKey, false)) Subscribe(true);
    }

    /// <summary>重ねている間だけ読み取りの結果を受け取る (重ねていなければ、GetText は結果を作らない)。</summary>
    private void Subscribe(bool on)
    {
        if (on == _active) return;
        _active = on;
        if (on) _context!.OcrFrameRead += OnFrame;
        else
        {
            _context!.OcrFrameRead -= OnFrame;
            _context.GetService<IOverlayService>()?.Clear(Owner);
        }
    }

    private Task SetActive(bool on)
    {
        var context = _context!;
        if (on && context.GetService<ITranslationService>() is not { IsAvailable: true })
        {
            context.Notify(new PluginNotification("訳を重ねられません", "この PC の中の翻訳が入っていません。設定 → モデルとセットアップ で入れられます。", PluginNotificationKind.Warning));
            return Task.CompletedTask;
        }
        context.Settings.Set(ActiveKey, on ? "true" : "false");
        Subscribe(on);
        _unavailableReported = false;
        if (on) context.Notify(new PluginNotification("訳を重ねます", "読み取りの枠の中の外国語の行に、日本語の訳を重ねます。読み取りの画面を開いている間だけ動きます。", PluginNotificationKind.Success));
        return Task.CompletedTask;
    }

    private void OnFrame(object? sender, OcrFrameEventArgs e)
    {
        var context = _context!;
        var overlay = context.GetService<IOverlayService>();
        var translator = context.GetService<ITranslationService>();
        if (!_active || overlay == null || translator == null) return;
        var lines = e.Lines.Where(l => OverlayText.IsForeign(l.Text)).ToList();
        if (lines.Count == 0)
        {
            overlay.Clear(Owner);
            return;
        }
        try
        {
            var missing = lines.Select(l => l.Text).Distinct().Where(t => !_cache.TryGet(t, out _)).ToList();
            if (missing.Count > 0)
            {
                // (受け取る処理は GetText が別のスレッドで呼ぶ。訳し終えるまで次の回は飛ばされる)
                var translated = translator.TranslateToJapaneseAsync(missing, CancellationToken.None).GetAwaiter().GetResult();
                for (int i = 0; i < missing.Count && i < translated.Count; i++)
                    if (!string.IsNullOrWhiteSpace(translated[i])) _cache.Add(missing[i], translated[i].Trim());
            }
            if (!_active) return;
            var labels = lines
                .Select(l => _cache.TryGet(l.Text, out var ja) ? new OverlayLabel(l.Left, l.Top, l.Right, l.Bottom, ja) : null)
                .OfType<OverlayLabel>()
                .ToList();
            overlay.Show(Owner, labels);
        }
        catch (InvalidOperationException ex)
        {
            if (!_unavailableReported)
            {
                _unavailableReported = true;
                context.Notify(new PluginNotification("訳せませんでした", ex.Message, PluginNotificationKind.Warning));
            }
            context.Log.Warn("訳せませんでした: " + ex.GetType().Name); // (文字は書かない)
        }
    }

    public void Shutdown()
    {
        if (_active && _context != null)
        {
            _context.OcrFrameRead -= OnFrame;
            _context.GetService<IOverlayService>()?.Clear(Owner);
        }
    }
}
