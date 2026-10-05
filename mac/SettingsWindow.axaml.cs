using System.Diagnostics;
using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace GetText;

/// <summary>設定画面 (Mac 版)。変更はその場でメイン画面に反映し、保存する。</summary>
public partial class SettingsWindow : Window
{
    private readonly TextWindow _main;
    private readonly AppSettings _settings;
    private bool _loading = true;

    /// <summary>Mac で選べる読み取り方式 (「Windows」の値を Mac の文字認識として使う)。</summary>
    public static readonly Option<OcrEngineKind>[] OcrEngines =
    [
        new("Mac の文字認識 (Vision) — 追加インストール不要", OcrEngineKind.Windows),
        new("AI (PP-OCRv6) — 記号・表に強い (要セットアップ)", OcrEngineKind.Ai),
    ];

    public static readonly Option<AppTheme>[] Themes =
    [
        new("Mac の設定に合わせる", AppTheme.System), new("ライト", AppTheme.Light), new("ダーク", AppTheme.Dark),
    ];

    public SettingsWindow() : this(new TextWindow(), new AppSettings()) { }

    public SettingsWindow(TextWindow main, AppSettings settings)
    {
        InitializeComponent();
        _main = main;
        _settings = settings;

        OcrEngineBox.ItemsSource = OcrEngines;
        OcrEngineBox.SelectedItem = SettingsOptions.Find(OcrEngines, settings.OcrEngine);
        IntervalBox.ItemsSource = SettingsOptions.Intervals;
        IntervalBox.SelectedItem = SettingsOptions.Find(SettingsOptions.Intervals, settings.IntervalMs);
        JoinCheck.IsChecked = settings.JoinCjk;

        TranslateCheck.IsChecked = settings.Translate;
        EngineBox.ItemsSource = SettingsOptions.TranslationEngines;
        EngineBox.SelectedItem = SettingsOptions.Find(SettingsOptions.TranslationEngines, settings.TranslationEngine);

        MinutesAiTranslateCheck.IsChecked = settings.MinutesAiTranslate;
        MinutesVoicesCheck.IsChecked = settings.MinutesRememberVoices;

        ThemeBox.ItemsSource = Themes;
        ThemeBox.SelectedItem = SettingsOptions.Find(Themes, settings.Theme);
        LayoutBox.ItemsSource = SettingsOptions.Layouts;
        LayoutBox.SelectedItem = SettingsOptions.Find(SettingsOptions.Layouts, settings.Layout);
        FontSlider.Value = settings.FontSize;
        FontValue.Text = $"{settings.FontSize:0} pt";
        MinutesFontSlider.Value = settings.MinutesFontSize;
        MinutesFontValue.Text = $"{settings.MinutesFontSize:0} pt";
        MinutesTimeCheck.IsChecked = settings.MinutesShowTime;
        MinutesTranslateCheck.IsChecked = settings.MinutesTranslate;
        MinutesFillerCheck.IsChecked = settings.MinutesHideFillers;
        MinutesScrollCheck.IsChecked = settings.MinutesAutoScroll;
        WrapCheck.IsChecked = settings.Wrap;
        ConvertBox.ItemsSource = SettingsOptions.ConvertModes;
        ConvertBox.SelectedItem = SettingsOptions.Find(SettingsOptions.ConvertModes, settings.Convert);
        TopmostCheck.IsChecked = settings.Topmost;
        FollowCheck.IsChecked = settings.Follow;

        BuildShortcuts();
        UpdateDependentState();
        Activated += (_, _) => _ = UpdateStatusAsync();
        Opened += (_, _) => _ = UpdateStatusAsync();
        _loading = false;
    }

    public void SelectTab(int index) => Tabs.SelectedIndex = Math.Clamp(index, 0, Tabs.ItemCount - 1);

    /// <summary>「議事録」のページの番号。</summary>
    public const int MinutesTab = 3;

    /// <summary>議事録の表示の設定を読み直す (議事録の画面の「表示」で変えたとき)。</summary>
    public void LoadMinutesDisplay()
    {
        bool loading = _loading;
        _loading = true;
        MinutesTimeCheck.IsChecked = _settings.MinutesShowTime;
        MinutesTranslateCheck.IsChecked = _settings.MinutesTranslate;
        MinutesFillerCheck.IsChecked = _settings.MinutesHideFillers;
        MinutesScrollCheck.IsChecked = _settings.MinutesAutoScroll;
        MinutesFontSlider.Value = _settings.MinutesFontSize;
        MinutesFontValue.Text = $"{_settings.MinutesFontSize:0} pt";
        _loading = loading;
    }

    private void MinutesDisplay_Click(object? sender, RoutedEventArgs e)
    {
        string name;
        if (sender == MinutesTimeCheck) { _settings.MinutesShowTime = MinutesTimeCheck.IsChecked == true; name = nameof(AppSettings.MinutesShowTime); }
        else if (sender == MinutesTranslateCheck) { _settings.MinutesTranslate = MinutesTranslateCheck.IsChecked == true; name = nameof(AppSettings.MinutesTranslate); }
        else if (sender == MinutesFillerCheck) { _settings.MinutesHideFillers = MinutesFillerCheck.IsChecked == true; name = nameof(AppSettings.MinutesHideFillers); }
        else { _settings.MinutesAutoScroll = MinutesScrollCheck.IsChecked == true; name = nameof(AppSettings.MinutesAutoScroll); }
        _settings.Save();
        Changed(name);
    }

    private void Changed(string name)
    {
        if (_loading) return;
        _main.OnSettingChanged(name);
        UpdateDependentState();
    }

    private void UpdateDependentState()
    {
        EngineBox.IsEnabled = _settings.Translate;
        DeepLRow.IsVisible = _settings.TranslationEngine == TranslationEngine.DeepL;
        KeyStatus.Text = string.IsNullOrEmpty(_settings.GetDeepLKey())
            ? "未設定です。DeepL API Free (月 50 万文字まで無料) のキーを入力してください。"
            : "設定済み (Mac のキーチェーンに保存しています)";
        EngineDescription.Text = _settings.TranslationEngine switch
        {
            TranslationEngine.Local => "Mac 内の翻訳モデルを使います。回数制限・料金なし、文字を外部に送りません。",
            TranslationEngine.Google => "外国語の文を Google に送信します。短時間に大量に送ると一時的に制限されることがあります。",
            _ => "外国語の文を DeepL に送信します。安定していて訳の品質も高いです。",
        };
    }

    private async Task UpdateStatusAsync()
    {
        AiOcrStatus.Text = !AiOcr.IsInstalled
            ? "未セットアップ — 下の「セットアップを実行」でインストールできます (入れるまでは Mac の文字認識で読み取ります)"
            : _main.AiOcr.Device switch
            {
                "gpu" => "動作中 (GPU)",
                "cpu" => "動作中 (CPU)",
                _ => "インストール済み (AI を選ぶと起動します)",
            };
        LocalTranslationStatus.Text = !LocalTranslator.IsInstalled
            ? "未セットアップ — 下の「セットアップを実行」でインストールできます"
            : _settings.TranslationEngine == TranslationEngine.Local && _settings.Translate
                ? "インストール済み ・ " + _main.Translator.EngineName
                : "インストール済み";
        MinutesStatus.Text = TranscriptionService.IsInstalled
            ? TranscriptionService.IsMultilingualInstalled
                ? "インストール済み ・ 日本語・英語など (メイン画面の「議事録」から使えます)"
                : "インストール済み ・ 日本語のみ (英語などは「セットアップを実行」で追加できます)"
            : "未セットアップ — 下の「セットアップを実行」でインストールできます";
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"GetText for Mac {version?.ToString(3)} ・ .NET {Environment.Version}";
        try
        {
            var (screen, mic) = await MacServices.PermissionsAsync();
            PermissionStatus.Text = mic == "unavailable"
                ? "この環境では確かめられません"
                : $"画面収録とシステムオーディオ録音: {(screen ? "許可されています" : "未許可 (画面の文字の読み取り・アプリの音の取り込みに必要)")} ・ " +
                  $"マイク: {mic switch { "granted" => "許可されています", "denied" => "許可されていません", _ => "まだ聞かれていません (議事録でマイクを使うときに聞かれます)" }}";
        }
        catch (Exception ex)
        {
            PermissionStatus.Text = "確かめられませんでした: " + ex.Message;
        }
    }

    private void BuildShortcuts()
    {
        (string Keys, string Action)[] global =
        [
            ("⌃⌥C", "原文をコピー"),
            ("⌃⌥T", "日本語訳をコピー"),
            ("⌃⌥R", "今すぐ読み取る"),
            ("⌃⌥P", "一時停止 / 再開"),
        ];
        (string Keys, string Action)[] local =
        [
            ("⌘R / F5", "今すぐ読み取る"),
            ("⌘P", "一時停止 / 再開"),
            ("⌘⇧C", "原文をコピー (選択範囲があればその部分)"),
            ("⌘⇧T", "日本語訳をコピー (選択範囲があればその部分)"),
            ("⌘,", "設定を開く"),
            ("⌘ + スクロール", "文字の大きさを変える"),
            ("右クリック", "コピー・保存・文字変換のメニュー"),
            ("議事録: ⌘F / ⌘S / ⌘O", "検索 / 保存 / 開く"),
            ("議事録: ⌘M / ⌘B / ⌘Z", "メモ / ★ / 削除を元に戻す"),
            ("議事録: ⌘R", "記録を開始 / 停止"),
        ];
        int index = ShortcutList.Children.IndexOf(LocalShortcutHeader);
        foreach (var (keys, action) in global)
        {
            bool failed = _main.FailedHotkeys.Contains(keys);
            ShortcutList.Children.Insert(index++, ShortcutRow(keys, failed ? action + " (ほかのアプリが使っているため使えません)" : action));
        }
        foreach (var (keys, action) in local) ShortcutList.Children.Add(ShortcutRow(keys, action));
    }

    private static Border ShortcutRow(string keys, string action)
    {
        var key = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            Child = new TextBlock { Text = keys, FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")! },
        };
        key.Bind(Border.BackgroundProperty, key.GetResourceObservable("Subtle"));
        DockPanel.SetDock(key, Dock.Right);
        var label = new TextBlock { Text = action, VerticalAlignment = VerticalAlignment.Center, Classes = { "label" } };
        return new Border { Classes = { "row" }, Child = new DockPanel { Children = { key, label } } };
    }

    // ───────── 読み取り ─────────

    private void OcrEngineBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (OcrEngineBox.SelectedItem is not Option<OcrEngineKind> o) return;
        _settings.OcrEngine = o.Value;
        Changed(nameof(AppSettings.OcrEngine));
    }

    private void IntervalBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IntervalBox.SelectedItem is not Option<int> o) return;
        _settings.IntervalMs = o.Value;
        Changed(nameof(AppSettings.IntervalMs));
    }

    private void JoinCheck_Click(object? sender, RoutedEventArgs e)
    {
        _settings.JoinCjk = JoinCheck.IsChecked == true;
        Changed(nameof(AppSettings.JoinCjk));
    }

    // ───────── 翻訳 ─────────

    private void TranslateCheck_Click(object? sender, RoutedEventArgs e)
    {
        _settings.Translate = TranslateCheck.IsChecked == true;
        Changed(nameof(AppSettings.Translate));
    }

    private void EngineBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (EngineBox.SelectedItem is not Option<TranslationEngine> o) return;
        _settings.TranslationEngine = o.Value;
        Changed(nameof(AppSettings.TranslationEngine));
        if (!_loading && o.Value == TranslationEngine.DeepL && string.IsNullOrEmpty(_settings.GetDeepLKey()))
            KeyButton_Click(this, new RoutedEventArgs());
    }

    private async void KeyButton_Click(object? sender, RoutedEventArgs e)
    {
        if (await AskKeyAsync() is not { } key) return;
        try
        {
            _settings.SetDeepLKey(key);
        }
        catch (Exception ex)
        {
            await Dialogs.AlertAsync(this, "キーを保存できませんでした: " + ex.Message, "GetText");
            return;
        }
        Changed(nameof(AppSettings.DeepLKeyProtected));
    }

    // DeepL のキーを入力してもらう (空欄で保存すると消す)。取り消しなら null
    private async Task<string?> AskKeyAsync()
    {
        var w = new Window
        {
            Title = "DeepL API キー", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var box = new TextBox { PasswordChar = '•', Watermark = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx:fx" };
        string? result = null;
        var ok = new Button { Content = "保存", MinWidth = 90, IsDefault = true, Classes = { "accent" }, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "取り消し", MinWidth = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => { result = box.Text ?? ""; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock { Text = "DeepL のアカウント画面で発行した API キーを入力してください。キーは Mac のキーチェーンに保存します (空欄で保存すると消します)。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { ok, cancel } },
            },
        };
        w.Opened += (_, _) => box.Focus();
        await w.ShowDialog(this);
        return result;
    }

    // ───────── 議事録 ─────────

    private void MinutesAiTranslate_Click(object? sender, RoutedEventArgs e)
    {
        _settings.MinutesAiTranslate = MinutesAiTranslateCheck.IsChecked == true;
        Changed(nameof(AppSettings.MinutesAiTranslate));
    }

    private void MinutesVoices_Click(object? sender, RoutedEventArgs e)
    {
        _settings.MinutesRememberVoices = MinutesVoicesCheck.IsChecked == true;
        Changed(nameof(AppSettings.MinutesRememberVoices));
    }

    private void OpenScreenPrivacy_Click(object? sender, RoutedEventArgs e) =>
        Open("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture");

    private void OpenMicPrivacy_Click(object? sender, RoutedEventArgs e) =>
        Open("x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone");

    // ───────── 表示 ─────────

    private void ThemeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is not Option<AppTheme> o) return;
        _settings.Theme = o.Value;
        Changed(nameof(AppSettings.Theme));
    }

    private void LayoutBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LayoutBox.SelectedItem is not Option<PaneLayout> o) return;
        _settings.Layout = o.Value;
        Changed(nameof(AppSettings.Layout));
    }

    private void FontSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (FontValue == null) return;
        FontValue.Text = $"{e.NewValue:0} pt";
        _settings.FontSize = e.NewValue;
        Changed(nameof(AppSettings.FontSize));
    }

    private void MinutesFontSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (MinutesFontValue == null) return;
        MinutesFontValue.Text = $"{e.NewValue:0} pt";
        _settings.MinutesFontSize = e.NewValue;
        Changed(nameof(AppSettings.MinutesFontSize));
    }

    private void WrapCheck_Click(object? sender, RoutedEventArgs e)
    {
        _settings.Wrap = WrapCheck.IsChecked == true;
        Changed(nameof(AppSettings.Wrap));
    }

    private void ConvertBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ConvertBox.SelectedItem is not Option<ConvertMode> o) return;
        _settings.Convert = o.Value;
        Changed(nameof(AppSettings.Convert));
    }

    private void TopmostCheck_Click(object? sender, RoutedEventArgs e)
    {
        _settings.Topmost = TopmostCheck.IsChecked == true;
        Changed(nameof(AppSettings.Topmost));
    }

    private void FollowCheck_Click(object? sender, RoutedEventArgs e)
    {
        _settings.Follow = FollowCheck.IsChecked == true;
        Changed(nameof(AppSettings.Follow));
    }

    // ───────── 情報 ─────────

    /// <summary>セットアップ (setup_mac.sh) をターミナルで開く。</summary>
    private async void Setup_Click(object? sender, RoutedEventArgs e)
    {
        var script = Path.Combine(PythonWorker.ScriptDir, "..", "setup_mac.sh");
        script = Path.GetFullPath(File.Exists(script) ? script : Path.Combine(AppContext.BaseDirectory, "setup_mac.sh"));
        if (!File.Exists(script))
        {
            await Dialogs.AlertAsync(this, "セットアップ用のファイルが見つかりません: " + script, "GetText");
            return;
        }
        // 入れる機能を選ぶ (メモリ 16GB 以上ならフル、それより少なければ文脈による補正の AI (約 8GB) を除く標準をおすすめ)
        double ramGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024));
        var free = new DriveInfo("/").AvailableFreeSpace / (1024.0 * 1024 * 1024);
        int recommended = ramGb >= 15 ? 0 : 1;
        (string Label, string Detail, double Need, string Args)[] modes =
        [
            ("フル ・ ダウンロード 約 8GB", "文字の読み取り・英語の翻訳・議事録 (文脈による聞き間違いの補正まで)", 11, ""),
            ("標準 ・ ダウンロード 約 4GB", "フルから「文脈による聞き間違いの補正」と「要約」を除く (メモリ 16GB 未満の Mac 向け)", 7, " --no-llm"),
            ("最小 ・ ダウンロード 約 1GB", "文字の読み取りと英語の翻訳だけ (議事録は使えません)", 2, " --no-asr --english-only"),
        ];
        var choice = await Dialogs.ChooseAsync(this, "GetText — AI の機能のセットアップ", "入れる機能を選んでください。あとからもう一度セットアップすれば追加できます。",
            modes.Select((m, i) => (m.Label + (i == recommended ? "　(おすすめ)" : ""), m.Detail + (free >= m.Need ? "" : $"\n空きが足りません (必要 約 {m.Need:0}GB)"), free >= m.Need)).ToList(),
            free >= modes[recommended].Need ? recommended : free >= modes[2].Need ? 2 : -1, "セットアップを始める",
            $"この Mac のメモリ {ramGb:0}GB・空き容量 {free:0.#}GB ・ 回線によっては 30 分以上かかります。ターミナルで進み具合を表示します。");
        if (choice is not int index || index < 0 || free < modes[index].Need) return;
        try
        {
            // 選んだ機能の引数を付けた .command を作ってターミナルで開く (Apple Events の許可が要らず、パスの記号も安全に渡せる)
            static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
            var command = Path.Combine(Path.GetTempPath(), "GetText_セットアップ.command");
            File.WriteAllText(command, $"#!/bin/bash\nbash {Quote(script)}{modes[index].Args}\n");
            File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var open = Process.Start(new ProcessStartInfo("open", ["-a", "Terminal", command]) { UseShellExecute = false })!;
            await open.WaitForExitAsync();
            if (open.ExitCode != 0) throw new InvalidOperationException($"open が {open.ExitCode} で終わりました");
        }
        catch (Exception ex)
        {
            await Dialogs.AlertAsync(this, "ターミナルを開けませんでした: " + ex.Message + "\n\nターミナルで次を実行してください:\nbash \"" + script + "\"", "GetText");
        }
    }

    private void OpenSettingsFolder_Click(object? sender, RoutedEventArgs e) =>
        OpenFolder(Path.GetDirectoryName(AppSettings.SettingsPath)!);

    private void OpenLogFolder_Click(object? sender, RoutedEventArgs e) =>
        OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "logs"));

    private void OpenMinutesFolder_Click(object? sender, RoutedEventArgs e) =>
        OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "minutes"));

    private void OpenModelFolder_Click(object? sender, RoutedEventArgs e) => OpenFolder(PythonWorker.Root);

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Open(path);
    }

    private static void Open(string target)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("open", [target]) { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log("Open", ex);
        }
    }
}
