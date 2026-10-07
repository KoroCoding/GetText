using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;

namespace GetText;

public partial class App : Application
{
    /// <summary>
    /// 動作確認・画面の画像を作るときなど、見本のデータで画面を動かしているとき。
    /// 設定・議事録の保存、読み取り、ショートカット、AI の起動は行わない。
    /// </summary>
    internal static bool DemoMode { get; set; }

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "logs", "error.log");

    private TextWindow? _text;

    /// <summary>機能を選ぶ画面 (普通に起動したとき。動作確認や見本の画面を作るときは null)。</summary>
    internal static HomeWindow? Home { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        MacTheme.Apply(this); // 色・字体のデザイントークン (DesignTokens の Mac の値)
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Log("UI", e.Exception);
                e.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log("Task", e.Exception);
                e.SetObserved();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Fatal", e.ExceptionObject as Exception);
            Start(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // 窓を作れなかったら (設定ファイルの値がおかしいなど) 既定の設定でやり直す
        foreach (var useDefaults in new[] { false, true })
        {
            try
            {
                var settings = useDefaults ? new AppSettings() : AppSettings.Load();
                ApplyTheme(settings.Theme);
                var capture = new CaptureWindow(settings);
                _text = new TextWindow(capture, settings);
                // 最初は機能を選ぶ画面を出す (選んだ機能を開く。読み取りの窓は作っておき、開くまで隠しておく)
                var home = new HomeWindow(_text, settings);
                Home = home;
                desktop.MainWindow = home;
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                _text.PrepareForLauncher();
                home.Show();
                // 動作確認 (CI) は読み取りの画面を開いた状態で確かめる
                if (Program.SmokeReport != null) _text.OpenOcr();
                BuildMenu(_text);
                if (useDefaults) Log("Startup", new InvalidOperationException("設定を読み込めなかったので既定の設定で起動しました"));
                break;
            }
            catch (Exception ex)
            {
                Log("Startup", ex);
                if (useDefaults) throw;
            }
        }
        // メニューの「終了」(⌘Q) やログアウトでも、設定と議事録を保存する
        desktop.ShutdownRequested += async (_, e) =>
        {
            // 録画中・議事録を記録中なら、両方とも止めてよいか先に確かめ、録画を書き終えてから終える
            // (途中で切ると動画のファイルが壊れる。Shutdown は窓を閉じるのを待たないので、先に保存しておく)
            if (!_quitConfirmed && (Home?.Recorder is { IsBusy: true } || _text?.Minutes is { IsBusy: true }))
            {
                e.Cancel = true;
                if (_quitting) return;
                _quitting = true;
                try
                {
                    if (Home != null && !await Home.ConfirmRecordingAsync(Home)) return;
                    if (_text?.Minutes is { IsBusy: true } minutes && !await minutes.ConfirmStopAsync("GetText を終了します")) return;
                    if (Home != null) await Home.StopRecordingAndWaitAsync();
                    _quitConfirmed = true;
                    if (_text?.Minutes is { HasPendingWork: true } pending) await pending.WaitPendingAsync();
                    _text?.SaveOnShutdown();
                    desktop.Shutdown();
                }
                finally
                {
                    _quitting = false;
                }
                return;
            }
            // 記録を止めて残りの音声を文字にしている途中・プロジェクトを保存している途中なら、終わってから終える
            if (_text?.Minutes is { HasPendingWork: true } stopping)
            {
                e.Cancel = true;
                if (_quitting) return; // (待っている間にもう一度 ⌘Q を押された)
                _quitting = true;
                try
                {
                    await stopping.WaitPendingAsync();
                }
                finally
                {
                    _quitting = false;
                }
                desktop.Shutdown();
                return;
            }
            _text?.SaveOnShutdown();
        };
        if (Program.SmokeReport is { } report && _text != null)
            Dispatcher.UIThread.Post(() => _ = SmokeTest.RunAsync(_text, report, desktop));
    }

    private bool _quitConfirmed;
    private bool _quitting;

    // Mac のメニューバー (GetText メニュー) に「設定…」「議事録」を加える
    private void BuildMenu(TextWindow text)
    {
        var settings = new NativeMenuItem("設定…") { Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.OemComma, Avalonia.Input.KeyModifiers.Meta) };
        settings.Click += (_, _) => text.OpenSettings(SettingsPage.Appearance);
        var minutes = new NativeMenuItem("議事録を開く") { Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.M, Avalonia.Input.KeyModifiers.Meta | Avalonia.Input.KeyModifiers.Shift) };
        minutes.Click += (_, _) => text.OpenMinutes();
        var ocr = new NativeMenuItem("文字の読み取りを開く");
        ocr.Click += (_, _) => text.OpenOcr();
        var record = new NativeMenuItem("画面の録画を開く");
        record.Click += (_, _) => Home?.OpenRecorder();
        var home = new NativeMenuItem("機能を選ぶ画面") { Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.D0, Avalonia.Input.KeyModifiers.Meta) };
        home.Click += (_, _) =>
        {
            if (Home == null) return;
            Home.WindowState = Avalonia.Controls.WindowState.Normal;
            Home.Activate();
        };
        var palette = new NativeMenuItem("コマンドの一覧…") { Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.K, Avalonia.Input.KeyModifiers.Meta) };
        palette.Click += (_, _) => AppCommands.TogglePalette(Home, CommandContext.Any);
        var menu = new NativeMenu();
        menu.Items.Add(palette);
        menu.Items.Add(settings);
        menu.Items.Add(home);
        menu.Items.Add(ocr);
        menu.Items.Add(minutes);
        menu.Items.Add(record);
        NativeMenu.SetMenu(this, menu);
    }

    /// <summary>ライト / ダーク / Mac の設定に合わせる。</summary>
    public static void ApplyTheme(AppTheme theme)
    {
        if (Current == null) return;
        Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    internal static void Log(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {ex}\n\n");
        }
        catch
        {
            // ログが書けなくても続行する
        }
    }
}
