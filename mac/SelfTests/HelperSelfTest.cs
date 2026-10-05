using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace GetText;

/// <summary>
/// 補助プログラムの確認 (GetText --selftest-helper 結果.txt)。文字認識 (Vision)・キーチェーン・ショートカットの登録・
/// ウィンドウの一覧を確かめ、画面と音の取り込みは許可があれば確かめる (許可が無ければ「許可なし」と書く)。
/// </summary>
internal static class HelperSelfTest
{
    public static async Task<int> RunAsync(string reportPath)
    {
        var report = new StringBuilder();
        int failed = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine((ok ? "OK  " : "NG  ") + what);
            if (!ok) failed++;
        }
        void Note(string what) => report.AppendLine("--  " + what);

        if (!MacHelper.IsAvailable)
        {
            Check(false, "補助プログラム (GetTextHelper) が見つかりません: " + MacHelper.HelperPath);
            return Finish();
        }
        var helper = MacHelper.Instance;
        try
        {
            var hello = await helper.RequestAsync("hello");
            Check(true, $"補助プログラムの起動 (macOS {hello["macos"]})");

            var self = await helper.RequestAsync("selftest", timeout: TimeSpan.FromSeconds(60));
            var ocr = self["ocr"]?.AsArray().Select(t => t!.GetValue<string>()).ToList() ?? [];
            var joined = string.Concat(ocr).Replace(" ", "");
            Check(joined.Contains("議事録") && joined.Contains("テスト") && joined.Contains("GetText") && joined.Contains("2026"),
                $"文字認識 (Vision) で日本語と英語を読む (読んだ文字: {string.Join(" / ", ocr)})");
            Check(self["secret"]?.GetValue<string>() == "秘密-123" && self["secret_deleted"]?.GetValue<bool>() == true,
                $"キーチェーンに保存・読み出し・削除する{(self["secret_error"] is { } se ? " (" + se + ")" : "")}");
            Check(self["hotkey"]?.GetValue<bool>() == true, "どのアプリでも効くショートカットを登録する");
            Note($"音を出しているアプリの判定 (macOS 14.4 以降): {(self["playing_supported"]?.GetValue<bool>() == true ? "使える" : "使えない")}");
            Note($"表示中のウィンドウ: {self["windows"]} 個");

            var windows = MacServices.GetWindows();
            Check(true, $"ウィンドウの一覧 ({windows.Count} 個: {string.Join(", ", windows.Take(5).Select(w => w.ProcessName))})");

            var (screen, mic) = await MacServices.PermissionsAsync();
            Note($"許可: 画面収録 {(screen ? "あり" : "なし")} ・ マイク {mic}");

            // 画面の一部の取り込みと文字認識 (許可が無いと失敗する)
            try
            {
                var shot = await helper.RequestAsync("capture_ocr", new JsonObject
                {
                    ["rect"] = new JsonArray(0, 0, 400, 200), ["force"] = true, ["scale"] = 1,
                }, TimeSpan.FromSeconds(20));
                Check(shot["w"]?.GetValue<int>() > 0, $"画面の一部を取り込んで読む ({shot["w"]}×{shot["h"]}、{shot["lines"]?.AsArray().Count ?? 0} 行)");
            }
            catch (Exception ex)
            {
                if (screen) Check(false, "画面の一部を取り込んで読む: " + ex.Message);
                else Note("画面の取り込みは許可が無いため確かめられません: " + ex.Message);
            }

            // すべての音 (このプロセス以外) を 2 秒取り込む
            try
            {
                using var source = new MacAudioSource(Environment.ProcessId, exclude: true);
                long bytes = 0;
                source.DataAvailable += d => Interlocked.Add(ref bytes, d.Length);
                source.Start();
                await Task.Delay(2000);
                double seconds = Interlocked.Read(ref bytes) / 2.0 / AudioSources.SampleRate;
                Check(seconds > 1.5, $"すべての音を取り込む (2 秒で {seconds:0.0} 秒分。音が無い間も無音で補う)");
            }
            catch (Exception ex)
            {
                if (screen) Check(false, "すべての音を取り込む: " + ex.Message);
                else Note("音の取り込みは許可が無いため確かめられません: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            Check(false, "補助プログラム: " + ex.Message);
        }
        return Finish();

        int Finish()
        {
            report.Insert(0, $"補助プログラムの確認 ・ {(failed == 0 ? "すべて合格" : $"不合格 {failed} 件")}\n");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            Console.WriteLine(report);
            return failed == 0 ? 0 : 2;
        }
    }
}
