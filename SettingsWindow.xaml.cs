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

        LoadTranslation();
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
        TopmostCheck.IsChecked = settings.TextTopmost;
        MinutesTopmostCheck.IsChecked = settings.MinutesTopmost;
        RecorderTopmostCheck.IsChecked = settings.RecorderTopmost;
        FollowCheck.IsChecked = settings.Follow;
        OcrViewBox.ItemsSource = SettingsOptions.OcrViews;
        OcrViewBox.SelectedItem = SettingsOptions.Find(SettingsOptions.OcrViews, settings.OcrView);
        HomeMinimizeCheck.IsChecked = settings.HomeMinimizeOnOpen;
        RecordFolderText.Text = RecordFolder;
        RecordFolderText.ToolTip = RecordFolder;

        foreach (var page in Pages.Children.OfType<StackPanel>())
            if (page.Tag is string tag && Enum.TryParse<SettingsPage>(tag, out var p)) _pages[p] = page;
        foreach (var nav in NavPanel.Children.OfType<RadioButton>())
            if (nav.Tag is string tag && Enum.TryParse<SettingsPage>(tag, out var p)) _nav[p] = nav;

        BuildShortcuts();
        BuildPrivacy();
        LoadDeveloperApi();
        UpdateDependentState();
        PreviewKeyDown += (_, e) =>
        {
            if (AppCommands.HandleKey(this, e, CommandContext.Settings)) return;
            if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                SettingSearch.Focus();
                SettingSearch.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape && SettingSearch.Text.Length > 0)
            {
                SettingSearch.Text = "";
                e.Handled = true;
            }
        };
        Activated += (_, _) => { UpdateStatus(); BuildPrivacy(); };
        // ライト / ダークを変えたら、コードで色を付けた印 (プライバシー・モデルの状態) も塗り直す
        Action repaint = () => { BuildPrivacy(); UpdateStatus(); };
        Theme.Changed += repaint;
        Closed += (_, _) => Theme.Changed -= repaint;
        Loaded += (_, _) => UpdateStatus();
        SelectPage(SettingsPage.Appearance);
        _loading = false;
    }

    private readonly Dictionary<SettingsPage, StackPanel> _pages = [];
    private readonly Dictionary<SettingsPage, RadioButton> _nav = [];
    private SettingsPage _page = SettingsPage.Appearance;

    /// <summary>今開いているページ。</summary>
    public SettingsPage CurrentPage => _page;

    /// <summary>ページを開く (検索していれば検索を消す)。</summary>
    public void SelectPage(SettingsPage page)
    {
        if (_nav.TryGetValue(page, out var nav))
        {
            if (nav.IsChecked == true) ShowPage(page);
            else nav.IsChecked = true; // (Nav_Checked でページを出す)
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<SettingsPage>(tag, out var page)) ShowPage(page);
    }

    private void ShowPage(SettingsPage page)
    {
        _page = page;
        if (SettingSearch.Text.Length > 0) SettingSearch.Text = ""; // (検索の結果を閉じる)
        foreach (var (p, panel) in _pages) panel.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        SearchResults.Visibility = Visibility.Collapsed;
        PageTitleText.Text = AppCommands.SettingsPages.First(x => x.Page == page).Title;
        if (page == SettingsPage.Extensions) RefreshExtensions();
        PageScroll.ScrollToTop();
    }

    // ───────── 設定の検索 ─────────

    public sealed record SettingHit(string Label, string Where, SettingsPage Page, Border Row);

    /// <summary>すべてのページの設定の行 (名前・説明) から探す。</summary>
    internal List<SettingHit> FindSettings(string query)
    {
        var tokens = CommandSearch.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<SettingHit>();
        if (tokens.Length == 0) return hits;
        foreach (var (page, panel) in _pages)
        {
            string pageTitle = AppCommands.SettingsPages.First(x => x.Page == page).Title;
            foreach (var row in LogicalRows(panel))
            {
                var texts = LogicalTexts(row).ToList();
                var label = texts.FirstOrDefault(t => t.Style == (Style)FindResource("SettingLabel"))?.Text;
                if (string.IsNullOrEmpty(label)) continue;
                var haystack = CommandSearch.Normalize(pageTitle + " " + string.Join(" ", texts.Select(t => t.Text)));
                if (tokens.All(t => haystack.Contains(t, StringComparison.Ordinal))) hits.Add(new SettingHit(label, pageTitle, page, row));
            }
        }
        return hits;
    }

    private IEnumerable<Border> LogicalRows(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Border { Style: { } style } row && style == (Style)FindResource("SettingRow")) yield return row;
            else foreach (var nested in LogicalRows(child)) yield return nested;
        }
    }

    private static IEnumerable<TextBlock> LogicalTexts(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock t) yield return t;
            foreach (var nested in LogicalTexts(child)) yield return nested;
        }
    }

    private void SettingSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool searching = SettingSearch.Text.Length > 0;
        SettingSearchPlaceholder.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        SettingSearchClear.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        if (!searching)
        {
            ShowPage(_page);
            return;
        }
        var hits = FindSettings(SettingSearch.Text);
        foreach (var panel in _pages.Values) panel.Visibility = Visibility.Collapsed;
        SearchResults.Visibility = Visibility.Visible;
        SearchResultList.ItemsSource = hits;
        SearchEmpty.Visibility = hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageTitleText.Text = hits.Count == 0 ? "検索" : $"検索 ・ {hits.Count} 件";
    }

    /// <summary>見本の画面・動作確認: 設定を検索する。</summary>
    internal void ShowDemoSearch(string query) => SettingSearch.Text = query;

    private void SettingSearchClear_Click(object sender, RoutedEventArgs e)
    {
        SettingSearch.Text = "";
        SettingSearch.Focus();
    }

    /// <summary>検索の結果を押した: そのページを開き、その行を見える所に出して少しの間 枠で示す。</summary>
    private void SearchResult_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SettingHit hit) return;
        SelectPage(hit.Page);
        // 詳細設定にたたんである行なら開く
        for (DependencyObject? node = hit.Row; node != null; node = LogicalTreeHelper.GetParent(node))
            if (node is Expander expander) expander.IsExpanded = true;
        Dispatcher.BeginInvoke(() =>
        {
            hit.Row.BringIntoView();
            var before = hit.Row.BorderBrush;
            hit.Row.BorderBrush = (System.Windows.Media.Brush)FindResource("Gt.FocusRing");
            hit.Row.BorderThickness = new Thickness(2);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                hit.Row.SetResourceReference(Border.BorderBrushProperty, "Gt.Border");
                hit.Row.BorderThickness = new Thickness(1);
            };
            timer.Start();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ───────── プライバシー ─────────

    public sealed record PrivacyRow(string Label, string Description, bool Online, SettingsPage? Page, string ActionLabel)
    {
        public string PillText => Online ? "オンライン" : "PC 内";
        public string PillGlyph => WindowsIcons.Glyph(Online ? AppIcon.Cloud : AppIcon.Local);
        public System.Windows.Media.Brush PillBackground => Theme.Brush(p => Online ? p.WarningSubtle : p.SuccessSubtle);
        public System.Windows.Media.Brush PillForeground => Theme.Brush(p => Online ? p.Warning : p.Success);
        public Visibility ActionVisibility => Page == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>何を PC の中で処理し、何をインターネットに送るか (今の設定で)。</summary>
    private void BuildPrivacy()
    {
        bool cloud = _settings.Translate && _settings.TranslationEngine != TranslationEngine.Local;
        string service = _settings.TranslationEngine == TranslationEngine.DeepL ? "DeepL" : "Google";
        var plugins = PluginPermissions.Privacy(PluginRuntime.Plugins);
        PrivacyList.ItemsSource = new List<PrivacyRow>
        {
            new("文字の読み取り", "枠の中の画面は PC の中で読み取ります (AI OCR・Windows OCR とも)。画像は保存しません。", false, SettingsPage.ScreenOcr, "設定"),
            new("翻訳", cloud ? $"外国語と判定した文を {service} に送って訳します。" : "PC の中の翻訳モデルで訳します。文字は外に送りません。", cloud, SettingsPage.Translation, "変更"),
            new("議事録の文字起こし", "会議の音声は PC の中で文字にします。" + (_settings.MinutesSaveAudio ? "聞き直せるよう、音声を PC に保存します。" : "音声は保存しません。"), false, SettingsPage.Meetings, "設定"),
            new("話者の声の記憶", _settings.MinutesRememberVoices ? "名前を付けた話者の声の特徴 (数値) を PC に覚えます。音声そのものは保存しません。" : "覚えません。", false, SettingsPage.Meetings, "設定"),
            new("画面の録画", "録画は PC のフォルダに保存します。どこにも送りません。", false, SettingsPage.Recording, "設定"),
            new("セットアップ", "AI のモデルを入れるときだけ、配布元 (Hugging Face など) からダウンロードします。", true, SettingsPage.Models, "開く"),
            new("拡張機能", plugins.Text, plugins.Online, SettingsPage.Extensions, "開く"),
        };
    }

    private void PrivacyAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SettingsPage page) SelectPage(page);
    }

    // ───────── 外観・ホーム・録画 ─────────

    private void HomeMinimize_Click(object sender, RoutedEventArgs e)
    {
        _settings.HomeMinimizeOnOpen = HomeMinimizeCheck.IsChecked == true;
        _settings.Save();
    }

    private void OcrViewBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OcrViewBox.SelectedItem is not Option<OcrView> o) return;
        _main.SetView(o.Value);
    }

    private string RecordFolder => string.IsNullOrWhiteSpace(_settings.RecordFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "GetText")
        : _settings.RecordFolder;

    private void RecordFolderChange_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "録画の保存先", InitialDirectory = Directory.Exists(RecordFolder) ? RecordFolder : null };
        if (dialog.ShowDialog(this) != true) return;
        _settings.RecordFolder = dialog.FolderName;
        _settings.Save();
        RecordFolderText.Text = RecordFolder;
        RecordFolderText.ToolTip = RecordFolder;
        App.Home?.Recorder?.ReloadFolder();
    }

    private void RecordFolderOpen_Click(object sender, RoutedEventArgs e) => OpenFolder(RecordFolder);

    private void OpenRecorder_Click(object sender, RoutedEventArgs e) => App.Home?.OpenRecorder();

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
            ? "AI は未セットアップのため、今は Windows 標準で読み取っています (「モデルとセットアップ」から入れられます)。"
            : "AI は記号・表・小さな文字まで正確に読めます (要セットアップ)。使えないときは自動で Windows 標準に切り替わります。";

        EngineBox.IsEnabled = _settings.Translate;
        OverlayCheck.IsChecked = _settings.Translate && _settings.TranslationOverlay; // (翻訳を切ると重ねるのも止まる)
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
            ? "上の「セットアップを実行」で入れられます"
            : _main.AiOcr.Device switch
            {
                "gpu" => "動作中 (GPU)",
                "cpu" => "動作中 (CPU)",
                _ => "インストール済み (AI OCR を選ぶと起動します)",
            };
        LocalTranslationStatus.Text = !LocalTranslator.IsInstalled
            ? "上の「セットアップを実行」で入れられます"
            : _settings.TranslationEngine == TranslationEngine.Local && _settings.Translate
                ? "インストール済み ・ " + _main.Translator.EngineName
                : "インストール済み";
        MinutesStatus.Text = TranscriptionService.IsInstalled
            ? TranscriptionService.IsMultilingualInstalled
                ? "インストール済み ・ 日本語・英語など (メイン画面の「議事録」から使えます)"
                : "インストール済み ・ 日本語のみ (英語などは「セットアップを実行」で追加できます)"
            : "未セットアップ — 上の「セットアップを実行」でインストールできます";
        bool missing = !AiOcr.IsInstalled || !TranscriptionService.IsInstalled;
        ModelsBadge.Visibility = missing && !App.DemoMode ? Visibility.Visible : Visibility.Collapsed;
        SetupButton.Content = missing ? "セットアップを実行" : "セットアップをやり直す・追加";
        SetPill(AiOcrPill, AiOcrPillText, AiOcr.IsInstalled);
        SetPill(TranslationPill, TranslationPillText, LocalTranslator.IsInstalled);
        SetPill(MinutesPill, MinutesPillText, TranscriptionService.IsInstalled);
        if (_demoModels == null && _modelCancel == null) RefreshModels();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"GetText {version?.ToString(3)} ・ .NET {Environment.Version}";
    }

    // 入っている / 入っていない の印 (色と文字の両方で)
    private void SetPill(Border pill, TextBlock text, bool installed)
    {
        pill.Background = Theme.Brush(p => installed ? p.SuccessSubtle : p.SurfaceSecondary);
        text.Foreground = Theme.Brush(p => installed ? p.Success : p.TextSecondary);
        text.Text = installed ? "入っています" : "未セットアップ";
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
            prompt.Text = reserved ? "ほかのアプリの操作 (Ctrl+C のコピー・Alt+F4 など) を奪うキーです。Ctrl+Alt や Ctrl+Shift と一緒に押してください。" : !valid ? "Ctrl か Alt と一緒に押してください。" : taken ? "ほかの操作で使っているキーです。別のキーを押してください。" : "このキーにするなら「決定」を押してください。";
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

    /// <summary>翻訳する・訳を重ねるの表示を今の設定に合わせる (読み取りの画面の 表示 で切り替えたとき)。</summary>
    public void LoadTranslation()
    {
        TranslateCheck.IsChecked = _settings.Translate;
        OverlayCheck.IsChecked = _settings.Translate && _settings.TranslationOverlay;
        EngineBox.IsEnabled = _settings.Translate;
    }

    private void OverlayCheck_Click(object sender, RoutedEventArgs e)
    {
        // 重ねるには翻訳が要る (読み取りの画面の 表示 → 訳を画面に重ねる と同じ)
        if (OverlayCheck.IsChecked == true != (_settings.Translate && _settings.TranslationOverlay)) _main.ToggleOverlay();
        LoadTranslation();
        UpdateDependentState();
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

    /// <summary>議事録・画面の録画の「常に手前に表示」(窓ごと)。</summary>
    private void WindowTopmost_Click(object sender, RoutedEventArgs e)
    {
        if (sender == MinutesTopmostCheck)
        {
            _settings.MinutesTopmost = MinutesTopmostCheck.IsChecked == true;
            Changed(nameof(AppSettings.MinutesTopmost));
        }
        else
        {
            _settings.RecorderTopmost = RecorderTopmostCheck.IsChecked == true;
            Changed(nameof(AppSettings.RecorderTopmost));
        }
    }

    private void TopmostCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.TextTopmost = TopmostCheck.IsChecked == true;
        Changed(nameof(AppSettings.TextTopmost));
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
