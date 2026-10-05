using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace GetText;

/// <summary>
/// 発言の音声を聞く範囲の確認 (GetText.exe --selftest-playback 会議.wav 出力フォルダ [再生先の端末 ID] [聞く発言の数])。
/// 会議アプリの代わりに別のプロセスで会議の音声を再生して記録と同じ方法で議事録にし (音声も保存して .m4a に変換)、
/// 真ん中あたりの発言を議事録の画面の再生の処理で 1 つずつ再生して、GetText が実際に出した音を取り込む。
/// 出力: minutes.json (議事録)・rec.m4a (保存した音声)・play_NN.wav (発言ごとに実際に流れた音)・play.json (発言と範囲)。
/// 流れた音が保存した音声のどこからどこまでかは、別に照らし合わせて確かめる。
/// </summary>
internal static class PlaybackSelfTest
{
    private sealed record Played(int Index, string Text, double Position, double Span, double From, double Until,
        double? PrevEnd, double? NextStart, string Wav, double CapturedSeconds);

    public static async Task<int> RunAsync(string wavPath, string outDir, string? deviceId, int count)
    {
        Directory.CreateDirectory(outDir);
        var dispatcher = Application.Current.Dispatcher;
        double seconds = MinutesSelfTest.ReadWav(wavPath).Length / 2.0 / SessionRecorder.SampleRate;

        // 1. 記録 (CaptureSelfTest と同じ経路)。記録済みなら、聞く所からやり直す
        if (!File.Exists(Path.Combine(outDir, "minutes.json"))) await RecordAsync(wavPath, outDir, deviceId, seconds, dispatcher);
        await PlayAsync(outDir, count);
        return 0;
    }

    private static async Task RecordAsync(string wavPath, string outDir, string? deviceId, double seconds, System.Windows.Threading.Dispatcher dispatcher)
    {
        using var service = new TranscriptionService();
        service.InitialSpeed = AppSettings.Load().MinutesSpeed; // 利用者の「速さ」の設定で
        var doc = new MinutesDocument { ShowTranslations = false };
        var lastResult = DateTime.Now;
        service.SegmentReceived += s => dispatcher.Invoke(() => { doc.Add(s.SessionStart ?? service.SessionStart, s); lastResult = DateTime.Now; });
        service.Revised += r => dispatcher.Invoke(() => { doc.Revise(r); lastResult = DateTime.Now; });
        service.Respeaker += c => dispatcher.Invoke(() => { doc.ApplySpeakerChanges(c); lastResult = DateTime.Now; });
        await service.EnsureStartedAsync();
        service.SetLanguages(["ja"]);
        var player = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--play-wav \"{wavPath}\" {deviceId ?? ""}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
        })!;
        var wav = Path.Combine(outDir, "rec.wav");
        var m4a = Path.Combine(outDir, "rec.m4a");
        try
        {
            await player.StandardOutput.ReadLineAsync();
            service.RecordAudioPath = wav;
            await service.StartAsync(player.Id, excludeProcess: false, includeMic: false, newSession: true);
            await player.StandardInput.WriteLineAsync("go");
            await player.StandardInput.FlushAsync();
            await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(seconds + 60));
            await Task.Delay(1500);
            await service.StopAsync();
            await Task.WhenAny(service.Flushed, Task.Delay(TimeSpan.FromMinutes(10)));
            lastResult = DateTime.Now;
            var waitStart = DateTime.Now;
            while ((DateTime.Now - lastResult).TotalSeconds < 15 && (DateTime.Now - waitStart).TotalMinutes < 5)
                await Task.Delay(500);
        }
        finally
        {
            if (!player.HasExited) player.Kill();
        }
        var rec = service.LastRecording!;
        await service.EncodeAudioAsync(wav, m4a); // 記録を止めたときと同じ変換
        await dispatcher.InvokeAsync(() =>
        {
            doc.AudioParts.Add(new AudioPart(m4a, rec.Start, rec.Duration.TotalSeconds));
            File.WriteAllText(Path.Combine(outDir, "minutes.json"), doc.ToFile().ToJson(), new UTF8Encoding(false));
        });
    }

    private static async Task PlayAsync(string outDir, int count)
    {

        // 2. 議事録の画面で、真ん中あたりの発言を 1 つずつ再生し、GetText が出した音を取り込む
        App.DemoMode = true; // 利用者の設定・自動保存を書き換えない
        App.ApplyTheme(AppTheme.Light);
        var settings = new AppSettings();
        var text = new TextWindow(new CaptureWindow(settings), settings);
        var w = new MinutesWindow(text, settings)
        {
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        w.Show();
        var file = MinutesFile.FromJson(File.ReadAllText(Path.Combine(outDir, "minutes.json")));
        w.LoadForTest(file);
        var entries = w.Document.Entries.Where(e => !e.IsNote).ToList();
        int first = Math.Max(0, entries.Count / 2 - count / 2);
        var chosen = entries.Skip(first).Take(count).ToList();

        var captured = new MemoryStream();
        var capture = AudioCapture.ForProcess(Environment.ProcessId);
        capture.DataAvailable += data => { lock (captured) captured.Write(data); };
        capture.Start();
        await Task.Delay(500);
        var results = new List<Played>();
        foreach (var entry in chosen)
        {
            long mark;
            lock (captured) mark = captured.Length;
            w.PlayForTest(entry);
            var started = DateTime.Now;
            await Task.Delay(300);
            while (w.IsPlayingForTest && (DateTime.Now - started).TotalSeconds < 60) await Task.Delay(20);
            await Task.Delay(700); // 止まった後に音が続いていないかも見る
            byte[] pcm;
            lock (captured) pcm = captured.ToArray()[(int)mark..];
            int index = entries.IndexOf(entry);
            var name = $"play_{index:000}.wav";
            WriteWav(Path.Combine(outDir, name), pcm);
            var range = w.Document.PlayRange(entry)!.Value;
            var pos = w.Document.AudioAt(entry)!.Value.Position.TotalSeconds;
            var prev = index > 0 ? w.Document.AudioAt(entries[index - 1])!.Value.Position.TotalSeconds + entries[index - 1].Span.TotalSeconds : (double?)null;
            var next = index + 1 < entries.Count ? w.Document.AudioAt(entries[index + 1])!.Value.Position.TotalSeconds : (double?)null;
            results.Add(new Played(index, entry.Text, pos, entry.Span.TotalSeconds, range.From.TotalSeconds, range.Until.TotalSeconds,
                prev, next, name, pcm.Length / 2.0 / SessionRecorder.SampleRate));
            await Task.Delay(300);
        }
        capture.Stop();
        File.WriteAllText(Path.Combine(outDir, "play.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new UTF8Encoding(false));
        w.Close();
    }

    // 16kHz・モノラル・16bit の WAV
    private static void WriteWav(string path, byte[] pcm)
    {
        using var f = new BinaryWriter(File.Create(path));
        int rate = SessionRecorder.SampleRate;
        f.Write("RIFF"u8); f.Write(36 + pcm.Length); f.Write("WAVEfmt "u8); f.Write(16); f.Write((short)1); f.Write((short)1);
        f.Write(rate); f.Write(rate * 2); f.Write((short)2); f.Write((short)16); f.Write("data"u8); f.Write(pcm.Length); f.Write(pcm);
    }
}
