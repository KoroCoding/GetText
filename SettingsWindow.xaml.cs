using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace GetText;

/// <summary>設定画面。変更はその場でメイン画面に反映し、保存する。</summary>
public partial class SettingsWindow : Window
{
    private readonly TextWindow _main;
    private readonly AppSettings _settings;
    private bool _loading = true;

    public SettingsWindow(TextWindow main, AppSettings settings)
    {
        InitializeComponent();
        _main = main;
        _settings = settings;
        // 小さな画面 (1280×720・拡大率 150% など) でも下の方まで見えるよう、画面に収める
        SourceInitialized += (_, _) => ScreenUtil.FitToWorkArea(this);
        // チェックの行は、説明の文字を押してもチェックが切り替わるようにする (小さなチェックだけを狙わなくてよい)
        AddHandler(System.Windows.Input.Mouse.MouseUpEvent, new System.Windows.Input.MouseButtonEventHandler(OnRowClick), true);
        AddHandler(System.Windows.Input.Mouse.MouseDownEvent, new System.Windows.Input.MouseButtonEventHandler((_, e) =>
            _pressedRow = e.ChangedButton == System.Windows.Input.MouseButton.Left ? RowOf(e.OriginalSource as DependencyObject) : null), true);

        OcrEngineBox.ItemsSource = SettingsOptions.OcrEngines;
        OcrEngineBox.SelectedItem = SettingsOptions.Find(SettingsOptions.OcrEngines, settings.OcrEngine);
        IntervalBox.ItemsSource = SettingsOptions.Intervals;
        IntervalBox.SelectedItem = SettingsOptions.Find(SettingsOptions.Intervals, settings.IntervalMs);
        JoinCheck.IsChecked = settings.JoinCjk;

        LanguageBox.ItemsSource = main.OcrLanguages;
        LanguageBox.SelectedItem = main.OcrLanguages.FirstOrDefault(o => o.Value == settings.Language)
                                   ?? main.OcrLanguages.FirstOrDefault();
        ScaleBox.ItemsSource = SettingsOptions.Scales;
        ScaleBox.SelectedItem = SettingsOptions.Find(SettingsOptions.Scales, settings.Scale);
        AccuracyCheck.IsChecked = settings.HighAccuracy;

        TranslateCheck.IsChecked = settings.Translate;
        EngineBox.ItemsSource = SettingsOptions.TranslationEngines;
        EngineBox.SelectedItem = SettingsOptions.Find(SettingsOptions.TranslationEngines, settings.TranslationEngine);

        MinutesAiTranslateCheck.IsChecked = settings.MinutesAiTranslate;
        MinutesVoicesCheck.IsChecked = settings.MinutesRememberVoices;
        LoadMinutesDisplay();

        ThemeBox.ItemsSource = SettingsOptions.Themes;
        ThemeBox.SelectedItem = SettingsOptions.Find(SettingsOptions.Themes, settings.Theme);
        LayoutBox.ItemsSource = SettingsOptions.Layouts;
        LayoutBox.SelectedItem = SettingsOptions.Find(SettingsOptions.Layouts, settings.Layout);
        FontSlider.Value = settings.FontSize;
        FontValue.Text = $"{settings.FontSize:0} pt";
        MinutesFontSlider.Value = settings.MinutesFontSize;
        MinutesFontValue.Text = $"{settings.MinutesFontSize:0} pt";
        WrapCheck.IsChecked = settings.Wrap;
        ConvertBox.ItemsSource = SettingsOptions.ConvertModes;
        ConvertBox.SelectedItem = SettingsOptions.Find(SettingsOptions.ConvertModes, settings.Convert);
        TopmostCheck.IsChecked = settings.Topmost;
        FollowCheck.IsChecked = settings.Follow;

        BuildShortcuts();
        UpdateDependentState();
        Activated += (_, _) => UpdateStatus();
        Loaded += (_, _) => UpdateStatus();
        _loading = false;
    }

    private Border? _pressedRow;

    private Border? RowOf(DependencyObject? node)
    {
        for (; node != null; node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(node) : null)
            if (node is Border { Style: { } style } row && style == (Style)FindResource("SettingRow")) return row;
        return null;
    }

    private void OnRowClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        var pressed = _pressedRow;
        _pressedRow = null;
        if (pressed == null || RowOf(source) != pressed) return; // 別の場所で押して離しただけ (ドロップダウンを閉じたときなど)
        // 押したのがボタン・チェック・入力欄などの操作の部品なら、その部品に任せる
        for (var node = source; node != null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase or ComboBox or Slider or TextBox) return;
            if (node is Border { Style: { } style } row && style == (Style)FindResource("SettingRow"))
            {
                var check = FindCheck(row);
                if (check is not { IsEnabled: true }) return;
                check.IsChecked = check.IsChecked != true;
                check.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                e.Handled = true;
                return;
            }
        }
    }

    private static CheckBox? FindCheck(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is CheckBox check) return check;
            if (FindCheck(child) is { } nested) return nested;
        }
        return null;
    }

    public void SelectTab(int index) => Tabs.SelectedIndex = Math.Clamp(index, 0, Tabs.Items.Count - 1);

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
        _loading = loading;
    }

    private void MinutesDisplay_Click(object sender, RoutedEventArgs e)
    {
        string name;
        if (sender == MinutesTimeCheck) { _settings.MinutesShowTime = MinutesTimeCheck.IsChecked == true; name = nameof(AppSettings.MinutesShowTime); }
        else if (sender == MinutesTranslateCheck) { _settings.MinutesTranslate = MinutesTranslateCheck.IsChecked == true; name = nameof(AppSettings.MinutesTranslate); }
        else if (sender == MinutesFillerCheck) { _settings.MinutesHideFillers = MinutesFillerCheck.IsChecked == true; name = nameof(AppSettings.MinutesHideFillers); }
        else { _settings.MinutesAutoScroll = MinutesScrollCheck.IsChecked == true; name = nameof(AppSettings.MinutesAutoScroll); }
        Changed(name);
    }

    private void Changed(string name)
    {
        if (_loading) return;
        _main.OnSettingChanged(name);
        UpdateDependentState();
    }

    /// <summary>他の設定によって意味がなくなる項目を無効にする。</summary>
    private void UpdateDependentState()
    {
        bool windowsOcr = _settings.OcrEngine == OcrEngineKind.Windows || !AiOcr.IsInstalled;
        LanguageBox.IsEnabled = ScaleBox.IsEnabled = AccuracyCheck.IsEnabled = windowsOcr;
        OcrEngineDescription.Text = _settings.OcrEngine == OcrEngineKind.Ai && !AiOcr.IsInstalled
            ? "AI は未セットアップのため、今は Windows 標準で読み取っています (「セットアップ・情報」から入れられます)。"
            : "AI は記号・表・小さな文字まで正確に読めます (要セットアップ)。使えないときは自動で Windows 標準に切り替わります。";

        EngineBox.IsEnabled = _settings.Translate;
        DeepLRow.Visibility = _settings.TranslationEngine == TranslationEngine.DeepL ? Visibility.Visible : Visibility.Collapsed;
        KeyStatus.Text = string.IsNullOrEmpty(_settings.GetDeepLKey())
            ? "未設定です。DeepL API Free (月 50 万文字まで無料) のキーを入力してください。"
            : "設定済み (この PC の Windows ユーザーだけが読める形で暗号化して保存しています)";
        EngineDescription.Text = _settings.TranslationEngine switch
        {
            TranslationEngine.Local => "PC 内の翻訳モデルを使います。回数制限・料金なし、文字を外部に送りません。",
            TranslationEngine.Google => "外国語の文を Google に送信します。短時間に大量に送ると一時的に制限されることがあります。",
            _ => "外国語の文を DeepL に送信します。安定していて訳の品質も高いです。",
        };
    }

    private void UpdateStatus()
    {
        AiOcrStatus.Text = !AiOcr.IsInstalled
            ? "未セットアップ — 上の「セットアップを実行」でインストールできます"
            : _main.AiOcr.Device switch
            {
                "gpu" => "動作中 (GPU)",
                "cpu" => "動作中 (CPU)",
                _ => "インストール済み (AI OCR を選ぶと起動します)",
            };
        LocalTranslationStatus.Text = !LocalTranslator.IsInstalled
            ? "未セットアップ — 上の「セットアップを実行」でインストールできます"
            : _settings.TranslationEngine == TranslationEngine.Local && _settings.Translate
                ? "インストール済み ・ " + _main.Translator.EngineName
                : "インストール済み";
        MinutesStatus.Text = TranscriptionService.IsInstalled
            ? TranscriptionService.IsMultilingualInstalled
                ? "インストール済み ・ 日本語・英語など (メイン画面の「議事録」から使えます)"
                : "インストール済み ・ 日本語のみ (英語などは「セットアップを実行」で追加できます)"
            : "未セットアップ — 上の「セットアップを実行」でインストールできます";
        bool missing = !AiOcr.IsInstalled || !TranscriptionService.IsInstalled;
        InfoTab.Header = missing ? "セットアップ・情報 ●" : "セットアップ・情報";
        SetupButton.Content = missing ? "セットアップを実行" : "セットアップをやり直す・追加";
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"GetText {version?.ToString(3)} ・ .NET {Environment.Version}";
    }

    private readonly List<UIElement> _globalRows = [];

    private void BuildShortcuts()
    {
        foreach (var row in _globalRows) ShortcutList.Children.Remove(row);
        _globalRows.Clear();
        (string Keys, string Action)[] local =
        [
            ("F5", "今すぐ読み取る"),
            ("Ctrl + P", "一時停止 / 再開"),
            ("Ctrl + Shift + C", "原文をコピー (選択範囲があればその部分)"),
            ("Ctrl + Shift + T", "日本語訳をコピー (選択範囲があればその部分)"),
            ("Ctrl + ,", "設定を開く"),
            ("Ctrl + ホイール", "文字の大きさを変える"),
            ("右クリック", "コピー・保存・文字変換のメニュー"),
        ];
        int index = ShortcutList.Children.IndexOf(LocalShortcutHeader);
        foreach (var (id, action, _) in HotkeyText.Actions)
        {
            var keys = HotkeyText.For(_settings, id);
            // 他のアプリが使っていて登録できなかったものは、そう書く
            bool failed = _main.FailedHotkeys.Contains(keys);
            var row = ShortcutRow(HotkeyText.Display(keys), failed ? action + " (他のアプリが使用中のため使えません。「変更」で別のキーにできます)" : action, id);
            _globalRows.Add(row);
            ShortcutList.Children.Insert(index++, row);
        }
        if (_localBuilt) return;
        _localBuilt = true;
        foreach (var (keys, action) in local) ShortcutList.Children.Add(ShortcutRow(keys, action));
    }

    private bool _localBuilt;

    /// <summary>どのアプリでも効くキーを変える (新しいキーを押してもらう)。</summary>
    private void ChangeHotkey(string id)
    {
        var (_, action, fallback) = HotkeyText.Actions.First(a => a.Id == id);
        string? chosen = null;
        var prompt = new TextBlock { Text = "新しいキーを押してください (Ctrl か Alt と一緒に)。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        var shown = new TextBlock { Text = HotkeyText.Display(HotkeyText.For(_settings, id)), FontSize = 18, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 14) };
        var ok = new Button { Content = "決定", MinWidth = 90, IsEnabled = false, Style = (Style)FindResource("AccentButtonStyle") };
        var reset = new Button { Content = $"元に戻す ({HotkeyText.Display(fallback)})", Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取り消し", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var w = new Window
        {
            Owner = this, Title = $"GetText — 「{action}」のキー", Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont"), FontSize = 13,
            Content = new StackPanel
            {
                Margin = new Thickness(20, 16, 20, 16),
                Children =
                {
                    prompt, shown,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { reset, ok, cancel } },
                },
            },
        };
        w.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (key is System.Windows.Input.Key.Escape or System.Windows.Input.Key.Tab) return;
            if (key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
            {
                if (ok.IsEnabled) w.DialogResult = true; // 選んだキーで決める (Enter そのものは選べない)
                e.Handled = true;
                return;
            }
            e.Handled = true;
            if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt
                or System.Windows.Input.Key.RightAlt or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
                or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin) return;
            var text = HotkeyText.Format(System.Windows.Input.Keyboard.Modifiers, key);
            bool valid = HotkeyText.TryParse(text, out System.Windows.Input.ModifierKeys _, out System.Windows.Input.Key _);
            bool taken = HotkeyText.Actions.Any(a => a.Id != id && HotkeyText.For(_settings, a.Id) == text);
            shown.Text = HotkeyText.Display(text);
            bool reserved = HotkeyText.IsReserved(System.Windows.Input.Keyboard.Modifiers, key);
            prompt.Text = reserved ? "Windows で決まった働きのあるキーです (Alt+F4 など)。別のキーを押してください。" : !valid ? "Ctrl か Alt と一緒に押してください。" : taken ? "ほかの操作で使っているキーです。別のキーを押してください。" : "このキーにするなら「決定」を押してください。";
            ok.IsEnabled = valid && !taken;
            chosen = valid && !taken ? text : null;
        };
        ok.Click += (_, _) => w.DialogResult = true;
        reset.Click += (_, _) => { chosen = fallback; w.DialogResult = true; };
        if (w.ShowDialog() != true || chosen == null) return;
        if (chosen == fallback) _settings.Hotkeys.Remove(id);
        else _settings.Hotkeys[id] = chosen;
        _settings.Save();
        _main.ReregisterHotkeys();
        BuildShortcuts();
    }

    private Border ShortcutRow(string keys, string action, string? hotkeyId = null)
    {
        var key = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("SubtleFillColorSecondaryBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            Child = new TextBlock { Text = keys, FontFamily = new System.Windows.Media.FontFamily("Consolas, Yu Gothic UI") },
        };
        DockPanel.SetDock(key, Dock.Right);
        var label = new TextBlock { Text = action, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(StyleProperty, "SettingLabel");
        var panel = new DockPanel();
        if (hotkeyId != null)
        {
            var change = new Button { Content = "変更…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center };
            change.Click += (_, _) => ChangeHotkey(hotkeyId);
            DockPanel.SetDock(change, Dock.Right);
            panel.Children.Add(change);
        }
        panel.Children.Add(key);
        panel.Children.Add(label);
        return new Border
        {
            Style = (Style)FindResource("SettingRow"),
            Child = panel,
        };
    }

    // ───────── 読み取り ─────────

    private void OcrEngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OcrEngineBox.SelectedItem is not Option<OcrEngineKind> o) return;
        _settings.OcrEngine = o.Value;
        Changed(nameof(AppSettings.OcrEngine));
    }

    private void IntervalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalBox.SelectedItem is not Option<int> o) return;
        _settings.IntervalMs = o.Value;
        Changed(nameof(AppSettings.IntervalMs));
    }

    private void JoinCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.JoinCjk = JoinCheck.IsChecked == true;
        Changed(nameof(AppSettings.JoinCjk));
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is not Option<string> o) return;
        _settings.Language = o.Value;
        Changed(nameof(AppSettings.Language));
    }

    private void ScaleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScaleBox.SelectedItem is not Option<double> o) return;
        _settings.Scale = o.Value;
        Changed(nameof(AppSettings.Scale));
    }

    private void AccuracyCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.HighAccuracy = AccuracyCheck.IsChecked == true;
        Changed(nameof(AppSettings.HighAccuracy));
    }

    // ───────── 翻訳 ─────────

    private void TranslateCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.Translate = TranslateCheck.IsChecked == true;
        Changed(nameof(AppSettings.Translate));
    }

    private void EngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EngineBox.SelectedItem is not Option<TranslationEngine> o) return;
        _settings.TranslationEngine = o.Value;
        Changed(nameof(AppSettings.TranslationEngine));
        if (!_loading && o.Value == TranslationEngine.DeepL && string.IsNullOrEmpty(_settings.GetDeepLKey()))
            KeyButton_Click(this, new RoutedEventArgs());
    }

    private void KeyButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new KeyDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _settings.SetDeepLKey(dialog.Key);
        Changed(nameof(AppSettings.DeepLKeyProtected));
    }

    // ───────── 議事録 ─────────

    private void MinutesAiTranslate_Click(object sender, RoutedEventArgs e)
    {
        _settings.MinutesAiTranslate = MinutesAiTranslateCheck.IsChecked == true;
        Changed(nameof(AppSettings.MinutesAiTranslate));
    }

    private void MinutesVoices_Click(object sender, RoutedEventArgs e)
    {
        _settings.MinutesRememberVoices = MinutesVoicesCheck.IsChecked == true;
        Changed(nameof(AppSettings.MinutesRememberVoices));
    }

    // ───────── 表示 ─────────

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is not Option<AppTheme> o) return;
        _settings.Theme = o.Value;
        Changed(nameof(AppSettings.Theme));
    }

    private void LayoutBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayoutBox.SelectedItem is not Option<PaneLayout> o) return;
        _settings.Layout = o.Value;
        Changed(nameof(AppSettings.Layout));
    }

    private void FontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontValue == null) return; // 初期化中
        FontValue.Text = $"{e.NewValue:0} pt";
        _settings.FontSize = e.NewValue;
        Changed(nameof(AppSettings.FontSize));
    }

    private void MinutesFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MinutesFontValue == null) return; // 初期化中
        MinutesFontValue.Text = $"{e.NewValue:0} pt";
        _settings.MinutesFontSize = e.NewValue;
        Changed(nameof(AppSettings.MinutesFontSize));
    }

    private void WrapCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.Wrap = WrapCheck.IsChecked == true;
        Changed(nameof(AppSettings.Wrap));
    }

    private void ConvertBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConvertBox.SelectedItem is not Option<ConvertMode> o) return;
        _settings.Convert = o.Value;
        Changed(nameof(AppSettings.Convert));
    }

    private void TopmostCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.Topmost = TopmostCheck.IsChecked == true;
        Changed(nameof(AppSettings.Topmost));
    }

    private void FollowCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.Follow = FollowCheck.IsChecked == true;
        Changed(nameof(AppSettings.Follow));
    }

    // ───────── 情報 ─────────

    private void Setup_Click(object sender, RoutedEventArgs e)
    {
        var setup = SetupDialog.FindSetupScript();
        if (setup == null)
        {
            // setup.bat が見つからないとき (古い配布版など) は、AI の環境だけを作る
            var script = Path.Combine(AppContext.BaseDirectory, "offline", "setup_offline.bat");
            if (!File.Exists(script))
            {
                MessageBox.Show(this, "セットアップ用のファイルが見つかりません: " + script, "GetText");
                return;
            }
            Process.Start(new ProcessStartInfo(script) { UseShellExecute = true });
            return;
        }
        var mode = SetupDialog.Ask(this);
        if (mode == null) return;
        // セットアップは AI の環境を置き換えるので、GetText を閉じてから始める (記録中に「止めない」を選んだら始めない)
        if (App.Home is { } home)
        {
            Close();
            home.ExitThen(() =>
            {
                if (!SetupDialog.Start(setup, mode))
                    MessageBox.Show("セットアップを起動できませんでした。GetText のフォルダの setup.bat を直接実行してください。", "GetText");
            });
            return;
        }
        var main = Application.Current.MainWindow;
        if (main == null)
        {
            SetupDialog.Start(setup, mode);
            return;
        }
        EventHandler start = (_, _) =>
        {
            if (!SetupDialog.Start(setup, mode))
                MessageBox.Show("セットアップを起動できませんでした。GetText のフォルダの setup.bat を直接実行してください。", "GetText");
        };
        main.Closed += start;
        Close();
        main.Close();
        // 閉じるのをやめた (記録中に「止めない」を選んだ) ときは、後で普通に閉じたときにセットアップを始めない
        // (残りの音声を文字にし終えてから閉じる途中なら、閉じたあとに始める)
        if (main.IsVisible && main is not TextWindow { ClosingAfterStop: true }) main.Closed -= start;
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GetText"));

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "logs"));

    private void OpenModelFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(PythonWorker.Root);

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
