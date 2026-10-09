using System.Diagnostics;
using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace GetText;

/// <summary>設定の検索で見つかった行。</summary>
public sealed record SettingHit(string Label, string Where, SettingsPage Page, Border Row);

/// <summary>プライバシーのページの 1 行 (何を Mac の中で処理し、何を送るか)。</summary>
public sealed record PrivacyRow(string Label, string Description, bool Online, SettingsPage? Page, string ActionLabel)
{
    public string PillText => Online ? "オンライン" : "Mac 内";
    public AppIcon PillIcon => Online ? AppIcon.Cloud : AppIcon.Local;
    public IBrush PillBackground => MacTheme.BrushOf(p => Online ? p.WarningSubtle : p.SuccessSubtle);
    public IBrush PillForeground => MacTheme.BrushOf(p => Online ? p.Warning : p.Success);
    public bool HasAction => Page != null;
}

/// <summary>設定画面 (Mac 版)。一般 / 機能 / 拡張 / 詳細 のページと検索。変更はその場でメイン画面に反映し、保存する。</summary>
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
        OcrViewBox.ItemsSource = SettingsOptions.OcrViews;
        OcrViewBox.SelectedItem = SettingsOptions.Find(SettingsOptions.OcrViews, settings.OcrView);

        LoadTranslation();
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
        TopmostCheck.IsChecked = settings.TextTopmost;
        MinutesTopmostCheck.IsChecked = settings.MinutesTopmost;
        RecorderTopmostCheck.IsChecked = settings.RecorderTopmost;
        FollowCheck.IsChecked = settings.Follow;
        HomeMinimizeCheck.IsChecked = settings.HomeMinimizeOnOpen;
        RecordFolderText.Text = RecordFolder;
        ToolTip.SetTip(RecordFolderText, RecordFolder);

        foreach (var page in Pages.Children.OfType<StackPanel>())
            if (page.Tag is string tag && Enum.TryParse<SettingsPage>(tag, out var p)) _pages[p] = page;
        foreach (var nav in NavPanel.Children.OfType<RadioButton>())
            if (nav.Tag is string tag && Enum.TryParse<SettingsPage>(tag, out var p)) _nav[p] = nav;

        SettingSearch.TextChanged += (_, _) => SettingSearch_TextChanged();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (AppCommands.HandleKey(this, e, CommandContext.Settings)) return;
            var cmd = TopLevel.GetTopLevel(this)?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Meta;
            if (e.Key == Key.F && e.KeyModifiers == cmd)
            {
                SettingSearch.Focus();
                SettingSearch.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && !string.IsNullOrEmpty(SettingSearch.Text))
            {
                SettingSearch.Text = "";
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        BuildShortcuts();
        BuildPrivacy();
        LoadDeveloperApi();
        UpdateDependentState();
        Activated += (_, _) => { _ = UpdateStatusAsync(); BuildPrivacy(); };
        ActualThemeVariantChanged += (_, _) => { BuildPrivacy(); _ = UpdateStatusAsync(); }; // (コードで色を付けた印を塗り直す)
        Opened += (_, _) => _ = UpdateStatusAsync();
        SelectPage(SettingsPage.Appearance);
        _loading = false;
    }

    private readonly Dictionary<SettingsPage, StackPanel> _pages = [];
    private readonly Dictionary<SettingsPage, RadioButton> _nav = [];
    private SettingsPage _page = SettingsPage.Appearance;

    public SettingsPage CurrentPage => _page;

    /// <summary>ページを開く (検索していれば検索を消す)。</summary>
    public void SelectPage(SettingsPage page)
    {
        if (!_nav.TryGetValue(page, out var nav)) return;
        if (nav.IsChecked == true) ShowPage(page);
        else nav.IsChecked = true; // (Nav_Checked でページを出す)
    }

    private void Nav_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true, Tag: string tag } && Enum.TryParse<SettingsPage>(tag, out var page)) ShowPage(page);
    }

    private void ShowPage(SettingsPage page)
    {
        _page = page;
        if (!string.IsNullOrEmpty(SettingSearch.Text)) SettingSearch.Text = "";
        foreach (var (p, panel) in _pages) panel.IsVisible = p == page;
        SearchResults.IsVisible = false;
        PageTitleText.Text = AppCommands.SettingsPages.First(x => x.Page == page).Title;
        if (page == SettingsPage.Extensions) RefreshExtensions();
        PageScroll.Offset = default;
    }

    // ───────── 設定の検索 ─────────

    internal List<SettingHit> FindSettings(string query)
    {
        var tokens = CommandSearch.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<SettingHit>();
        if (tokens.Length == 0) return hits;
        foreach (var (page, panel) in _pages)
        {
            string pageTitle = AppCommands.SettingsPages.First(x => x.Page == page).Title;
            foreach (var row in panel.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("row")))
            {
                var texts = row.GetLogicalDescendants().OfType<TextBlock>().ToList();
                var label = texts.FirstOrDefault(t => t.Classes.Contains("label"))?.Text;
                if (string.IsNullOrEmpty(label)) continue;
                var haystack = CommandSearch.Normalize(pageTitle + " " + string.Join(" ", texts.Select(t => t.Text)));
                if (tokens.All(t => haystack.Contains(t, StringComparison.Ordinal))) hits.Add(new SettingHit(label, pageTitle, page, row));
            }
        }
        return hits;
    }

    private void SettingSearch_TextChanged()
    {
        var query = SettingSearch.Text ?? "";
        if (query.Length == 0)
        {
            ShowPage(_page);
            return;
        }
        var hits = FindSettings(query);
        foreach (var panel in _pages.Values) panel.IsVisible = false;
        SearchResults.IsVisible = true;
        SearchResultList.ItemsSource = hits;
        SearchEmpty.IsVisible = hits.Count == 0;
        PageTitleText.Text = hits.Count == 0 ? "検索" : $"検索 ・ {hits.Count} 件";
    }

    /// <summary>見本の画面・動作確認: 設定を検索する。</summary>
    internal void ShowDemoSearch(string query) => SettingSearch.Text = query;

    private void SearchResult_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SettingHit hit) return;
        SelectPage(hit.Page);
        foreach (var expander in hit.Row.GetLogicalAncestors().OfType<Expander>()) expander.IsExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            hit.Row.BringIntoView();
            hit.Row.BorderBrush = MacTheme.BrushOf(p => p.FocusRing);
            hit.Row.BorderThickness = new Thickness(2);
            DispatcherTimer.RunOnce(() =>
            {
                hit.Row.ClearValue(Border.BorderBrushProperty);
                hit.Row.ClearValue(Border.BorderThicknessProperty);
            }, TimeSpan.FromSeconds(1.6));
        }, DispatcherPriority.Loaded);
    }

    // ───────── プライバシー ─────────

    private void BuildPrivacy()
    {
        bool cloud = _settings.Translate && _settings.TranslationEngine != TranslationEngine.Local;
        string service = _settings.TranslationEngine == TranslationEngine.DeepL ? "DeepL" : "Google";
        var plugins = PluginPermissions.Privacy(PluginRuntime.Plugins);
        PrivacyList.ItemsSource = new List<PrivacyRow>
        {
            new("文字の読み取り", "枠の中の画面は Mac の中で読み取ります (AI OCR・Mac の文字認識とも)。画像は保存しません。", false, SettingsPage.ScreenOcr, "設定"),
            new("翻訳", cloud ? $"外国語と判定した文を {service} に送って訳します。" : "Mac の中の翻訳モデルで訳します。文字は外に送りません。", cloud, SettingsPage.Translation, "変更"),
            new("議事録の文字起こし", "会議の音声は Mac の中で文字にします。" + (_settings.MinutesSaveAudio ? "聞き直せるよう、音声を Mac に保存します。" : "音声は保存しません。"), false, SettingsPage.Meetings, "設定"),
            new("話者の声の記憶", _settings.MinutesRememberVoices ? "名前を付けた話者の声の特徴 (数値) を Mac に覚えます。音声そのものは保存しません。" : "覚えません。", false, SettingsPage.Meetings, "設定"),
            new("画面の録画", "録画は Mac のフォルダに保存します。どこにも送りません。", false, SettingsPage.Recording, "設定"),
            new("セットアップ", "AI のモデルを入れるときだけ、配布元 (Hugging Face など) からダウンロードします。", true, SettingsPage.Models, "開く"),
            new("拡張機能", plugins.Text, plugins.Online, SettingsPage.Extensions, "開く"),
        };
    }

    private void PrivacyAction_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is SettingsPage page) SelectPage(page);
    }

    // ───────── 外観・ホーム・録画 ─────────

    private void HomeMinimize_Click(object? sender, RoutedEventArgs e)
    {
        _settings.HomeMinimizeOnOpen = HomeMinimizeCheck.IsChecked == true;
        _settings.Save();
    }

    private void OcrViewBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || OcrViewBox.SelectedItem is not Option<OcrView> o) return;
        _main.SetView(o.Value);
    }

    private string RecordFolder => string.IsNullOrWhiteSpace(_settings.RecordFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "GetText")
        : _settings.RecordFolder;

    private async void RecordFolderChange_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "録画の保存先" });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } folder) return;
        _settings.RecordFolder = folder;
        _settings.Save();
        RecordFolderText.Text = RecordFolder;
        ToolTip.SetTip(RecordFolderText, RecordFolder);
        App.Home?.Recorder?.ReloadFolder();
    }

    private void RecordFolderOpen_Click(object? sender, RoutedEventArgs e) => OpenFolder(RecordFolder);

    private void OpenRecorder_Click(object? sender, RoutedEventArgs e) => App.Home?.OpenRecorder();

    // 入っている / 入っていない の印 (色と文字の両方で)
    private static void SetPill(Border pill, TextBlock text, bool installed)
    {
        pill.Background = MacTheme.BrushOf(p => installed ? p.SuccessSubtle : p.SurfaceSecondary);
        text.Foreground = MacTheme.BrushOf(p => installed ? p.Success : p.TextSecondary);
        text.Text = installed ? "入っています" : "未セットアップ";
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
        OverlayCheck.IsChecked = _settings.Translate && _settings.TranslationOverlay; // (翻訳を切ると重ねるのも止まる)
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
            ? "上の「セットアップを実行」で入れられます (入れるまでは Mac の文字認識で読み取ります)"
            : _main.AiOcr.Device switch
            {
                "gpu" => "動作中 (GPU)",
                "cpu" => "動作中 (CPU)",
                _ => "インストール済み (AI を選ぶと起動します)",
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
            : "上の「セットアップを実行」で入れられます";
        bool missing = !AiOcr.IsInstalled || !TranscriptionService.IsInstalled;
        ModelsBadge.IsVisible = missing && !App.DemoMode;
        SetupButton.Content = missing ? "セットアップを実行" : "セットアップをやり直す・追加";
        SetPill(AiOcrPill, AiOcrPillText, AiOcr.IsInstalled);
        SetPill(TranslationPill, TranslationPillText, LocalTranslator.IsInstalled);
        SetPill(MinutesPill, MinutesPillText, TranscriptionService.IsInstalled);
        if (_demoModels == null && _modelCancel == null) RefreshModels();
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

    /// <summary>翻訳する・訳を重ねるの表示を今の設定に合わせる (読み取りの画面の 表示 で切り替えたとき)。</summary>
    public void LoadTranslation()
    {
        TranslateCheck.IsChecked = _settings.Translate;
        OverlayCheck.IsChecked = _settings.Translate && _settings.TranslationOverlay;
        EngineBox.IsEnabled = _settings.Translate;
    }

    private void OverlayCheck_Click(object? sender, RoutedEventArgs e)
    {
        // 重ねるには翻訳が要る (読み取りの画面の 表示 → 訳を画面に重ねる と同じ)
        if (OverlayCheck.IsChecked == true != (_settings.Translate && _settings.TranslationOverlay)) _main.ToggleOverlay();
        LoadTranslation();
        UpdateDependentState();
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

    /// <summary>議事録・画面の録画の「常に手前に表示」(窓ごと)。</summary>
    private void WindowTopmost_Click(object? sender, RoutedEventArgs e)
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

    private void TopmostCheck_Click(object? sender, RoutedEventArgs e)
    {
        _settings.TextTopmost = TopmostCheck.IsChecked == true;
        Changed(nameof(AppSettings.TextTopmost));
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
