using GetText.Plugins;

namespace GetText.Plugins.Faulty;

/// <summary>
/// わざと問題を起こす拡張機能 (単体テストで、GetText が落ちずに受け止めるかを確かめる)。
/// DataDirectory/mode.txt: ok・throw-init・emoji-command・throw-shutdown・hang-shutdown・throw-ocr・slow-ocr・throw-command・throw-status
/// </summary>
public sealed class FaultyPlugin : IGetTextPlugin
{
    private string _mode = "ok";

    public void Initialize(IPluginContext context)
    {
        var file = Path.Combine(context.DataDirectory, "mode.txt");
        _mode = File.Exists(file) ? File.ReadAllText(file).Trim() : "ok";
        context.AddCommand(new PluginCommand
        {
            Id = "run",
            Title = _mode == "emoji-command" ? "🚀 速くする" : "テスト: 実行",
            Execute = _ => _mode == "throw-command" ? throw new InvalidOperationException("command failed") : Task.CompletedTask,
        });
        context.AddFeature(new PluginFeature
        {
            Id = "tile",
            Name = "テストの機能",
            Description = "テスト",
            Status = () => _mode == "throw-status" ? throw new InvalidOperationException("status failed") : new PluginFeatureStatus(PluginFeatureState.Closed),
            Open = _ => Task.CompletedTask,
        });
        context.OcrFrameRead += (_, e) =>
        {
            if (_mode == "throw-ocr") throw new InvalidOperationException("ocr handler failed");
            if (_mode == "slow-ocr") Thread.Sleep(300); // (遅い拡張機能: 読み取りが待たされないか)
            // 受け取ったことをテストに知らせる (読み取った文字は知らせに入れない)
            context.Notify(new PluginNotification("ocr", $"{e.Lines.Count} lines"));
        };
        if (_mode == "throw-init") throw new InvalidOperationException("initialize failed");
    }

    public void Shutdown()
    {
        if (_mode == "throw-shutdown") throw new InvalidOperationException("shutdown failed");
        if (_mode == "hang-shutdown") Thread.Sleep(10_000);
    }
}
