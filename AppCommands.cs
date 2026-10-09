using System.IO;
using System.Windows;
using System.Windows.Input;

namespace GetText;

/// <summary>
/// GetText のコマンドと機能の登録 (Windows 版)。コマンドの一覧 (Ctrl+K) とホームはここに登録されたものを表示する。
/// 拡張機能も Registry.Register・Features.Add で同じように加えられる (表示は GetText が行う)。
/// </summary>
public static class AppCommands
{
    public static CommandRegistry Registry { get; } = new();

    /// <summary>ホームに並べる機能 (並べた順に表示)。</summary>
    public static List<FeatureInfo> Features { get; } = [];

    /// <summary>コマンドの一覧を開く / 閉じる。</summary>
    public static void TogglePalette(Window? owner, CommandContext context)
    {
        RefreshRecent();
        CommandPalette.Toggle(owner, Registry, context);
    }

    /// <summary>どの窓でも効くキー (Ctrl+K)。処理したら true。</summary>
    public static bool HandleKey(Window window, KeyEventArgs e, CommandContext context)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            TogglePalette(window, context);
            e.Handled = true;
            return true;
        }
        // Ctrl+, (設定): コマンドの一覧に出しているキーなので、どの画面でも効くようにする (読み取りの画面は、読み取りのページを開く)
        if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control && context != CommandContext.ScreenOcr && Registry.TryRun("settings.open"))
        {
            e.Handled = true;
            return true;
        }
        return false;
    }

    /// <summary>GetText の機能とコマンドを登録する (起動したとき・画面の見本を作るとき)。</summary>
    public static void RegisterBuiltIns(HomeWindow? home, TextWindow text, AppSettings settings)
    {
        Features.Clear();
        Features.Add(new FeatureInfo
        {
            Id = "ocr",
            Name = "文字の読み取り",
            Description = "画面の枠の中の文字を読み取り、日本語に訳します",
            Icon = AppIcon.ScreenOcr,
            Status = () => text.IsOcrOpen ? new FeatureStatus(FeatureState.Open, "開いています") : FeatureStatus.Closed,
            Open = () => { text.OpenOcr(); home?.AfterFeatureOpened(); },
            Close = text.CloseOcr,
        });
        Features.Add(new FeatureInfo
        {
            Id = "minutes",
            Name = "議事録",
            Description = "会議アプリの音声を、話者と時刻つきで文字にします",
            Icon = AppIcon.Meeting,
            IsInstalled = () => App.DemoMode || TranscriptionService.IsInstalled,
            DownloadSize = "約 4 GB",
            Status = () => text.Minutes is not { IsLoaded: true } m ? FeatureStatus.Closed
                : m.BusyLabel is { } busy ? new FeatureStatus(FeatureState.Active, busy) : new FeatureStatus(FeatureState.Open, "開いています"),
            Open = () => { text.OpenMinutes(); home?.AfterFeatureOpened(); },
            Close = () => text.Minutes?.Close(),
            Install = () => text.OpenSettings(SettingsPage.Models, home),
        });
        Features.Add(new FeatureInfo
        {
            Id = "record",
            Name = "画面の録画",
            Description = "ウィンドウや画面全体を、音つきで MP4 に録画します",
            Icon = AppIcon.Recording,
            IsInstalled = () => ScreenRecorder.IsSupported,
            Status = () => home?.Recorder is not { IsLoaded: true } r ? FeatureStatus.Closed
                : r.IsRecording ? new FeatureStatus(FeatureState.Active, "録画中") : new FeatureStatus(FeatureState.Open, "開いています"),
            Open = () => { home?.OpenRecorder(); home?.AfterFeatureOpened(); },
            Close = () => home?.Recorder?.Close(),
        });

        foreach (var f in Features)
        {
            var feature = f;
            Registry.Register(new AppCommand
            {
                Id = "feature." + feature.Id,
                Title = feature.Name + "を開く",
                Subtitle = feature.Description,
                Keywords = feature.Id switch
                {
                    "ocr" => ["Screen OCR", "OCR", "よみとり", "文字"],
                    "minutes" => ["Meeting", "Minutes", "会議", "文字起こし", "transcribe"],
                    _ => ["Record screen", "Recording", "録画", "動画"],
                },
                Category = CommandCategory.Feature,
                Icon = feature.Icon,
                Contexts = [CommandContext.Home],
                Execute = () => { if (feature.IsInstalled()) feature.Open(); else feature.Install?.Invoke(); },
            });
        }

        bool OcrOpen() => text.IsOcrOpen;
        bool MinutesOpen() => text.Minutes is { IsLoaded: true };
        bool RecorderOpen() => home?.Recorder is { IsLoaded: true };
        CommandContext[] ocr = [CommandContext.ScreenOcr];

        // 文字の読み取り
        Registry.Register(new AppCommand { Id = "ocr.read", Title = "今すぐ読み取る", Keywords = ["Read now", "refresh", "OCR"], Icon = AppIcon.Refresh, Shortcut = "F5", Contexts = ocr, IsAvailable = OcrOpen, Execute = text.ReadNow });
        Registry.Register(new AppCommand { Id = "ocr.pause", Title = "自動読み取りを一時停止 / 再開", Keywords = ["Pause", "Resume", "停止"], Icon = AppIcon.Pause, Shortcut = "Ctrl+P", Contexts = ocr, IsAvailable = OcrOpen, Execute = text.TogglePause });
        Registry.Register(new AppCommand { Id = "ocr.search", Title = "読み取った文字を検索", Keywords = ["Search", "Find", "けんさく", "探す"], Icon = AppIcon.Search, Shortcut = "Ctrl+F", Contexts = ocr, IsAvailable = OcrOpen, Execute = text.ShowSearch });
        Registry.Register(new AppCommand { Id = "ocr.view.groups", Title = "枠・まとまりごとに表示", Subtitle = "名簿や表を枠ごとに分けて読む", Keywords = ["Layout", "Group", "枠", "表", "グループ"], Icon = AppIcon.Groups, Contexts = ocr, IsAvailable = () => OcrOpen() && settings.OcrView != OcrView.Groups && !text.IsAccumulating, Execute = () => text.SetView(OcrView.Groups) });
        Registry.Register(new AppCommand { Id = "ocr.view.text", Title = "上から順に表示", Keywords = ["Layout", "Text", "通常"], Icon = AppIcon.Layout, Contexts = ocr, IsAvailable = () => OcrOpen() && settings.OcrView != OcrView.Text, Execute = () => text.SetView(OcrView.Text) });
        Registry.Register(new AppCommand { Id = "ocr.accumulate", Title = "蓄積 (スクロールしながら集める) の開始 / 終了", Keywords = ["Accumulate", "collect", "長い文書"], Icon = AppIcon.Accumulate, Contexts = ocr, IsAvailable = OcrOpen, Execute = text.ToggleAccumulate });
        Registry.Register(new AppCommand { Id = "ocr.overlay", Title = "訳を画面に重ねる / やめる", Subtitle = "読み取りの枠の中の外国語の文に、日本語の訳を重ねる", Keywords = ["Overlay", "translation overlay", "翻訳", "重ねる", "訳"], Icon = AppIcon.Translate, Contexts = ocr, IsAvailable = OcrOpen, Execute = text.ToggleOverlay });
        Registry.Register(new AppCommand { Id = "ocr.copy", Title = "原文をコピー", Keywords = ["Copy", "original"], Icon = AppIcon.Copy, Shortcut = "Ctrl+Shift+C", Contexts = ocr, IsAvailable = OcrOpen, Execute = text.CopyOriginal });
        Registry.Register(new AppCommand { Id = "ocr.copyTranslation", Title = "日本語訳をコピー", Keywords = ["Copy", "translation"], Icon = AppIcon.Copy, Shortcut = "Ctrl+Shift+T", Contexts = ocr, IsAvailable = () => OcrOpen() && settings.Translate, Execute = text.CopyTranslation });
        Registry.Register(new AppCommand { Id = "ocr.pin", Title = "常に手前に表示 (切り替え)", Keywords = ["Pin", "Topmost", "always on top", "固定"], Icon = AppIcon.Pin, Contexts = ocr, IsAvailable = OcrOpen, Execute = text.TogglePin });
        Registry.Register(new AppCommand { Id = "ocr.close", Title = "文字の読み取りを閉じる", Keywords = ["Close OCR"], Icon = AppIcon.Close, IsAvailable = OcrOpen, Execute = text.CloseOcr });
        Registry.Register(new AppCommand { Id = "ocr.quick", Title = "範囲を選んで文字をコピー (Quick OCR)", Subtitle = "読み取りの画面を開かずに 1 回だけ読む", Keywords = ["Quick OCR", "Silent OCR", "範囲", "スクリーンショット", "コピー"], Icon = AppIcon.ScreenOcr, ShortcutSource = () => HotkeyText.For(settings, "quick-ocr"), Execute = QuickOcr.Run });

        // 議事録
        CommandContext[] meeting = [CommandContext.Meeting];
        Registry.Register(new AppCommand { Id = "minutes.toggle", Title = "議事録の記録を開始 / 停止", Keywords = ["Start Meeting", "Stop", "記録", "録音"], Icon = AppIcon.Meeting, Shortcut = "Ctrl+R", Contexts = meeting, IsAvailable = MinutesOpen, Execute = () => text.Minutes?.ToggleRecording() });
        Registry.Register(new AppCommand { Id = "minutes.file", Title = "ファイルから文字起こし", Subtitle = "MP4・MP3・WAV など", Keywords = ["Transcribe file", "音声ファイル", "動画"], Icon = AppIcon.Document, Contexts = [CommandContext.Meeting, CommandContext.Home], IsAvailable = () => App.DemoMode || TranscriptionService.IsInstalled, Execute = () => text.OpenMinutes().TranscribeFile() });

        // 画面の録画
        CommandContext[] recording = [CommandContext.Recording];
        Registry.Register(new AppCommand { Id = "record.toggle", Title = "録画を開始 / 停止", Keywords = ["Record", "Stop recording", "録画"], Icon = AppIcon.Recording, Contexts = recording, IsAvailable = RecorderOpen, Execute = () => home?.Recorder?.ToggleRecording() });
        Registry.Register(new AppCommand { Id = "record.folder", Title = "録画の保存先を開く", Keywords = ["Open folder", "videos", "フォルダ"], Icon = AppIcon.Folder, Contexts = recording, Execute = () => (home?.Recorder ?? home?.OpenRecorder())?.OpenFolder() });

        // 設定
        Registry.Register(new AppCommand { Id = "settings.open", Title = "設定を開く", Keywords = ["Settings", "Preferences", "せってい"], Category = CommandCategory.Settings, Icon = AppIcon.Settings, Shortcut = "Ctrl+,", Execute = () => text.OpenSettings(SettingsPage.Appearance, ActiveWindow()) });
        foreach (var (page, title, keywords, icon) in SettingsPages)
        {
            var p = page;
            Registry.Register(new AppCommand { Id = "settings." + page, Title = "設定: " + title, Keywords = keywords, Category = CommandCategory.Settings, Icon = icon, Execute = () => text.OpenSettings(p, ActiveWindow()) });
        }
        foreach (var (theme, label) in new[] { (AppTheme.System, "Windows に合わせる"), (AppTheme.Light, "ライト"), (AppTheme.Dark, "ダーク") })
        {
            var t = theme;
            Registry.Register(new AppCommand
            {
                Id = "theme." + theme, Title = "テーマ: " + label, Keywords = ["Theme", "Appearance", t == AppTheme.Dark ? "dark mode" : t == AppTheme.Light ? "light mode" : "system"],
                Category = CommandCategory.Settings, Icon = AppIcon.Appearance, IsAvailable = () => settings.Theme != t,
                Execute = () => { settings.Theme = t; text.OnSettingChanged(nameof(AppSettings.Theme)); },
            });
        }
        Registry.Register(new AppCommand { Id = "app.home", Title = "ホームを表示", Keywords = ["Home", "Hub", "機能を選ぶ"], Icon = AppIcon.Home, IsAvailable = () => home != null, Execute = () => home?.ShowHome() });
        Registry.Register(new AppCommand { Id = "app.logs", Title = "ログのフォルダを開く", Keywords = ["Diagnostics", "logs", "error"], Category = CommandCategory.Settings, Icon = AppIcon.Diagnostics, Execute = () => OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "logs")) });
        Registry.Register(new AppCommand { Id = "app.exit", Title = "GetText を終了", Keywords = ["Exit", "Quit", "終わる"], Icon = AppIcon.Exit, IsAvailable = () => home != null, Execute = () => home?.Close() });
    }

    /// <summary>設定のページ (名前・検索の言葉・アイコン)。</summary>
    public static readonly (SettingsPage Page, string Title, string[] Keywords, AppIcon Icon)[] SettingsPages =
    [
        (SettingsPage.Appearance, "外観", ["Appearance", "テーマ", "文字の大きさ", "font"], AppIcon.Appearance),
        (SettingsPage.Shortcuts, "ショートカット", ["Shortcuts", "Keyboard", "キー", "ホットキー"], AppIcon.Keyboard),
        (SettingsPage.Privacy, "プライバシー", ["Privacy", "送信", "ネットワーク", "データ"], AppIcon.Privacy),
        (SettingsPage.ScreenOcr, "文字の読み取り", ["Screen OCR", "OCR", "読み取り"], AppIcon.ScreenOcr),
        (SettingsPage.Translation, "翻訳", ["Translation", "DeepL", "Google"], AppIcon.Translate),
        (SettingsPage.Meetings, "議事録", ["Meetings", "Minutes", "会議"], AppIcon.Meeting),
        (SettingsPage.Recording, "画面の録画", ["Recording", "録画", "保存先"], AppIcon.Recording),
        (SettingsPage.Models, "モデルとセットアップ", ["Models", "Setup", "インストール", "AI"], AppIcon.Model),
        (SettingsPage.Extensions, "拡張機能", ["Extensions", "Plugins", "プラグイン", "アドオン", "追加"], AppIcon.Extensions),
        (SettingsPage.Diagnostics, "診断", ["Diagnostics", "ログ", "バージョン", "about"], AppIcon.Diagnostics),
    ];

    // 最近の議事録 (開くたびに作り直す)
    private static void RefreshRecent()
    {
        foreach (var old in Registry.All.Where(c => c.Id.StartsWith("recent.", StringComparison.Ordinal)).Select(c => c.Id).ToList())
            Registry.Unregister(old);
        if (App.DemoMode || _text == null) return;
        try
        {
            foreach (var (path, label) in RecentMinutes.List(MinutesWindow.RecentFolder, 6))
            {
                var p = path;
                Registry.Register(new AppCommand
                {
                    Id = "recent." + Path.GetFileName(path), Title = "議事録: " + label, Keywords = ["Recent", "最近", "開く"],
                    Category = CommandCategory.Recent, Icon = AppIcon.Document,
                    Execute = () => _text.OpenMinutes().OpenRecent(p),
                });
            }
        }
        catch (Exception ex)
        {
            App.Log("RecentMinutes", ex);
        }
    }

    private static TextWindow? _text;

    /// <summary>最近の議事録を開く先 (読み取りの画面が持つ議事録の画面)。</summary>
    public static void SetHost(TextWindow text) => _text = text;

    private static Window? ActiveWindow() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w is not CommandPalette)
        ?? App.Home;

    internal static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log("OpenFolder", ex);
        }
    }
}
