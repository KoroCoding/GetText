using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace GetText;

/// <summary>
/// 実際のアプリの音を取り込む経路の確認 (GetText.exe --selftest-capture 会議.wav 正解.json 出力フォルダ [再生先の端末 ID])。
/// 会議アプリの代わりに別のプロセス (GetText.exe --play-wav) で会議の音声を再生し、記録と同じ方法
/// (そのアプリの音だけの取り込み → 文字起こし → 話者の聞き分け) で議事録にして、正解と比べる。
/// 再生先の端末 ID (VB-Audio Virtual Cable の「CABLE Input」など、スピーカーにつながらない端末) を渡すと音は鳴らない。
/// 渡さなければ既定のスピーカーから鳴る。結果は 出力フォルダ/capture_report.txt に書く。
/// </summary>
internal static class CaptureSelfTest
{
    public static async Task<int> RunAsync(string wavPath, string truthPath, string outDir, string? deviceId)
    {
        Directory.CreateDirectory(outDir);
        var dispatcher = Application.Current.Dispatcher;
        var report = new StringBuilder();
        int failed = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine((ok ? "OK  " : "NG  ") + what);
            if (!ok) failed++;
        }

        var truth = JsonSerializer.Deserialize<List<MinutesSelfTest.Truth>>(File.ReadAllText(truthPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        double seconds = MinutesSelfTest.ReadWav(wavPath).Length / 2.0 / SessionRecorder.SampleRate;

        using var service = new TranscriptionService();
        var doc = new MinutesDocument { ShowTranslations = false };
        var lastResult = DateTime.Now;
        var errors = new List<string>();
        var lost = new List<string>();
        service.SegmentReceived += s => dispatcher.Invoke(() => { doc.Add(s.SessionStart ?? service.SessionStart, s); lastResult = DateTime.Now; });
        service.Revised += r => dispatcher.Invoke(() => { doc.Revise(r); lastResult = DateTime.Now; });
        service.Respeaker += c => dispatcher.Invoke(() => { doc.ApplySpeakerChanges(c); lastResult = DateTime.Now; });
        service.Error += m => { lock (errors) errors.Add(m); };
        service.CaptureLost += m => { lock (lost) lost.Add(m); };
        await service.EnsureStartedAsync();
        service.SetLanguages(["ja", "en"]);

        // 会議アプリの代わり: 取り込みの準備ができるまで待ってから再生するプロセス
        var player = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--play-wav \"{wavPath}\" {deviceId ?? ""}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
        })!;
        // 「会議の画面も録画する」の代わり: 見本の画面と、再生するプロセスの音を録画する (あとで字幕を焼き込む)
        Window? screen = null;
        ScreenRecorder? video = null;
        try
        {
            var ready = await player.StandardOutput.ReadLineAsync();
            Check(ready == "ready", $"再生するプロセスの準備 ({ready})");
            var wav = Path.Combine(outDir, "capture_音声1.wav");
            service.RecordAudioPath = wav;
            await service.StartAsync(player.Id, excludeProcess: false, includeMic: false, newSession: true);
            if (ScreenRecorder.IsSupported)
            {
                screen = new Window
                {
                    Title = "GetText 録画の確認", Width = 640, Height = 360, Left = 40, Top = 40, ShowActivated = false, Topmost = true,
                    WindowStyle = WindowStyle.ToolWindow, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 90, 140)),
                    Content = new System.Windows.Controls.TextBlock { Text = "会議の画面 (録画の確認)", FontSize = 28, Foreground = System.Windows.Media.Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                screen.Show();
                await Task.Delay(500);
                video = await ScreenRecorder.StartWindowAsync(new System.Windows.Interop.WindowInteropHelper(screen).Handle, player.Id,
                    Path.Combine(outDir, "capture_録画.mp4"), new RecordOptions(Fps: 30));
            }
            var start = service.SessionStart;
            await player.StandardInput.WriteLineAsync("go");
            await player.StandardInput.FlushAsync();

            // 再生中に、そのアプリが音を出していると分かるか (一覧の 🔊)
            await Task.Delay(5000);
            bool playing = AudioSessions.PlayingProcessTree().Contains(player.Id);
            Check(playing, "再生中のアプリを「音を出している」と判定する (一覧の 🔊)");

            await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(seconds + 60));
            var played = await player.StandardOutput.ReadToEndAsync();
            await Task.Delay(1500);
            if (video != null)
            {
                try { await video.StopAsync(); }
                catch (Exception ex) { Check(false, "会議の画面の録画を書き終える: " + ex.Message); video = null; }
            }
            screen?.Close();
            await service.StopAsync();
            await Task.WhenAny(service.Flushed, Task.Delay(TimeSpan.FromMinutes(10)));
            lastResult = DateTime.Now;
            var waitStart = DateTime.Now;
            while ((DateTime.Now - lastResult).TotalSeconds < 15 && (DateTime.Now - waitStart).TotalMinutes < 5)
                await Task.Delay(500);
            report.Insert(0, $"    再生: {played.Trim()}\n");

            var rec = service.LastRecording;
            Check(rec != null && rec.Duration.TotalSeconds >= seconds - 0.5,
                $"取り込んだ音声の保存 (WAV {rec?.Duration.TotalSeconds:0.0} 秒 / 再生した音声 {seconds:0.0} 秒)");
            lock (lost) Check(lost.Count == 0, $"取り込みが途中で止まらない{(lost.Count > 0 ? ": " + string.Join(" / ", lost) : "")}");

            await dispatcher.InvokeAsync(() =>
            {
                var entries = doc.Entries.ToList();
                Check(entries.Count >= truth.Count * 0.7, $"発言の数 ({entries.Count} / 正解 {truth.Count})");
                var said = MinutesSelfTest.Normalize(string.Concat(truth.Select(t => t.Text)));
                var heard = MinutesSelfTest.Normalize(string.Concat(entries.Select(e => e.Text)));
                double cer = MinutesSelfTest.Levenshtein(said, heard) / (double)Math.Max(1, said.Length);
                Check(cer <= 0.15, $"文字起こしの誤り率 {cer:P1} (15% 以下で合格)");

                // 発言の時刻: 取り込んだ音の時刻が、再生した音声の時刻とずれていないか (最初の発言で比べる)
                var first = entries.FirstOrDefault();
                double offset = first == null ? double.NaN : (first.Time - start).TotalSeconds - truth[0].Start;
                Check(Math.Abs(offset) < 2.0, $"発言の時刻のずれ {Signed(offset)} 秒 (取り込みの開始の遅れを含む・2 秒未満で合格)");
                // 後半で、正解の発言の始まりに合う最後の発言と比べる (軽さ優先では「…。お疲れさまでした。」の後半が別の発言になり、
                // 正解の発言の途中から始まるので、最後の発言そのものでは比べられない)
                var aligned = entries.Skip(1)
                    .Select(e => (e.Time - start).TotalSeconds - offset)
                    .Select(t => truth.Select(tr => t - tr.Start).OrderBy(Math.Abs).First())
                    .Where(d => Math.Abs(d) < 3.0)
                    .DefaultIfEmpty(double.NaN)
                    .Last();
                Check(Math.Abs(aligned) < 1.5, $"最後の発言までの時刻のずれの変化 {Signed(aligned)} 秒 (長く取り込んでも時刻がずれていかない)");

                int speakers = entries.Select(e => e.Speaker).Distinct().Count(s => entries.Where(e => e.Speaker == s).Sum(e => e.Duration.TotalSeconds) >= 4);
                Check(speakers == truth.Select(t => t.Speaker).Distinct().Count(), $"話者の数 ({speakers} 人 / 正解 {truth.Select(t => t.Speaker).Distinct().Count()} 人)");
                File.WriteAllText(Path.Combine(outDir, "capture_議事録.txt"), doc.ToPlainText(), new UTF8Encoding(false));
            });

            // 録画に字幕を焼き込む (議事録の画面で記録を止めたときと同じ処理)
            if (video != null)
            {
                Check(video.Duration.TotalSeconds >= seconds - 1,
                    $"会議の画面の録画 ({video.Duration.TotalSeconds:0.0} 秒・{video.FileSize / 1024} KB / 再生した音声 {seconds:0.0} 秒)");
                var cues = await dispatcher.InvokeAsync(() => doc.SubtitleCues(null, video.StartedAt, video.Duration));
                var firstCue = cues.FirstOrDefault();
                // 録画の先頭から最初の字幕まで = 録画を始めてから再生を始めるまで + 最初の発言の時刻
                double expected = (start - video.StartedAt).TotalSeconds + truth[0].Start;
                Check(firstCue != null && Math.Abs(firstCue.Start.TotalSeconds - expected) < 2.5,
                    $"録画の字幕の時刻 (最初の字幕 {firstCue?.Start.TotalSeconds:0.0} 秒 / 見込み {expected:0.0} 秒)");
                File.WriteAllText(Path.Combine(outDir, "capture_録画.srt"),
                    await dispatcher.InvokeAsync(() => doc.ToSubtitles(false, null, video.StartedAt, video.Duration)), new UTF8Encoding(false));
                var burned = Path.Combine(outDir, "capture_録画_字幕.mp4");
                double lastDone = 0;
                var watch = Stopwatch.StartNew();
                try
                {
                    await service.BurnSubtitlesAsync(video.Path, burned, cues, (done, total) => lastDone = done, CancellationToken.None)
                        .WaitAsync(TimeSpan.FromMinutes(5));
                    Check(File.Exists(burned) && new FileInfo(burned).Length > 50_000 && lastDone > seconds * 0.8,
                        $"字幕つきの動画 ({(File.Exists(burned) ? new FileInfo(burned).Length / 1024 : 0)} KB・{watch.Elapsed.TotalSeconds:0.0} 秒で作成・進み具合 {lastDone:0} 秒まで)");
                }
                catch (Exception ex)
                {
                    Check(false, "字幕つきの動画: " + ex.Message);
                }
                Check(!File.Exists(burned + ".part"), "作りかけのファイルが残らない");
            }
            lock (errors) Check(errors.Count == 0, $"エラーの通知なし{(errors.Count > 0 ? ": " + string.Join(" / ", errors) : "")}");
        }
        finally
        {
            if (!player.HasExited) player.Kill();
            if (video != null) try { await video.StopAsync(); } catch { }
            screen?.Close();
        }

        report.Insert(0, $"実際のアプリの音の取り込みの確認 ・ {(failed == 0 ? "すべて合格" : $"不合格 {failed} 件")} ・ 再生先 {(deviceId == null ? "既定のスピーカー" : "指定した端末 (" + deviceId + ")")}\n");
        File.WriteAllText(Path.Combine(outDir, "capture_report.txt"), report.ToString(), new UTF8Encoding(false));
        return failed == 0 ? 0 : 2;
    }

    /// <summary>
    /// --play-wav 会議.wav [端末 ID]: 16kHz・モノラル・16bit の WAV を再生する (会議アプリの代わり)。
    /// 「ready」を出して標準入力の合図を待ち、合図が来たら再生する。
    /// </summary>
    public static int PlayWav(string path, string? deviceId)
    {
        var pcm = MinutesSelfTest.ReadWav(path);
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice device;
        if (string.IsNullOrEmpty(deviceId)) Check(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out device));
        else Check(enumerator.GetDevice(deviceId, out device));
        var iid = typeof(IAudioClient).GUID;
        Check(device.Activate(ref iid, 0x17, IntPtr.Zero, out var o));
        var client = (IAudioClient)o;
        var format = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = 16000, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = 32000 };
        Check(client.Initialize(0, 0x80000000 | 0x08000000 /* AUTOCONVERTPCM | SRC_DEFAULT_QUALITY */, 10_000_000, 0, ref format, IntPtr.Zero));
        var rid = typeof(IAudioRenderClient).GUID;
        Check(client.GetService(ref rid, out var r));
        var render = (IAudioRenderClient)r;
        Check(client.GetBufferSize(out uint bufferFrames));
        Console.WriteLine("ready");
        Console.Out.Flush();
        Console.In.ReadLine();
        var sw = Stopwatch.StartNew();
        Check(client.Start());
        int pos = 0;
        while (pos < pcm.Length / 2)
        {
            Check(client.GetCurrentPadding(out uint padding));
            int n = Math.Min((int)(bufferFrames - padding), pcm.Length / 2 - pos);
            if (n > 0)
            {
                Check(render.GetBuffer((uint)n, out IntPtr buffer));
                Marshal.Copy(pcm, pos * 2, buffer, n * 2);
                Check(render.ReleaseBuffer((uint)n, 0));
                pos += n;
            }
            Thread.Sleep(20);
        }
        while (client.GetCurrentPadding(out uint left) >= 0 && left > 0) Thread.Sleep(20);
        client.Stop();
        Console.WriteLine($"{pcm.Length / 2 / 16000.0:0.0} 秒の音声を {sw.Elapsed.TotalSeconds:0.0} 秒で再生");
        return 0;
    }

    private static string Signed(double x) => (Math.Abs(x) < 0.05 ? 0 : x).ToString("+0.0;-0.0;0.0");

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public int nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WAVEFORMATEX format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}
