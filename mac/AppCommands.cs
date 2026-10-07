using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;

namespace GetText;

/// <summary>
/// GetText のコマンドと機能の登録 (Mac 版)。コマンドの一覧 (⌘K) とホームはここに登録されたものを表示する。
/// 拡張機能も Registry.Register・Features.Add で同じように加えられる (表示は GetText が行う)。
/// </summary>
public static class AppCommands
{
    public static CommandRegistry Registry { get; } = new();

    public static List<FeatureInfo> Features { get; } = [];

    private static TextWindow? _text;

    public static void TogglePalette(Window? owner, CommandContext context)
    {
        RefreshRecent();
        CommandPalette.Toggle(owner, Registry, context);
    }

    /// <summary>どの窓でも効くキー (⌘K)。処理したら true。</summary>
    public static bool HandleKey(Window window, KeyEventArgs e, CommandContext context)
    {
        var cmd = TopLevel.GetTopLevel(window)?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Meta;
        if (e.Key == Key.K && e.KeyModifiers == cmd)
        {
            TogglePalette(window, context);
            e.Handled = true;
            return true;
        }
        return false;
    }

    public static void RegisterBuiltIns(HomeWindow? home, TextWindow text, AppSettings settings)
    {
        _text = text;
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
            Status = () => text.Minutes is not { IsVisible: true } m ? FeatureStatus.Closed
                : m.IsBusy ? new FeatureStatus(FeatureState.Active, "記録中") : new FeatureStatus(FeatureState.Open, "開いています"),
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
            Status = () => home?.Recorder is not { IsVisible: true } r ? FeatureStatus.Closed
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
        bool MinutesOpen() => text.Minutes is { IsVisible: true };
        bool RecorderOpen() => home?.Recorder is { IsVisible: true };
        CommandContext[] ocr = [CommandContext.ScreenOcr];

        Registry.Register(new AppCommand { Id = "ocr.read", Title = "今すぐ読み取る", Keywords = ["Read now", "refresh", "OCR"], Icon = AppIcon.Refresh, Shortcut = "⌘R", Contexts = ocr, IsAvailable = OcrOpen, Execute = text.ReadNow });
        Registry.Register(new AppCommand { Id = "ocr.pause", Title = "自動読み取りを一時停止 / 再開", Keywords = ["Pause", "Resume", "停止"], Icon = AppIcon.Pause, Shortcut = "⌘P", Contexts = ocr, IsAvailable = OcrOpen, Execute = text.TogglePause });
        Registry.Register(new AppCommand { Id = "ocr.search", Title = "読み取った文字を検索", Keywords = ["Search", "Find", "けんさく", "探す"], Icon = AppIcon.Search, Shortcut = "⌘F", Contexts = ocr, IsAvailable = OcrOpen, Execute = text.ShowSearch });
        Registry.Register(new AppCommand { Id = "ocr.view.groups", Title = "枠・まとまりごとに表示", Subtitle = "名簿や表を枠ごとに分けて読む", Keywords = ["Layout", "Group", "枠", "表", "グループ"], Icon = AppIcon.Groups, Contexts = ocr, IsAvailable = () => OcrOpen() && settings.OcrView != OcrView.Groups, Execute = () => text.SetView(OcrView.Groups) });
        Registry.Register(new AppCommand { Id = "ocr.view.text", Title = "上から順に表示", Keywords = ["Layout", "Text", "通常"], Icon = AppIcon.Layout, Contexts = ocr, IsAvailable = () => OcrOpen() && settings.OcrView != OcrView.Text, Execute = () => text.SetView(OcrView.Text) });
        Registry.Register(new AppCommand { Id = "ocr.accumulate", Title = "蓄積 (スクロールしながら集める) の開始 / 終了", Keywords = ["Accumulate", "collect", "長い文書"], Icon = AppIcon.Accumulate, Contexts = ocr, IsAvailable = OcrOpen, Execute = text.ToggleAccumulate });
        Registry.Register(new AppCommand { Id = "ocr.copy", Title = "原文をコピー", Keywords = ["Copy", "original"], Icon = AppIcon.Copy, Shortcut = "⌘⇧C", Contexts = ocr, IsAvailable = OcrOpen, Execute = text.CopyOriginal });
        Registry.Register(new AppCommand { Id = "ocr.copyTranslation", Title = "日本語訳をコピー", Keywords = ["Copy", "translation"], Icon = AppIcon.Copy, Shortcut = "⌘⇧T", Contexts = ocr, IsAvailable = () => OcrOpen() && settings.Translate, Execute = text.CopyTranslation });
        Registry.Register(new AppCommand { Id = "ocr.pin", Title = "常に手前に表示 (切り替え)", Keywords = ["Pin", "Topmost", "always on top", "固定"], Icon = AppIcon.Pin, Contexts = ocr, IsAvailable = OcrOpen, Execute = text.TogglePin });
        Registry.Register(new AppCommand { Id = "ocr.close", Title = "文字の読み取りを閉じる", Keywords = ["Close OCR"], Icon = AppIcon.Close, IsAvailable = OcrOpen, Execute = text.CloseOcr });
        Registry.Register(new AppCommand { Id = "ocr.quick", Title = "範囲を選んで文字をコピー (Quick OCR)", Subtitle = "読み取りの画面を開かずに 1 回だけ読む", Keywords = ["Quick OCR", "Silent OCR", "範囲", "スクリーンショット", "コピー"], Icon = AppIcon.ScreenOcr, Shortcut = "⌃⌥Q", Execute = MacQuickOcr.Run });

        CommandContext[] meeting = [CommandContext.Meeting];
        Registry.Register(new AppCommand { Id = "minutes.toggle", Title = "議事録の記録を開始 / 停止", Keywords = ["Start Meeting", "Stop", "記録", "録音"], Icon = AppIcon.Meeting, Shortcut = "⌘R", Contexts = meeting, IsAvailable = MinutesOpen, Execute = () => text.Minutes?.ToggleRecording() });
        Registry.Register(new AppCommand { Id = "minutes.file", Title = "ファイルから文字起こし", Subtitle = "MP4・MP3・WAV など", Keywords = ["Transcribe file", "音声ファイル", "動画"], Icon = AppIcon.Document, Contexts = [CommandContext.Meeting, CommandContext.Home], IsAvailable = () => App.DemoMode || TranscriptionService.IsInstalled, Execute = () => text.OpenMinutes().TranscribeFile() });

        CommandContext[] recording = [CommandContext.Recording];
        Registry.Register(new AppCommand { Id = "record.toggle", Title = "録画を開始 / 停止", Keywords = ["Record", "Stop recording", "録画"], Icon = AppIcon.Recording, Contexts = recording, IsAvailable = RecorderOpen, Execute = () => home?.Recorder?.ToggleRecording() });
        Registry.Register(new AppCommand { Id = "record.folder", Title = "録画の保存先を開く", Keywords = ["Open folder", "Movies", "フォルダ"], Icon = AppIcon.Folder, Contexts = recording, Execute = () => (home?.Recorder ?? home?.OpenRecorder())?.OpenFolder() });

        Registry.Register(new AppCommand { Id = "settings.open", Title = "設定を開く", Keywords = ["Settings", "Preferences", "せってい"], Category = CommandCategory.Settings, Icon = AppIcon.Settings, Shortcut = "⌘,", Execute = () => text.OpenSettings(SettingsPage.Appearance) });
        foreach (var (page, title, keywords, icon) in SettingsPages)
        {
            var p = page;
            Registry.Register(new AppCommand { Id = "settings." + page, Title = "設定: " + title, Keywords = keywords, Category = CommandCategory.Settings, Icon = icon, Execute = () => text.OpenSettings(p) });
        }
        foreach (var (theme, label) in new[] { (AppTheme.System, "Mac に合わせる"), (AppTheme.Light, "ライト"), (AppTheme.Dark, "ダーク") })
        {
            var t = theme;
            Registry.Register(new AppCommand
            {
                Id = "theme." + theme, Title = "テーマ: " + label, Keywords = ["Theme", "Appearance", t == AppTheme.Dark ? "dark mode" : t == AppTheme.Light ? "light mode" : "system"],
                Category = CommandCategory.Settings, Icon = AppIcon.Appearance, IsAvailable = () => settings.Theme != t,
                Execute = () => { settings.Theme = t; text.OnSettingChanged(nameof(AppSettings.Theme)); },
            });
        }
        Registry.Register(new AppCommand { Id = "app.home", Title = "ホームを表示", Keywords = ["Home", "Hub", "機能を選ぶ"], Icon = AppIcon.Home, Shortcut = "⌘0", IsAvailable = () => home != null, Execute = () => home?.ShowHome() });
        Registry.Register(new AppCommand { Id = "app.logs", Title = "ログのフォルダを開く", Keywords = ["Diagnostics", "logs", "error"], Category = CommandCategory.Settings, Icon = AppIcon.Diagnostics, Execute = () => OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "logs")) });
        Registry.Register(new AppCommand { Id = "app.exit", Title = "GetText を終了", Keywords = ["Quit", "Exit", "終わる"], Icon = AppIcon.Exit, Shortcut = "⌘Q", IsAvailable = () => home != null, Execute = () => home?.Close() });
    }

    public static readonly (SettingsPage Page, string Title, string[] Keywords, AppIcon Icon)[] SettingsPages =
    [
        (SettingsPage.Appearance, "外観", ["Appearance", "テーマ", "文字の大きさ", "font"], AppIcon.Appearance),
        (SettingsPage.Shortcuts, "ショートカット", ["Shortcuts", "Keyboard", "キー"], AppIcon.Keyboard),
        (SettingsPage.Privacy, "プライバシー", ["Privacy", "送信", "許可", "画面収録"], AppIcon.Privacy),
        (SettingsPage.ScreenOcr, "文字の読み取り", ["Screen OCR", "OCR", "読み取り"], AppIcon.ScreenOcr),
        (SettingsPage.Translation, "翻訳", ["Translation", "DeepL", "Google"], AppIcon.Translate),
        (SettingsPage.Meetings, "議事録", ["Meetings", "Minutes", "会議"], AppIcon.Meeting),
        (SettingsPage.Recording, "画面の録画", ["Recording", "録画", "保存先"], AppIcon.Recording),
        (SettingsPage.Models, "モデルとセットアップ", ["Models", "Setup", "インストール", "AI"], AppIcon.Model),
        (SettingsPage.Extensions, "拡張機能", ["Extensions", "Plugins", "プラグイン", "アドオン", "追加"], AppIcon.Extensions),
        (SettingsPage.Diagnostics, "診断", ["Diagnostics", "ログ", "バージョン", "about"], AppIcon.Diagnostics),
    ];

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

    internal static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("open", [path]) { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log("OpenFolder", ex);
        }
    }
}
