namespace GetText.Plugins.Barcode;

/// <summary>
/// QR コード・バーコードを読む: 画面の範囲を選ぶと、その中のコードを読んでコピーする。URL なら「開く」も出す (押したときだけ開く)。
/// 読むのはこの PC の中で、画像は保存しない。URL を自動で開くことはしない。
/// </summary>
public sealed class BarcodePlugin : IGetTextPlugin
{
    private IPluginContext? _context;
    private bool _busy;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.AddFeature(new PluginFeature
        {
            Id = "scan",
            Name = new LocalizedText(new Dictionary<string, string> { ["ja"] = "QR コード・バーコード", ["en"] = "QR / Barcode" }),
            Description = "画面の範囲を選ぶと、QR コード・バーコードを読んでコピーします",
            Icon = "ScreenOcr",
            Open = ScanAsync,
        });
        context.AddCommand(new PluginCommand
        {
            Id = "scan",
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "範囲を選んで QR コード・バーコードを読む", ["en"] = "Scan QR code / barcode on screen" }),
            Keywords = ["qr", "barcode", "バーコード", "QR コード", "JAN", "読む"],
            Icon = "ScreenOcr",
            IsAvailable = () => !_busy,
            Execute = ScanAsync,
        });
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        var context = _context!;
        if (_busy) return;
        var ui = context.GetService<IPluginUi>();
        if (ui == null) return;
        _busy = true;
        try
        {
            var image = await ui.SelectScreenRegionAsync("QR コード・バーコードを読む", ct);
            if (image == null) return;
            var hits = await Task.Run(() => BarcodeScanner.Scan(image.Bgra, image.Width, image.Height), ct);
            if (hits.Count == 0)
            {
                context.Notify(new PluginNotification("コードが見つかりませんでした", "コードの全体が入るように、少し広めに選んでください。", PluginNotificationKind.Warning));
                return;
            }
            var text = string.Join("\n", hits.Select(h => h.Text));
            bool copied = await ui.CopyTextAsync(text);
            var first = hits[0];
            var title = hits.Count == 1 ? $"{first.FormatLabel}を読みました" : $"コードを {hits.Count} 個読みました";
            var message = (copied ? "コピーしました: " : "") + (text.Length > 120 ? text[..120] + "…" : text);
            // URL は自動で開かない。押したときだけ、http・https のものを開く
            context.Notify(first.Url is { } url
                ? new PluginNotification(title, message, PluginNotificationKind.Success, ActionLabel: $"{url.Host} を開く", Action: () => ui.OpenUrl(url))
                : new PluginNotification(title, message, PluginNotificationKind.Success));
            context.Log.Info($"コードを読みました ({hits.Count} 個)"); // (中身は書かない)
        }
        finally
        {
            _busy = false;
        }
    }

    public void Shutdown()
    {
    }
}
