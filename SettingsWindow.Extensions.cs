using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GetText.Plugins;
using Microsoft.Win32;

namespace GetText;

/// <summary>設定 → 拡張機能 (入れたもの・見つける・更新)。一覧の中身と文は PluginCatalog・PluginTexts (Windows 版・Mac 版で共有)。</summary>
public partial class SettingsWindow
{
    private PluginCatalog? _catalog;
    private string _extTab = "Installed";
    private bool _extBusy;
    private bool _extDemo;

    private PluginCatalog Catalog => _catalog ??= PluginCatalog.ForApp(App.DemoMode);

    /// <summary>見本の画面: 見本の一覧で拡張機能のページを描く (通信・書き込みはしない)。</summary>
    internal void ShowDemoExtensions(PluginCatalog catalog, string tab, string? error = null, string? progress = null)
    {
        _extDemo = true;
        _catalog = catalog;
        SelectPage(SettingsPage.Extensions);
        (tab switch { "Discover" => ExtTabDiscover, "Updates" => ExtTabUpdates, _ => ExtTabInstalled }).IsChecked = true;
        _extTab = tab;
        ExtErrorBar.Visibility = Visibility.Collapsed;
        ExtProgressPanel.Visibility = Visibility.Collapsed;
        RefreshExtensions();
        if (error != null) ShowExtError(error, warning: true);
        if (progress != null)
        {
            ExtProgressPanel.Visibility = Visibility.Visible;
            ExtProgressText.Text = progress;
            ExtProgress.IsIndeterminate = false;
            ExtProgress.Value = 0.42;
        }
    }

    /// <summary>拡張機能のページを描き直す (開いたとき・操作した後)。</summary>
    private void RefreshExtensions()
    {
        if (!_pages.ContainsKey(SettingsPage.Extensions)) return;
        var catalog = Catalog;
        var installed = catalog.Installed();
        var updates = installed.Where(i => i.HasUpdate && !i.PendingRemoval).ToList();
        ExtTabInstalledText.Text = installed.Count > 0 ? $"入れたもの {installed.Count}" : "入れたもの";
        ExtTabUpdatesText.Text = updates.Count > 0 ? $"更新 {updates.Count}" : "更新";

        var items = _extTab switch { "Discover" => catalog.Available(), "Updates" => updates, _ => installed };
        var shown = PluginCatalog.Filter(items, ExtSearch.Text);
        ExtList.ItemsSource = shown.Select(i => new ExtensionRow(i, _extBusy)).ToList(); // (見本では押しても何もしない)
        ExtEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        (ExtEmptyTitle.Text, ExtEmptyDetail.Text) = PluginTexts.Empty(_extTab, catalog, ExtSearch.Text.Length > 0, items.Count);

        ExtOnlineButton.Content = _extTab == "Updates" ? "更新を確認" : "オンラインで探す";
        ExtOnlineButton.IsEnabled = !_extBusy;
        ExtSourceText.Text = PluginTexts.Source(catalog, PluginRuntime.SafeMode);

        bool restart = installed.Any(i => i.State == PluginItemState.RestartRequired);
        ExtRestartBar.Visibility = restart ? Visibility.Visible : Visibility.Collapsed;
        ExtensionsBadge.Visibility = restart || updates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExtensionsBadgeText.Text = restart ? "再起動" : $"更新 {updates.Count}";
        ExtNotice.Text = PluginPermissions.Notice;
    }

    private void ExtTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tab }) return;
        _extTab = tab;
        if (!_loading) RefreshExtensions();
    }

    private void ExtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool searching = ExtSearch.Text.Length > 0;
        ExtSearchPlaceholder.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        ExtSearchClear.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        if (!_loading) RefreshExtensions();
    }

    private void ExtSearchClear_Click(object sender, RoutedEventArgs e)
    {
        ExtSearch.Text = "";
        ExtSearch.Focus();
    }

    private void ShowExtError(string text, bool warning = false)
    {
        ExtErrorText.Text = text;
        ExtErrorIcon.Text = WindowsIcons.Glyph(warning ? AppIcon.Warning : AppIcon.Error);
        ExtErrorIcon.Foreground = Theme.Brush(p => warning ? p.Warning : p.Critical);
        ExtErrorBar.Background = Theme.Brush(p => warning ? p.WarningSubtle : p.CriticalSubtle);
        ExtErrorBar.Visibility = Visibility.Visible;
    }

    private void ExtErrorClose_Click(object sender, RoutedEventArgs e) => ExtErrorBar.Visibility = Visibility.Collapsed;

    /// <summary>入れる・一覧を読むなど時間のかかる操作 (その間は他の操作を止め、進み具合を出す)。</summary>
    private async Task RunExtAsync(string busy, string failed, Func<IProgress<double>, CancellationToken, Task> work)
    {
        if (_extBusy || _extDemo) return;
        _extBusy = true;
        ExtErrorBar.Visibility = Visibility.Collapsed;
        ExtProgressPanel.Visibility = Visibility.Visible;
        ExtProgressText.Text = busy;
        ExtProgress.IsIndeterminate = true;
        RefreshExtensions();
        var progress = new Progress<double>(v =>
        {
            ExtProgress.IsIndeterminate = false;
            ExtProgress.Value = v;
        });
        try
        {
            await work(progress, CancellationToken.None);
        }
        catch (Exception ex) when (PluginCatalog.IsExpectedFailure(ex))
        {
            PluginLog.Write("error", null, failed + ": " + ex.Message);
            ShowExtError($"{failed}: {ex.Message}");
        }
        finally
        {
            _extBusy = false;
            ExtProgressPanel.Visibility = Visibility.Collapsed;
            RefreshExtensions();
        }
    }

    private async void ExtOnline_Click(object sender, RoutedEventArgs e)
    {
        await RunExtAsync("拡張機能の一覧を読み込んでいます", "拡張機能の一覧を読み込めませんでした", async (_, ct) =>
        {
            await Catalog.RefreshOnlineAsync(PluginCatalog.Http, ct);
            if (Catalog.OnlineError is { } error) ShowExtError(PluginTexts.OnlineError(error), warning: true);
        });
    }

    private async void ExtInstallFile_Click(object sender, RoutedEventArgs e)
    {
        if (_extDemo) return;
        var dialog = new OpenFileDialog { Title = "入れる拡張機能のファイル", Filter = "GetText の拡張機能 (*.gtplugin)|*.gtplugin" };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, PluginTexts.FileInstallWarning + "\n\n" + PluginPermissions.Notice + "\n\n入れますか？",
                "拡張機能を入れる", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunExtAsync("拡張機能を入れています", "拡張機能を入れられませんでした", async (_, ct) => await Catalog.InstallFileAsync(dialog.FileName, ct));
    }

    private async void ExtPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_extDemo || (sender as FrameworkElement)?.DataContext is not ExtensionRow { Item: { Entry: { } entry } item }) return;
        bool update = item.Installed;
        if (!update && MessageBox.Show(this, PluginTexts.ConfirmInstall(item), "拡張機能を入れる", MessageBoxButton.YesNo,
                item.Trust == PluginTrust.Community || !item.Local ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return;
        await RunExtAsync(update ? $"「{item.Name}」を更新しています" : $"「{item.Name}」を入れています",
            update ? $"「{item.Name}」を更新できませんでした" : $"「{item.Name}」を入れられませんでした",
            async (progress, ct) => await Catalog.InstallAsync(entry, PluginCatalog.Http, progress, ct));
    }

    private void ExtToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_extDemo || (sender as FrameworkElement)?.DataContext is not ExtensionRow { Item: var item }) return;
        Catalog.SetEnabled(item.Id, !item.Enabled);
        RefreshExtensions();
    }

    private void ExtRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_extDemo || (sender as FrameworkElement)?.DataContext is not ExtensionRow { Item: var item }) return;
        if (item.PendingRemoval)
        {
            Catalog.CancelUninstall(item.Id);
        }
        else
        {
            if (MessageBox.Show(this, PluginTexts.ConfirmRemove(item), "拡張機能を消す",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            Catalog.Uninstall(item.Id);
        }
        RefreshExtensions();
    }

    private void ExtCopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExtensionRow { Item: var item } || Catalog.Diagnostics(item) is not { } text) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            ShowExtError("クリップボードに入れられませんでした: " + ex.Message, warning: true);
        }
    }

    private void ExtRestart_Click(object sender, RoutedEventArgs e)
    {
        if (_extDemo) return;
        App.Restart();
    }
}

/// <summary>拡張機能の一覧の 1 行の表示 (PluginItem に色・アイコン・ボタンを足したもの。文は PluginTexts)。</summary>
public sealed class ExtensionRow(PluginItem item, bool busy)
{
    public PluginItem Item { get; } = item;
    public string Name => Item.Name;
    public string Description => Item.Description;
    public string Glyph => WindowsIcons.Glyph(PluginTexts.Icon(Item));
    public bool CanAct => !busy;

    public string TrustLabel => Item.TrustLabel;
    public string TrustGlyph => WindowsIcons.Glyph(PluginTexts.TrustIcon(Item));
    public Brush TrustBackground => Theme.Brush(p => PluginTexts.TrustBackground(Item, p));
    public Brush TrustForeground => Theme.Brush(p => PluginTexts.TrustForeground(Item, p));

    public string StateLabel => Item.StateLabel;
    public Visibility StateVisibility => StateLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string StateGlyph => WindowsIcons.Glyph(PluginTexts.StateIcon(Item));
    public Brush StateBackground => Theme.Brush(p => PluginTexts.StateBackground(Item, p));
    public Brush StateForeground => Theme.Brush(p => PluginTexts.StateForeground(Item, p));

    public string Meta => PluginTexts.Meta(Item);
    public string? Detail => Item.Detail;
    public Visibility DetailVisibility => string.IsNullOrEmpty(Item.Detail) ? Visibility.Collapsed : Visibility.Visible;
    public Brush DetailForeground => Theme.Brush(p => PluginTexts.DetailForeground(Item, p));

    public IReadOnlyList<PermissionChip> Permissions => PluginTexts.Chips(Item);
    public Visibility PermissionsVisibility => Item.Permissions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string PrimaryLabel => PluginTexts.PrimaryLabel(Item);
    public Visibility PrimaryVisibility => PluginTexts.HasPrimary(Item) ? Visibility.Visible : Visibility.Collapsed;
    public string ToggleLabel => PluginTexts.ToggleLabel(Item);
    public Visibility ToggleVisibility => Item.Installed && !Item.PendingRemoval ? Visibility.Visible : Visibility.Collapsed;
    public string RemoveLabel => PluginTexts.RemoveLabel(Item);
    public Visibility RemoveVisibility => Item.Installed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DiagnosticsVisibility => Item.Installed ? Visibility.Visible : Visibility.Collapsed;

    public string AccessibleName => PluginTexts.AccessibleName(Item);
    public string DetailsText => PluginTexts.Details(Item);

    // ───────── 拡張機能の設定 (GetText の部品で描く) ─────────

    private sealed record ChoiceItem(string Value, string Label);

    private UIElement? _settingsPanel;

    public Visibility SettingsVisibility => Item.Loaded?.Context?.SettingsPages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public UIElement? SettingsPanel => _settingsPanel ??= Item.Loaded?.Context is { } context ? BuildSettings(context) : null;

    private static UIElement BuildSettings(PluginContext context)
    {
        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var page in context.SettingsPages)
        {
            if (context.SettingsPages.Count > 1)
                root.Children.Add(new TextBlock { Text = page.Title.For("ja"), Style = (Style)Application.Current.FindResource("SettingHeader") });
            foreach (var setting in page.Items.Where(s => !s.Advanced)) root.Children.Add(SettingRow(context.Settings, setting));
            var advanced = page.Items.Where(s => s.Advanced).ToList();
            if (advanced.Count > 0)
            {
                var more = new StackPanel();
                foreach (var setting in advanced) more.Children.Add(SettingRow(context.Settings, setting));
                root.Children.Add(new Expander { Header = "詳細設定", Content = more, Margin = new Thickness(0, 4, 0, 0) });
            }
        }
        return root;
    }

    private static FrameworkElement SettingRow(IPluginSettings settings, PluginSetting s)
    {
        var label = new StackPanel();
        label.Children.Add(new TextBlock { Text = s.Label.For("ja"), Style = (Style)Application.Current.FindResource("SettingLabel") });
        if (s.Description != null)
            label.Children.Add(new TextBlock { Text = s.Description.For("ja"), Style = (Style)Application.Current.FindResource("SettingDescription") });
        string current = settings.Get(s.Key) ?? s.Default ?? "";
        FrameworkElement control;
        switch (s.Kind)
        {
            case PluginSettingKind.Toggle:
                var check = new CheckBox { IsChecked = bool.TryParse(current, out var b) && b, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
                check.Click += (_, _) => settings.Set(s.Key, (check.IsChecked == true).ToString().ToLowerInvariant());
                control = check;
                break;
            case PluginSettingKind.Choice:
                var combo = new ComboBox { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center, DisplayMemberPath = nameof(ChoiceItem.Label) };
                var choices = s.Choices.Select(c => new ChoiceItem(c.Value, c.Label.For("ja"))).ToList();
                combo.ItemsSource = choices;
                combo.SelectedItem = choices.FirstOrDefault(c => c.Value == current);
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ChoiceItem chosen) settings.Set(s.Key, chosen.Value);
                };
                control = combo;
                break;
            case PluginSettingKind.Number:
                var number = new TextBox { Text = current, Width = 120, VerticalAlignment = VerticalAlignment.Center };
                number.LostFocus += (_, _) =>
                {
                    if (double.TryParse(number.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v))
                    {
                        v = Math.Clamp(v, s.Minimum, s.Maximum);
                        number.Text = v.ToString(CultureInfo.CurrentCulture);
                        settings.Set(s.Key, v.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        number.Text = settings.Get(s.Key) ?? s.Default ?? "";
                    }
                };
                control = number;
                break;
            default:
                bool multiline = s.Kind == PluginSettingKind.MultilineText;
                var text = new TextBox
                {
                    Text = current,
                    MinWidth = 240,
                    AcceptsReturn = multiline,
                    TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    Height = multiline ? 80 : double.NaN,
                    VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                text.LostFocus += (_, _) => settings.Set(s.Key, text.Text);
                control = text;
                break;
        }
        System.Windows.Automation.AutomationProperties.SetName(control, s.Label.For("ja"));
        control.Margin = new Thickness(16, 0, 0, 0);
        DockPanel.SetDock(control, Dock.Right);
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(control);
        row.Children.Add(label);
        return row;
    }
}
