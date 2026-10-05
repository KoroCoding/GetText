using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace GetText;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "logs", "error.log");

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    private Mutex? _singleInstance;

    /// <summary>
    /// 操作手順の動画用に、見本のデータで画面を描いて画像に保存しているとき (GetText.exe --doc-snapshots フォルダ)。
    /// 設定・議事録の保存、読み取り、ホットキー、AI の起動は行わない。
    /// </summary>
    internal static bool DemoMode { get; set; }

    /// <summary>機能を選ぶ画面 (普通に起動したとき。動作確認や見本の画面を作るときは null)。</summary>
    internal static HomeWindow? Home { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        bool snapshots = e.Args.Length >= 2 && e.Args[0] == "--doc-snapshots";
        bool transcribe = e.Args.Length >= 3 && e.Args[0] == "--transcribe-file";
        bool selftest = e.Args.Length >= 4 && e.Args[0] == "--selftest-minutes";
        bool minutesShots = e.Args.Length >= 4 && e.Args[0] == "--doc-snapshots-minutes";
        bool uiTest = e.Args.Length >= 2 && e.Args[0] == "--selftest-ui";
        bool screenTest = e.Args.Length >= 2 && e.Args[0] == "--selftest-screen";
        bool captureTest = e.Args.Length >= 4 && e.Args[0] == "--selftest-capture";
        bool recordTest = e.Args.Length >= 2 && e.Args[0] == "--selftest-record";
        bool playbackTest = e.Args.Length >= 3 && e.Args[0] == "--selftest-playback";
        if (e.Args.Length >= 2 && e.Args[0] == "--play-wav")
        {
            // --selftest-capture が会議アプリの代わりに起動する、音声を再生するだけのプロセス
            Environment.Exit(CaptureSelfTest.PlayWav(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : null));
            return;
        }
        snapshots &= !minutesShots;
        if (snapshots || transcribe || selftest || minutesShots || uiTest || screenTest || captureTest || recordTest || playbackTest)
        {
            DemoMode = true;
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = 0;
            try
            {
                if (snapshots) await DocSnapshots.RunAsync(e.Args[1], e.Args.Contains("dark"));
                else if (uiTest) code = await UiSelfTest.RunAsync(e.Args[1]);
                else if (screenTest) code = await ScreenSelfTest.RunAsync(e.Args[1]);
                else if (recordTest) code = await RecordSelfTest.RunAsync(e.Args[1], e.Args.Length >= 3 && e.Args[2].Length > 0 ? e.Args[2] : null,
                    e.Args.Length >= 4 && int.TryParse(e.Args[3], out int recordFps) ? recordFps : 30);
                else if (playbackTest) code = await PlaybackSelfTest.RunAsync(e.Args[1], e.Args[2], e.Args.Length >= 4 && e.Args[3].Length > 0 ? e.Args[3] : null,
                    e.Args.Length >= 5 && int.TryParse(e.Args[4], out int playCount) ? playCount : 12);
                else if (captureTest) code = await CaptureSelfTest.RunAsync(e.Args[1], e.Args[2], e.Args[3], e.Args.Length >= 5 ? e.Args[4] : null);
                else if (minutesShots) await DocSnapshots.RunMinutesAsync(e.Args[1], e.Args[2], e.Args[3], e.Args.Contains("final"));
                else if (selftest) code = await MinutesSelfTest.RunAsync(e.Args[1], e.Args[2], e.Args[3], !e.Args.Contains("nosummary"));
                else await FileTranscribeCheck.RunAsync(e.Args[1], e.Args[2], e.Args.Length >= 4 ? e.Args[3] : "auto");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                code = 1;
            }
            Environment.Exit(code); // ウィンドウの終了処理 (設定の保存など) を通さずに終える
            return;
        }

        // 二重起動すると枠やホットキーが重なるので、既存のウィンドウを前面に出して終わる
        _singleInstance = new Mutex(true, @"Local\GetText.SingleInstance", out bool created);
        if (!created)
        {
            ActivateExisting();
            Shutdown();
            return;
        }

        // 予期しないエラーで落ちないようにし、原因を記録する
        DispatcherUnhandledException += (_, args) =>
        {
            Log("UI", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("Task", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("Fatal", args.ExceptionObject as Exception);

        base.OnStartup(e);

        // 窓を作れなかったら (設定ファイルの値がおかしいなど) 既定の設定でやり直し、それでもだめなら知らせて終わる
        // (何も出ないまま裏に残ると、二重起動の防止でその後も起動できなくなるため)
        TextWindow? text = null;
        foreach (var useDefaults in new[] { false, true })
        {
            try
            {
                var settings = useDefaults ? new AppSettings() : AppSettings.Load();
                ApplyTheme(settings.Theme);
                var capture = new CaptureWindow(settings);
                text = new TextWindow(capture, settings);
                // 最初は機能を選ぶ画面を出す (選んだ機能を開く。読み取りの窓は作っておき、開くまで隠しておく)
                var home = new HomeWindow(text, settings);
                Home = home;
                MainWindow = home;
                text.PrepareForLauncher();
                home.Show();
                if (useDefaults) Log("Startup", new InvalidOperationException("設定を読み込めなかったので既定の設定で起動しました"));
                break;
            }
            catch (Exception ex)
            {
                Log("Startup", ex);
                Home?.Abandon(); // (閉じても GetText を終えず、既定の設定で作り直す)
                Home = null; // (読み取りの窓の ✕ が「閉じる」ではなく本当に閉じるように)
                foreach (Window w in Windows) w.Close();
                text = null;
                if (useDefaults)
                {
                    MessageBox.Show("GetText を起動できませんでした。\n\n" + ex.Message + "\n\n詳しくは %LOCALAPPDATA%\\GetText\\logs\\error.log を見てください。",
                        "GetText", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(1);
                    return;
                }
            }
        }
        // Windows のログオフ・シャットダウンでは窓の Closing が来ないことがあるので、ここでも保存する
        SessionEnding += (_, _) =>
        {
            Home?.StopRecordingNow(); // 録画を書き終える (途中で切ると動画のファイルが壊れる)
            text?.SaveOnSessionEnd();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        Home?.RunAfterExit(); // (設定の「セットアップをやり直す」: GetText を終えてから始める)
        base.OnExit(e);
    }

    /// <summary>Windows 11 風の Fluent テーマを適用する (ライト / ダーク / Windows の設定に合わせる)。</summary>
    public static void ApplyTheme(AppTheme theme)
    {
        var mode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        if (Current.ThemeMode != mode) Current.ThemeMode = mode;
    }

    private static void ActivateExisting()
    {
        var current = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(current.ProcessName))
        {
            if (p.Id == current.Id || p.MainWindowHandle == IntPtr.Zero) continue;
            ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
            SetForegroundWindow(p.MainWindowHandle);
            break;
        }
    }

    internal static void Log(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // 大きくなりすぎたら作り直す
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {ex}\r\n\r\n");
        }
        catch
        {
            // ログが書けなくても続行する
        }
    }
}

