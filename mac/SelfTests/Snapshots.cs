using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

namespace GetText;

/// <summary>
/// 画面の画像を作る (GetText --snapshots フォルダ [dark])。画面には出さずに (Skia で描いて) 見本のデータの画面を PNG にする。
/// Mac の見た目 (ヒラギノの文字など) の確認と、説明書の画像に使う。
/// </summary>
internal static class Snapshots
{
    public static async Task<int> RunAsync(string dir, bool dark)
    {
        Directory.CreateDirectory(dir);
        App.ApplyTheme(dark ? AppTheme.Dark : AppTheme.Light);
        Dialogs.AutoAnswer = true;
        var settings = new AppSettings { MinutesLanguages = ["ja", "en"], Translate = true };
        int saved = 0;

        async Task Save(Window w, string name)
        {
            await UiSelfTest.Idle();
            var frame = w.CaptureRenderedFrame();
            if (frame == null) return;
            frame.Save(Path.Combine(dir, name + ".png"));
            Console.WriteLine($"{name}.png {frame.PixelSize.Width}x{frame.PixelSize.Height}");
            saved++;
        }

        var minutes = new MinutesWindow(new TestHost(), settings) { Width = 1040, Height = 720 };
        minutes.Show();
        foreach (var scene in new[] { "empty", "loading", "recording", "file", "many", "summary" })
        {
            minutes.LoadDemo(scene);
            await Save(minutes, "minutes_" + scene);
        }
        // 小さな画面 (ノート PC など) で、要約と発言の欄が重ならないか
        minutes.Width = 940;
        minutes.Height = 656;
        minutes.LoadDemo("summary");
        await Save(minutes, "minutes_summary_small");
        minutes.Close();

        var capture = new CaptureWindow(settings);
        var text = new TextWindow(capture, settings);
        capture.Show();
        text.Show();
        text.ShowDemo(
            "Quarterly Report\nRevenue grew 12% compared with last year.\n\n売上は前年より 12% 伸びました。",
            "四半期報告\n売上高は前年比で 12% 増加しました。\n\n売上は前年より 12% 伸びました。",
            "Mac の文字認識", "ローカル翻訳", "14:00:05 更新 ・ 4 行 ・ 180 ms");
        await Save(text, "text");
        await Save(capture, "capture");

        var settingsWindow = new SettingsWindow(text, settings);
        settingsWindow.Show();
        for (int i = 0; i < 6; i++)
        {
            settingsWindow.SelectTab(i);
            await Save(settingsWindow, $"settings_{i}");
        }
        settingsWindow.Close();
        return saved > 0 ? 0 : 1;
    }
}
