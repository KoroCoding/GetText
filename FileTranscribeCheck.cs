using System.IO;
using System.Text;
using System.Windows;

namespace GetText;

/// <summary>
/// 画面を出さずに、動画・音声ファイルを議事録と同じ処理で文字起こしして Markdown に保存する
/// (GetText.exe --transcribe-file 入力 出力.md [言語])。「ファイルから…」の動作確認や、まとめて処理したいとき用。
/// 設定は保存しない (App.DemoMode)。
/// </summary>
internal static class FileTranscribeCheck
{
    public static async Task RunAsync(string input, string output, string language)
    {
        var dispatcher = Application.Current.Dispatcher;
        using var service = new TranscriptionService();
        var doc = new MinutesDocument { FromFile = true, Source = Path.GetFileName(input) };
        var refined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 画面と同じく、結果は UI スレッドで文書に反映する
        service.SegmentReceived += s => dispatcher.Invoke(() => doc.Add(service.SessionStart, s));
        service.Revised += r => dispatcher.Invoke(() => doc.Revise(r));
        service.Respeaker += c => dispatcher.Invoke(() => doc.ApplySpeakerChanges(c));
        service.Progress += (done, total) => Console.WriteLine($"{done / Math.Max(total, 1):P0}");
        service.Error += m => Console.Error.WriteLine("エラー: " + m);
        service.Refined += () => refined.TrySetResult();

        await service.EnsureStartedAsync();
        // 言語は "auto" (日本語と英語から判定) か、"ja" / "ja,en,zh" のような言語コードの並び
        service.SetLanguages(language == "auto" ? ["ja", "en"] : language.Split(',', StringSplitOptions.RemoveEmptyEntries));
        var started = DateTime.Now;
        var (duration, cancelled) = await service.TranscribeFileAsync(input);
        // 文脈による補正が最後の発言まで終わるのを少し待つ (GPU が無いと時間がかかるので上限あり)
        await Task.WhenAny(refined.Task, Task.Delay(TimeSpan.FromSeconds(90)));
        // 日本語以外の発言には、画面と同じく PC 内のモデルで日本語訳を付ける
        var foreign = await dispatcher.InvokeAsync(() => doc.Entries.Where(en => en.NeedsTranslation).ToList());
        if (foreign.Count > 0 && Translator.OfflineAvailable)
        {
            var translator = new Translator();
            try
            {
                var results = await translator.TranslateOfflineAsync(foreign.Select(en => en.Text).ToList(), CancellationToken.None);
                await dispatcher.InvokeAsync(() => { for (int i = 0; i < foreign.Count; i++) foreign[i].Translation = results[i]; });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("日本語訳を付けられませんでした: " + ex.Message);
            }
            finally
            {
                translator.Shutdown();
            }
        }
        await dispatcher.InvokeAsync(() =>
        {
            doc.Duration = TimeSpan.FromSeconds(duration);
            doc.SourcePath = Path.GetFullPath(input);
            File.WriteAllText(output, doc.ToMarkdown(), new UTF8Encoding(false));
            // 画面の「開く…」で開ける議事録データも一緒に書く
            File.WriteAllText(Path.ChangeExtension(output, ".json"), doc.ToFile().ToJson(), new UTF8Encoding(false));
        });
        Console.WriteLine($"完了: {doc.Entries.Count} 発言, 話者 {doc.Speakers.Count} 人, 音声 {duration:0} 秒, 処理 {(DateTime.Now - started).TotalSeconds:0} 秒, 中止={cancelled}, 端末={service.Device}");
    }
}



