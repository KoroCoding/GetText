using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;

namespace GetText;

/// <summary>
/// 実際の画面での起動の確認 (GetText --smoke 結果.txt)。Mac の本物の窓 (Avalonia.Native) で読み取りの画面・枠・議事録・設定を開き、
/// 窓を画像にして (結果のファイルと同じフォルダ) から終わる。見本の動作なので、設定や議事録は保存しない。
/// </summary>
internal static class SmokeTest
{
    public static async Task RunAsync(TextWindow text, string reportPath, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var report = new StringBuilder();
        int failed = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine((ok ? "OK  " : "NG  ") + what);
            if (!ok) failed++;
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        try
        {
            await Task.Delay(2500);
            Check(text.IsVisible, $"読み取りの画面を開く ({text.Bounds.Width:0}×{text.Bounds.Height:0}、拡大率 {text.DesktopScaling})");
            Check(Render(text, Path.Combine(dir, "smoke_text.png")), "読み取りの画面を描く");
            var capture = desktop.Windows.OfType<CaptureWindow>().FirstOrDefault();
            Check(capture is { IsVisible: true }, "読み取り枠を開く");
            if (capture != null)
            {
                Check(Render(capture, Path.Combine(dir, "smoke_capture.png")), "読み取り枠を描く");
                Check(!OperatingSystem.IsMacOS() || capture.WindowNumber > 0, $"枠の窓の番号を調べる ({capture.WindowNumber})");
            }
            if (MacHelper.IsAvailable)
            {
                // 枠の中を実際に読み取る (枠の窓の番号 → 補助プログラムで画面を取り込み → Vision で文字を読む)
                var status = await text.ReadNowForTestAsync();
                Check(System.Text.RegularExpressions.Regex.IsMatch(status, @"\d+ 行"), $"枠の中を読み取る (状態: {status})");
            }
            text.ShowDemo("Hello GetText\n議事録のテスト", "こんにちは GetText\n議事録のテスト", "Mac の文字認識", "ローカル翻訳", "見本");

            var minutes = text.OpenMinutes();
            await Task.Delay(1500);
            minutes.LoadDemo("summary");
            await Task.Delay(1000);
            Check(minutes.IsVisible, "議事録の画面を開く");
            Check(Render(minutes, Path.Combine(dir, "smoke_minutes.png")), "議事録の画面を描く");

            text.OpenSettings(SettingsPage.Meetings);
            await Task.Delay(1000);
            var settings = desktop.Windows.OfType<SettingsWindow>().FirstOrDefault();
            Check(settings is { IsVisible: true }, "設定の画面を開く");
            if (settings != null) Check(Render(settings, Path.Combine(dir, "smoke_settings.png")), "設定の画面を描く");

            if (MacHelper.IsAvailable)
            {
                var hello = await MacHelper.Instance.RequestAsync("hello");
                Check(true, $"アプリから補助プログラムを使う (macOS {hello["macos"]})");
                var (screen, mic) = await MacServices.PermissionsAsync();
                report.AppendLine($"--  許可: 画面収録 {(screen ? "あり" : "なし")} ・ マイク {mic}");

                // 画面の録画: 画面全体を 3 秒、音も入れて MP4 に録画し、書き終えたファイルを確かめる
                var targets = await MacHelper.Instance.RequestAsync("record_targets", timeout: TimeSpan.FromSeconds(15));
                int display = targets["displays"]?.AsArray().FirstOrDefault()?["id"]?.GetValue<int>() ?? 0;
                Check(display != 0, $"録画するものの一覧 (ウィンドウ {targets["windows"]?.AsArray().Count} 個・画面 {targets["displays"]?.AsArray().Count} 個)");
                // 録画の画面のプレビュー (画面全体を小さな JPEG に)
                var preview = await MacHelper.Instance.RequestAsync("record_preview", new System.Text.Json.Nodes.JsonObject
                {
                    ["window"] = 0, ["display"] = display, ["max"] = 640,
                }, TimeSpan.FromSeconds(15));
                var jpeg = Convert.FromBase64String(preview["jpeg"]?.GetValue<string>() ?? "");
                Check(jpeg.Length > 1000 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, $"録画のプレビューを作る ({preview["w"]}×{preview["h"]}・{jpeg.Length / 1024} KB)");
                File.WriteAllBytes(Path.Combine(dir, "smoke_record_preview.jpg"), jpeg);
                var movie = Path.Combine(dir, "smoke_record.mp4");
                var started = await MacHelper.Instance.RequestAsync("record_start", new System.Text.Json.Nodes.JsonObject
                {
                    ["window"] = 0, ["display"] = display, ["audio"] = true, ["fps"] = 30, ["hq"] = false, ["path"] = movie,
                }, TimeSpan.FromSeconds(20));
                Check(true, $"画面の録画を始める ({started["w"]}×{started["h"]})");
                text.ShowDemo("録画のテスト", "録画のテスト", "Mac の文字認識", "ローカル翻訳", "見本"); // 録画中に画面を変える
                await Task.Delay(3000);
                var stopped = await MacHelper.Instance.RequestAsync("record_stop", timeout: TimeSpan.FromSeconds(60));
                double seconds = stopped["seconds"]?.GetValue<double>() ?? 0;
                long size = File.Exists(movie) ? new FileInfo(movie).Length : 0;
                Check(seconds > 2.5 && size > 20_000, $"画面の録画を止めて書き終える (止めた時刻まで入る) ({seconds:0.0} 秒・{size / 1024} KB)");
                if (File.Exists(movie)) File.Delete(movie); // (CI の結果には大きいので残さない)
            }
            else
            {
                Check(!OperatingSystem.IsMacOS(), "補助プログラムがアプリに入っている: " + MacHelper.HelperPath);
            }
        }
        catch (Exception ex)
        {
            Check(false, "例外: " + ex);
        }
        report.Insert(0, $"実際の画面での起動の確認 ・ {(failed == 0 ? "すべて合格" : $"不合格 {failed} 件")}\n");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        Console.WriteLine(report);
        desktop.Shutdown(failed == 0 ? 0 : 2);
    }

    private static bool Render(Window w, string path)
    {
        try
        {
            double scale = w.RenderScaling > 0 ? w.RenderScaling : 1;
            var size = new PixelSize(Math.Max(1, (int)(w.Bounds.Width * scale)), Math.Max(1, (int)(w.Bounds.Height * scale)));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(w);
            bitmap.Save(path);
            return true;
        }
        catch (Exception ex)
        {
            App.Log("SmokeRender", ex);
            return false;
        }
    }
}
