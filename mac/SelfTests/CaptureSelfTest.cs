using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GetText;

/// <summary>
/// 実際に再生した音の取り込みの確認 (GetText --selftest-capture 会議.wav 正解.json フォルダ)。
/// 会議アプリの代わりに afplay で会議の音声を再生し、「すべての音」の取り込み (ScreenCaptureKit) で議事録にして正解と比べる。
/// 画面収録とシステムオーディオ録音の許可が要る (無ければ終了コード 3 で「確かめられない」と書く)。
/// </summary>
internal static class CaptureSelfTest
{
    public static async Task<int> RunAsync(string wavPath, string truthPath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        int failed = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine((ok ? "OK  " : "NG  ") + what);
            if (!ok) failed++;
        }
        int Finish(int? code = null)
        {
            report.Insert(0, $"実際に再生した音の取り込みの確認 ・ {(code == 3 ? "確かめられません" : failed == 0 ? "すべて合格" : $"不合格 {failed} 件")}\n");
            File.WriteAllText(Path.Combine(outDir, "capture_report.txt"), report.ToString(), new UTF8Encoding(false));
            Console.WriteLine(report);
            return code ?? (failed == 0 ? 0 : 2);
        }

        var (screen, _) = await MacServices.PermissionsAsync();
        if (!screen)
        {
            report.AppendLine("--  画面収録とシステムオーディオ録音の許可が無いため、音を取り込めません");
            return Finish(3);
        }
        var truth = JsonSerializer.Deserialize<List<MinutesSelfTest.Truth>>(File.ReadAllText(truthPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        double seconds = MinutesSelfTest.ReadWav(wavPath).Length / 2.0 / AudioSources.SampleRate;
        var doc = new MinutesDocument { ShowTranslations = false };
        var gate = new object();
        var lastResult = DateTime.Now;
        using var service = new TranscriptionService();
        service.SegmentReceived += s => { lock (gate) { doc.Add(service.SessionStart, s); lastResult = DateTime.Now; } };
        service.Revised += r => { lock (gate) { doc.Revise(r); lastResult = DateTime.Now; } };
        service.Respeaker += c => { lock (gate) { doc.ApplySpeakerChanges(c); lastResult = DateTime.Now; } };
        DateTime? firstSound = null; // 音が初めて届いた時刻 (再生のプログラムが鳴らし始めた時刻の目安)
        service.Level += (source, level) => { if (source == "win" && level > 0.02) firstSound ??= DateTime.Now; };
        await service.EnsureStartedAsync();
        service.SetLanguages(["ja", "en"]);
        service.RecordAudioPath = Path.Combine(outDir, "capture_音声1.wav");
        await service.StartAsync(Environment.ProcessId, excludeProcess: true, includeMic: false, newSession: true);
        var start = service.SessionStart;
        await Task.Delay(500);
        var launched = DateTime.Now;
        var player = Process.Start(new ProcessStartInfo("afplay", [wavPath]) { UseShellExecute = false })!;
        await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(seconds + 60));
        await Task.Delay(1500);
        await service.StopAsync();
        await Task.WhenAny(service.Flushed, Task.Delay(TimeSpan.FromMinutes(10)));
        lastResult = DateTime.Now;
        var waitStart = DateTime.Now;
        while ((DateTime.Now - lastResult).TotalSeconds < 15 && (DateTime.Now - waitStart).TotalMinutes < 5)
            await Task.Delay(500);

        lock (gate)
        {
            var rec = service.LastRecording;
            Check(rec != null && rec.Duration.TotalSeconds >= seconds - 0.5, $"取り込んだ音声の保存 ({rec?.Duration.TotalSeconds:0.0} 秒 / 再生 {seconds:0.0} 秒)");
            var entries = doc.Entries.ToList();
            Check(entries.Count >= truth.Count * 0.7, $"発言の数 ({entries.Count} / 正解 {truth.Count})");
            var said = MinutesSelfTest.Normalize(string.Concat(truth.Select(t => t.Text)));
            var heard = MinutesSelfTest.Normalize(string.Concat(entries.Select(e => e.Text)));
            double cer = MinutesSelfTest.Levenshtein(said, heard) / (double)Math.Max(1, said.Length);
            Check(cer <= 0.15, $"文字起こしの誤り率 {cer:P1} (15% 以下で合格)");
            // 再生のプログラム (afplay) は鳴らし始めるまでに時間がかかることがあるので、音が初めて届いた時刻と比べる
            double played = ((firstSound ?? launched) - start).TotalSeconds;
            report.AppendLine($"--  afplay を起動してから音が届くまで {((firstSound ?? launched) - launched).TotalSeconds:0.0} 秒");
            var first = entries.FirstOrDefault();
            double offset = first == null ? double.NaN : (first.Time - start).TotalSeconds - truth[0].Start - played;
            Check(Math.Abs(offset) < 1.5, $"発言の時刻のずれ {offset:+0.0;-0.0;0.0} 秒 (音が届き始めた時刻との差・1.5 秒未満で合格)");
            File.WriteAllText(Path.Combine(outDir, "capture_議事録.txt"), doc.ToPlainText(), new UTF8Encoding(false));
        }
        return Finish();
    }
}
