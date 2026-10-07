namespace GetText.Plugins.Sample;

/// <summary>
/// 見本の拡張機能: ホームのタイル・コマンドの一覧のコマンド・設定 1 つ・知らせ。
/// 画面は描かず、情報を渡すだけ (GetText が自分の部品で描く)。
/// </summary>
public sealed class SamplePlugin : IGetTextPlugin
{
    private IPluginContext? _context;
    private int _count;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.AddSettings(new PluginSettingsPage
        {
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "見本の拡張機能", ["en"] = "Sample Plugin" }),
            Items =
            [
                new PluginSetting
                {
                    Key = "greeting",
                    Label = "あいさつの文",
                    Description = "コマンドを実行したときに知らせに出す文です。",
                    Kind = PluginSettingKind.Text,
                    Default = "こんにちは。拡張機能が動いています。",
                },
            ],
        });
        context.AddCommand(new PluginCommand
        {
            Id = "greet",
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "見本: あいさつを出す", ["en"] = "Sample: Say hello" }),
            Keywords = ["sample", "hello", "見本"],
            Icon = "Info",
            Execute = _ => Greet(),
        });
        context.AddFeature(new PluginFeature
        {
            Id = "sample",
            Name = "見本の拡張機能",
            Description = "押すとあいさつの知らせを出します",
            Icon = "Extensions",
            Status = () => _count > 0
                ? new PluginFeatureStatus(PluginFeatureState.Open, $"{_count} 回")
                : new PluginFeatureStatus(PluginFeatureState.Closed),
            Open = _ => Greet(),
        });
        context.Log.Info("見本の拡張機能を初期化しました");
    }

    private Task Greet()
    {
        var context = _context!;
        _count++;
        var text = context.Settings.Get("greeting") is { Length: > 0 } g ? g : "こんにちは。拡張機能が動いています。";
        context.Notify(new PluginNotification("見本の拡張機能", text, PluginNotificationKind.Success));
        return Task.CompletedTask;
    }

    public void Shutdown()
    {
        _context?.Log.Info("見本の拡張機能を終えました");
    }
}
