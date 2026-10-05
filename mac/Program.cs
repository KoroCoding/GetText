using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace GetText;

internal static class Program
{
    /// <summary>--smoke のとき: 実際の画面で起動して確かめ、結果を書くファイル。</summary>
    public static string? SmokeReport { get; private set; }

    /// <summary>
    /// 起動のしかた:
    ///   GetText                                         普通に起動
    ///   GetText --smoke 結果.txt                        実際の画面で起動し、窓を画像にして確かめてから終わる (CI 用)
    ///   GetText --selftest-ui 結果.txt                  画面に出さずに議事録の画面の操作を確かめる
    ///   GetText --snapshots フォルダ [dark]             画面に出さずに各画面を画像にする
    ///   GetText --selftest-helper 結果.txt              補助プログラム (文字認識・キーチェーン・ショートカット) を確かめる
    ///   GetText --selftest-minutes 会議.wav 正解.json フォルダ [nosummary]   議事録の通しの確認 (Python と AI のモデルが必要)
    ///   GetText --selftest-capture 会議.wav 正解.json フォルダ               実際に再生した音の取り込みの確認 (画面収録の許可が必要)
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        try
        {
            switch (mode)
            {
                case "--smoke" when args.Length >= 2:
                    SmokeReport = args[1];
                    App.DemoMode = true;
                    return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                case "--selftest-ui" when args.Length >= 2:
                    return RunHeadless(() => UiSelfTest.RunAsync(args[1]));
                case "--snapshots" when args.Length >= 2:
                    return RunHeadless(() => Snapshots.RunAsync(args[1], args.Contains("dark")));
                case "--selftest-helper" when args.Length >= 2:
                    return HelperSelfTest.RunAsync(args[1]).GetAwaiter().GetResult();
                case "--selftest-minutes" when args.Length >= 4:
                    return MinutesSelfTest.RunAsync(args[1], args[2], args[3], !args.Contains("nosummary")).GetAwaiter().GetResult();
                case "--selftest-capture" when args.Length >= 4:
                    return CaptureSelfTest.RunAsync(args[1], args[2], args[3]).GetAwaiter().GetResult();
                default:
                    return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
        }
        catch (Exception ex)
        {
            App.Log("Main", ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            MacHelper.Instance.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .LogToTrace();

    /// <summary>画面に出さずに (Skia で描いて) 動かす。動作確認・画面の画像用。</summary>
    private static int RunHeadless(Func<Task<int>> body)
    {
        App.DemoMode = true;
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        int code = 1;
        var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                code = await body();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                code = 1;
            }
            finally
            {
                done.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        return code;
    }
}
