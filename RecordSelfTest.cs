using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GetText;

/// <summary>
/// 画面の録画の確認 (GetText.exe --selftest-record 出力フォルダ [再生先の端末ID])。
/// 色と数字が変わる見本の窓を、別のプロセスが鳴らす 1kHz の音と一緒に 5 秒録画し、MP4 の長さ・大きさ・音と画面の中身を確かめる。
/// 再生先に VB-Audio Virtual Cable などスピーカーにつながらない端末を渡すと、音は鳴らない。
/// </summary>
internal static class RecordSelfTest
{
    /// <param name="fps">録画のコマ数。60 より多いときは、いちばん速い画面に、毎回の描画で変わる窓を出して、実際に取り込めたコマ数を確かめる。</param>
    public static async Task<int> RunAsync(string outDir, string? deviceId, int fps = 30)
    {
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        int failed = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine((ok ? "OK  " : "NG  ") + what);
            if (!ok) failed++;
        }

        Check(ScreenRecorder.IsSupported, "この Windows で画面を取り込める (Windows Graphics Capture)");
        var tone = Path.Combine(outDir, "tone.wav");
        WriteTone(tone, seconds: 7);
        var player = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--play-wav \"{tone}\" {deviceId ?? ""}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        })!;
        var path = Path.Combine(outDir, "record_test.mp4");
        Window? window = null;
        DispatcherTimer? timer = null;
        try
        {
            Check(await player.StandardOutput.ReadLineAsync() == "ready", "音を鳴らすプロセスの準備");
            // 色と数字が 0.15 秒ごとに変わる見本の窓 (画面の左上に 5 秒だけ出す)
            var area = SystemParameters.WorkArea;
            var label = new TextBlock { FontSize = 72, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            window = new Window
            {
                Title = "GetText 録画の確認", Width = 480, Height = 320, Left = area.Left + 24, Top = area.Top + 24,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                Content = new Grid { Children = { label } }, Background = Brushes.Crimson,
            };
            window.Show();
            Brush[] colors = [Brushes.Crimson, Brushes.SeaGreen, Brushes.RoyalBlue, Brushes.DarkOrange];
            int n = 0;
            int hz = 60;
            EventHandler? everyFrame = null;
            if (fps > 60)
            {
                // いちばん速い画面に出し、描画のたびに数字を変える (画面の更新の速さだけ新しい画面ができる)
                var (monitorLeft, monitorTop, monitorHz) = FastestMonitor();
                hz = monitorHz;
                ScreenUtil.MoveTo(window, new Point(monitorLeft + 40, monitorTop + 40));
                everyFrame = (_, _) => label.Text = (++n).ToString();
                CompositionTarget.Rendering += everyFrame;
                window.Background = Brushes.SeaGreen;
            }
            else
            {
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                timer.Tick += (_, _) =>
                {
                    n++;
                    window.Background = colors[n % colors.Length];
                    label.Text = n.ToString();
                };
                timer.Start();
            }
            await Task.Delay(500);

            var hwnd = new WindowInteropHelper(window).Handle;
            Check(!ScreenRecorder.IsCaptureProtected(hwnd), "見本の窓は取り込みを禁止していない");

            // 録画する前のプレビュー: 見本の窓が映り (黒くない)、上にタイトルバー (明るい) がある
            using (var preview = CapturePreview.TryStart(hwnd, isMonitor: false))
            {
                byte[]? frame = null;
                int pw = 0, ph = 0;
                for (int i = 0; i < 30 && frame == null; i++)
                {
                    await Task.Delay(100);
                    if (preview?.TryTake(out var px, out pw, out ph) == true) frame = px;
                }
                Check(frame != null && LooksLikeSample(frame, pw, ph, bottomUp: false),
                    $"録画する前のプレビュー ({pw}×{ph}{(frame != null ? "・" + Describe(frame, pw, ph, false) : "・映らない")})");
            }
            var sw = Stopwatch.StartNew();
            var recorder = await ScreenRecorder.StartWindowAsync(hwnd, player.Id, path, new RecordOptions(Fps: fps, WindowAudio: true));
            Check(true, $"録画を始める ({recorder.Width}×{recorder.Height}、準備 {sw.ElapsedMilliseconds} ms)");
            // 音を鳴らし始めるのと同時に、窓を白くする (録画で、音の出だしと白い画面の時刻を比べて、音と映像のずれを確かめるため。
            // 確かめ方: offline\av_sync_check.py record_test.mp4)
            timer?.Stop();
            if (timer != null)
            {
                window.Background = Brushes.White;
                label.Text = "";
            }
            await player.StandardInput.WriteLineAsync(); // 音を鳴らし始める
            await player.StandardInput.FlushAsync();
            await Task.Delay(400);
            timer?.Start();
            await Task.Delay(2100);
            // 録画中のプレビュー: 録画している画面 (下の行から並ぶ) が見本の窓の絵になっている
            bool previewOk = false;
            string previewText = "";
            recorder.CopyPreview((px, w, h) =>
            {
                previewOk = LooksLikeSample(px, w, h, bottomUp: true);
                previewText = $"{w}×{h}・{Describe(px, w, h, true)}";
            });
            Check(previewOk, $"録画中のプレビュー ({previewText})");
            await Task.Delay(2500);
            long sizeWhile = recorder.FileSize;
            sw.Restart();
            await recorder.StopAsync();
            Check(true, $"録画を止めて書き終える ({sw.ElapsedMilliseconds} ms、録画中のファイル {sizeWhile / 1024} KB)");
            if (everyFrame != null) CompositionTarget.Rendering -= everyFrame;
            if (fps > 60)
            {
                // Windows が渡す画面の数は画面の更新より少ないことがある (この PC の 180Hz では約 115)。
                // 既定の上限 (60) を超えて受け取れていること・届いた画面を取りこぼさないことを確かめる
                Check(recorder.ArrivedFps > 75 && recorder.CapturedFps >= recorder.ArrivedFps * 0.9,
                    $"{fps} コマ/秒で録画して、取り込めた画面 {recorder.CapturedFps:0} コマ/秒 (画面 {hz}Hz・描画 {n / recorder.Duration.TotalSeconds:0} 回/秒・届いた画面 {recorder.ArrivedFps:0}・書き出し {recorder.EncoderFps}、届いた画面の 9 割以上・60 より多くで合格)");
            }

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var video = await file.Properties.GetVideoPropertiesAsync();
            Check(video.Duration.TotalSeconds is > 4.3 and < 6.5, $"動画の長さ ({video.Duration.TotalSeconds:0.0} 秒 / 録画 {recorder.Duration.TotalSeconds:0.0} 秒)");
            Check(video.Width == recorder.Width && video.Height == recorder.Height, $"動画の大きさ ({video.Width}×{video.Height})");
            var size = new FileInfo(path).Length;
            Check(size > 50_000, $"ファイルの大きさ ({size / 1024} KB)");
        }
        catch (Exception ex)
        {
            Check(false, "録画できませんでした: " + ex);
        }
        finally
        {
            timer?.Stop();
            window?.Close();
            try { if (!player.HasExited) player.Kill(); } catch { }
        }
        report.Insert(0, $"画面の録画の確認 ・ {(failed == 0 ? "すべて合格" : $"不合格 {failed} 件")} ・ 再生先 {(deviceId == null ? "既定のスピーカー" : deviceId)}\n");
        File.WriteAllText(Path.Combine(outDir, "record_report.txt"), report.ToString(), new UTF8Encoding(false));
        return failed == 0 ? 0 : 2;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size, Left, Top, Right, Bottom, WorkLeft, WorkTop, WorkRight, WorkBottom, Flags; }

    // いちばん更新の速い画面の作業領域の左上 (物理ピクセル) と、その速さ (Hz)
    private static (int Left, int Top, int Hz) FastestMonitor()
    {
        var best = (Left: 0, Top: 0, Hz: 0);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            int hz = RecorderWindow.RefreshRate(monitor, true) ?? 60;
            if (GetMonitorInfo(monitor, ref info) && hz > best.Hz) best = (info.WorkLeft, info.WorkTop, hz);
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // 見本の窓の絵か: 上の方 (タイトルバー) が明るく、真ん中が色付き (黒くない)
    private static bool LooksLikeSample(byte[] px, int w, int h, bool bottomUp)
    {
        var (top, middle) = Sample(px, w, h, bottomUp);
        return top > 120 && top > middle + 40 && middle > 30 && middle < 200; // (タイトルバーの明るさは画面・テーマで変わる)
    }

    private static string Describe(byte[] px, int w, int h, bool bottomUp)
    {
        var (top, middle) = Sample(px, w, h, bottomUp);
        return $"上の明るさ {top:0}・真ん中 {middle:0}";
    }

    private static (double Top, double Middle) Sample(byte[] px, int w, int h, bool bottomUp)
    {
        double Row(int y)
        {
            int row = bottomUp ? h - 1 - y : y;
            double sum = 0;
            for (int x = w / 4; x < w * 3 / 4; x++)
            {
                int i = (row * w + x) * 4;
                sum += (px[i] + px[i + 1] + px[i + 2]) / 3.0;
            }
            return sum / (w / 2);
        }
        return (Row(Math.Min(h - 1, 8)), Row(h / 2 + h / 6));
    }

    /// <summary>1kHz の音 (16kHz・モノラル・16bit の WAV)。</summary>
    private static void WriteTone(string path, double seconds)
    {
        int rate = 16000, count = (int)(rate * seconds);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + count * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(count * 2);
        for (int i = 0; i < count; i++) w.Write((short)(Math.Sin(2 * Math.PI * 1000 * i / rate) * 9000));
    }
}
