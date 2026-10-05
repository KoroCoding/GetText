using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GetText;

/// <summary>
/// 議事録の通しの動作確認 (GetText.exe --selftest-minutes 会議.wav 正解.json 出力フォルダ [nosummary])。
/// 正解の分かっている会議の音声 (tools/make_test_meeting.py で作る架空の会議) を、記録と同じ経路
/// (音声の保存 → 文字起こし → 話者の聞き分け → 補正) に流し、音声の変換・発言の位置・メモ・★・保存と読み込み・要約までを確かめる。
/// 結果は 出力フォルダ/selftest_report.txt に書き、すべて合格なら終了コード 0、不合格があれば 2。
/// </summary>
internal static class MinutesSelfTest
{
    /// <summary>処理を 1 つずつ順に行う (画面のスレッドの代わり。議事録のデータは画面に出さないので、鍵で順番を守れば足りる)。</summary>
    private sealed class SerialRunner
    {
        private readonly object _gate = new();
        public void Invoke(Action action) { lock (_gate) action(); }
        public Task InvokeAsync(Action action) { lock (_gate) action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) { lock (_gate) return Task.FromResult(func()); }
    }

    internal sealed record Truth(double Start, double End, string Speaker, string Text);

    public static async Task<int> RunAsync(string wavPath, string truthPath, string outDir, bool summary)
    {
        Directory.CreateDirectory(outDir);
        var dispatcher = new SerialRunner(); // 結果は届いた順に 1 つずつ議事録に入れる (Windows 版・Mac 版で共通)
        var report = new StringBuilder();
        int failed = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine((ok ? "OK  " : "NG  ") + what);
            if (!ok) failed++;
        }

        var truth = JsonSerializer.Deserialize<List<Truth>>(File.ReadAllText(truthPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var pcm = ReadWav(wavPath);
        double seconds = pcm.Length / 2.0 / SessionRecorder.SampleRate;

        using var service = new TranscriptionService();
        var doc = new MinutesDocument { ShowTranslations = false };
        var steps = new List<string>();
        var refined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<string>();
        service.LoadingStep += s => { lock (steps) steps.Add(s); };
        var lastResult = DateTime.Now; // 最後に結果 (発言・補正・話者の付け直し) が届いた時刻
        service.SegmentReceived += s => dispatcher.Invoke(() => { doc.Add(s.SessionStart ?? service.SessionStart, s); lastResult = DateTime.Now; });
        service.Revised += r => dispatcher.Invoke(() => { doc.Revise(r); lastResult = DateTime.Now; });
        service.Respeaker += c => dispatcher.Invoke(() => { doc.ApplySpeakerChanges(c); lastResult = DateTime.Now; });
        service.Refined += () => refined.TrySetResult();
        service.Error += m => { lock (errors) errors.Add(m); };

        // 1. 準備 (準備中の表示に使う段階の通知)
        var sw = Stopwatch.StartNew();
        await service.EnsureStartedAsync();
        Check(steps.Contains("asr") && steps.Contains("speakers"), $"準備中の段階の通知 ({string.Join(" → ", steps)} → 準備完了、{sw.Elapsed.TotalSeconds:0.0} 秒、{service.Device})");
        service.SetLanguages(["ja", "en"]);

        // 2. 記録 (音声も保存)。実際より速く (約 4 倍) 流す
        var wav = Path.Combine(outDir, "selftest_音声1.wav");
        service.RecordAudioPath = wav;
        await service.StartSimulatedAsync();
        var start = service.SessionStart;
        int chunk = SessionRecorder.SampleRate * 2 / 5; // 0.2 秒
        for (int i = 0; i < pcm.Length; i += chunk)
        {
            service.FeedAudio("win", pcm[i..Math.Min(pcm.Length, i + chunk)]);
            await Task.Delay(50);
        }
        await service.StopAsync();
        // 流した音声が最後まで文字になるまで待ち (CPU だけで、ほかの処理が重いと時間がかかるので最大 10 分)、
        // そのあと補正 (まとめて認識し直す・文脈の補正) が落ち着くまで、結果が 15 秒届かなくなるまで待つ
        await Task.WhenAny(service.Flushed, Task.Delay(TimeSpan.FromMinutes(10)));
        lastResult = DateTime.Now;
        var waitStart = DateTime.Now;
        while ((DateTime.Now - lastResult).TotalSeconds < 15 && (DateTime.Now - waitStart).TotalMinutes < 5)
            await Task.Delay(500);
        await Task.Delay(500);
        var rec = service.LastRecording;
        Check(rec != null && Math.Abs(rec.Duration.TotalSeconds - seconds) < 0.3,
            $"音声の保存 (WAV {rec?.Duration.TotalSeconds:0.0} 秒 / 流した音声 {seconds:0.0} 秒)");

        // 3. 小さな .m4a への変換
        var m4a = Path.ChangeExtension(wav, ".m4a");
        try
        {
            await service.EncodeAudioAsync(wav, m4a);
            var size = new FileInfo(m4a).Length;
            Check(size > 1000 && size < new FileInfo(wav).Length / 4, $"音声の変換 (WAV {new FileInfo(wav).Length / 1024} KB → m4a {size / 1024} KB)");
            File.Delete(wav);
        }
        catch (Exception ex)
        {
            Check(false, "音声の変換: " + ex.Message);
        }
        if (rec != null) doc.AudioParts.Add(new AudioPart(m4a, rec.Start, rec.Duration.TotalSeconds));

        await dispatcher.InvokeAsync(() =>
        {
            var entries = doc.Entries.ToList();
            Check(entries.Count >= truth.Count * 0.7, $"発言の数 ({entries.Count} / 正解 {truth.Count})");
            Check(entries.All(e => e.Language == "ja"), $"言語 ({string.Join(", ", entries.GroupBy(e => e.Language).Select(g => $"{g.Key} {g.Count()}"))})");

            // 4. 文字起こしの正しさ (文字の誤り率)
            var said = Normalize(string.Concat(truth.Select(t => t.Text)));
            var heard = Normalize(string.Concat(entries.Select(e => e.Text)));
            double cer = Levenshtein(said, heard) / (double)Math.Max(1, said.Length);
            Check(cer <= 0.15, $"文字起こしの誤り率 {cer:P1} (15% 以下で合格)");

            // 5. 話者の聞き分け (発言時間の多い 3 人、時間で重みを付けた正しさ)
            var bySpeaker = entries.GroupBy(e => e.Speaker).Select(g => (g.Key, Talk: g.Sum(e => e.Duration.TotalSeconds))).ToList();
            int major = bySpeaker.Count(s => s.Talk >= 4);
            double correct = 0, total = 0;
            var mapping = new Dictionary<MinutesSpeaker, string>();
            foreach (var g in entries.GroupBy(e => e.Speaker))
            {
                var votes = g.GroupBy(e => TruthAt(truth, (e.Time - start).TotalSeconds)?.Speaker ?? "?")
                    .Select(v => (v.Key, Sec: v.Sum(e => e.Duration.TotalSeconds))).OrderByDescending(v => v.Sec).First();
                mapping[g.Key] = votes.Key;
            }
            foreach (var e in entries)
            {
                var who = TruthAt(truth, (e.Time - start).TotalSeconds)?.Speaker;
                total += e.Duration.TotalSeconds;
                if (who != null && mapping[e.Speaker] == who) correct += e.Duration.TotalSeconds;
            }
            double acc = correct / Math.Max(1e-9, total);
            Check(major == 3, $"話者の人数 (主な話者 {major} 人 / 正解 3 人、全体 {bySpeaker.Count} 人)");
            Check(acc >= 0.85 && mapping.Values.Distinct().Count() >= 3, $"話者の聞き分けの正しさ {acc:P1} (85% 以上で合格)");

            // 6. 相づち
            Check(entries.Any(e => e.IsFiller), $"相づちの判定 ({entries.Count(e => e.IsFiller)} 件: {string.Join("、", entries.Where(e => e.IsFiller).Select(e => e.Text))})");

            // 7. 保存した音声から発言の位置を決める。正解の発言の中 (始まりの 1.5 秒前〜終わり) にあれば正しい
            //    (長い発言が途中で 2 つに分かれたとき、後半の始まりは正解の発言の途中になる)
            var diffs = entries.Select(e => doc.AudioAt(e) is { } a
                ? truth.Min(t => a.Position.TotalSeconds < t.Start - 1.5 ? t.Start - 1.5 - a.Position.TotalSeconds
                    : a.Position.TotalSeconds > t.End ? a.Position.TotalSeconds - t.End : 0) : double.NaN).ToList();
            int inside = diffs.Count(d => d == 0);
            Check(diffs.All(d => !double.IsNaN(d)) && inside >= entries.Count * 0.9,
                $"発言の音声の位置 ({inside}/{entries.Count} 件が正解の発言の中、外れた最大 {diffs.Where(d => !double.IsNaN(d)).DefaultIfEmpty(0).Max():0.00} 秒)");

            // 8. メモ・★・保存と読み込み
            if (entries.Count < 5)
            {
                Check(false, "メモ・★・保存と読み込み (発言が少なすぎて確かめられません)");
                return;
            }
            var memo = doc.AddNote(entries[3].Time.AddSeconds(0.5), "セルフテストのメモ");
            entries[4].IsMarked = true;
            Check(doc.Entries.IndexOf(memo) == 4 && !doc.Speakers.Any(s => s.Name == MinutesDocument.NoteName), "メモを時刻の位置に差し込む");
            foreach (var (kind, text) in new[] { ("Markdown", doc.ToMarkdown()), ("テキスト", doc.ToPlainText()), ("議事録データ", doc.ToFile().ToJson()) })
            {
                var back = new MinutesDocument();
                back.Load(kind == "議事録データ" ? MinutesFile.FromJson(text) : MinutesFile.FromText(text));
                bool ok = back.Entries.Count(e => e.IsNote) == 1 && back.Entries.Count(e => e.IsMarked) == 1
                          && back.Speakers.Count == doc.Speakers.Count(s => doc.Entries.Any(e => e.Speaker == s));
                Check(ok, $"{kind}で保存して読み戻す (メモ・★・話者)");
            }
            var srt = doc.ToSubtitles(vtt: false);
            // 発言ごとに 1 枚以上 (長い発言は読める長さに区切る)。1 枚の本文は 40 字まで
            var cues = srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            int longest = cues.Max(c => c.Split('\n')[2].Split(": ", 2)[^1].Length);
            Check(cues.Length >= doc.Entries.Count(e => !e.IsNote) && longest <= 40,
                $"字幕 (SRT) の書き出し ({cues.Length} 枚 / 発言 {doc.Entries.Count(e => !e.IsNote)} 件、1 枚の本文は最長 {longest} 字)");
        });

        // 9. 要約 (PC 内の AI)
        if (summary)
        {
            sw.Restart();
            try
            {
                var transcript = await dispatcher.InvokeAsync(doc.SummaryTranscript);
                var text = await service.SummarizeAsync(transcript);
                doc.Summary = text;
                bool shaped = text != null && new[] { "### 概要", "### 決定事項", "### やること", "### 主な論点" }.All(text.Contains);
                Check(shaped, $"要約 ({sw.Elapsed.TotalSeconds:0} 秒、見出し 4 つ)");
                Check(text != null && text.Contains("水曜"), "要約に「来週の水曜日まで (テスト結果の共有)」が入る");
                Check(text != null && !text.Contains("未定") && !text.Contains("未明記"), "要約に「未定」「未明記」を書かない");
            }
            catch (Exception ex)
            {
                Check(false, "要約: " + ex.Message);
            }
        }

        lock (errors) Check(errors.Count == 0, $"エラーの通知なし{(errors.Count > 0 ? ": " + string.Join(" / ", errors) : "")}");
        await dispatcher.InvokeAsync(() =>
        {
            File.WriteAllText(Path.Combine(outDir, "selftest_議事録.md"), doc.ToMarkdown(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outDir, "selftest_議事録.json"), doc.ToFile().ToJson(), new UTF8Encoding(false));
        });
        report.Insert(0, $"議事録のセルフテスト {DateTime.Now:yyyy-MM-dd HH:mm} ・ {(failed == 0 ? "すべて合格" : $"不合格 {failed} 件")}\n");
        File.WriteAllText(Path.Combine(outDir, "selftest_report.txt"), report.ToString(), new UTF8Encoding(false));
        Console.WriteLine(report);
        return failed == 0 ? 0 : 2;
    }

    internal static Truth? TruthAt(List<Truth> truth, double t) =>
        truth.FirstOrDefault(x => t >= x.Start - 0.6 && t <= x.End + 0.3) ?? truth.MinBy(x => Math.Abs(x.Start - t));

    internal static string Normalize(string s) =>
        new(s.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c) && "、。！？「」・ー".IndexOf(c) < 0).ToArray());

    internal static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // 16kHz・モノラル・16bit の WAV の音の部分
    internal static byte[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, pos, 4);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "fmt " && (BitConverter.ToInt16(bytes, pos + 10) != 1 || BitConverter.ToInt32(bytes, pos + 12) != SessionRecorder.SampleRate))
                throw new InvalidDataException("16kHz・モノラルの WAV を使ってください");
            if (id == "data") return bytes[(pos + 8)..Math.Min(bytes.Length, pos + 8 + size)];
            pos += 8 + size;
        }
        throw new InvalidDataException("WAV の音の部分が見つかりません");
    }
}
