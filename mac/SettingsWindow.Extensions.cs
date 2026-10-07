using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GetText.Plugins;

namespace GetText;

/// <summary>設定 → 拡張機能 (入れたもの・見つける・更新)。一覧の中身と文は PluginCatalog・PluginTexts (Windows 版と共有)。</summary>
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
        _extTab = tab;
        (tab switch { "Discover" => ExtTabDiscover, "Updates" => ExtTabUpdates, _ => ExtTabInstalled }).IsChecked = true;
        ExtErrorBar.IsVisible = false;
        ExtProgressPanel.IsVisible = false;
        RefreshExtensions();
        if (error != null) ShowExtError(error, warning: true);
        if (progress != null)
        {
            ExtProgressPanel.IsVisible = true;
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

        var query = ExtSearch.Text ?? "";
        var items = _extTab switch { "Discover" => catalog.Available(), "Updates" => updates, _ => installed };
        var shown = PluginCatalog.Filter(items, query);
        ExtList.ItemsSource = shown.Select(i => new MacExtensionRow(i, _extBusy)).ToList(); // (見本では押しても何もしない)
        ExtEmpty.IsVisible = shown.Count == 0;
        (ExtEmptyTitle.Text, ExtEmptyDetail.Text) = PluginTexts.Empty(_extTab, catalog, query.Length > 0, items.Count);

        ExtOnlineButton.Content = _extTab == "Updates" ? "更新を確認" : "オンラインで探す";
        ExtOnlineButton.IsEnabled = !_extBusy;
        ExtSourceText.Text = PluginTexts.Source(catalog, PluginRuntime.SafeMode);

        bool restart = installed.Any(i => i.State == PluginItemState.RestartRequired);
        ExtRestartBar.IsVisible = restart;
        ExtensionsBadge.IsVisible = restart || updates.Count > 0;
        ExtensionsBadgeText.Text = restart ? "再起動" : $"更新 {updates.Count}";
        ExtNotice.Text = PluginPermissions.Notice;
    }

    private void ExtTab_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true, Tag: string tab }) return;
        _extTab = tab;
        if (!_loading) RefreshExtensions();
    }

    private void ExtSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_loading) RefreshExtensions();
    }

    private void ShowExtError(string text, bool warning = false)
    {
        ExtErrorText.Text = text;
        ExtErrorIcon.Icon = warning ? AppIcon.Warning : AppIcon.Error;
        ExtErrorIcon.Foreground = MacTheme.BrushOf(p => warning ? p.Warning : p.Critical);
        ExtErrorBar.Background = MacTheme.BrushOf(p => warning ? p.WarningSubtle : p.CriticalSubtle);
        ExtErrorBar.IsVisible = true;
    }

    private void ExtErrorClose_Click(object? sender, RoutedEventArgs e) => ExtErrorBar.IsVisible = false;

    /// <summary>入れる・一覧を読むなど時間のかかる操作 (その間は他の操作を止め、進み具合を出す)。</summary>
    private async Task RunExtAsync(string busy, string failed, Func<IProgress<double>, CancellationToken, Task> work)
    {
        if (_extBusy || _extDemo) return;
        _extBusy = true;
        ExtErrorBar.IsVisible = false;
        ExtProgressPanel.IsVisible = true;
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
            ExtProgressPanel.IsVisible = false;
            RefreshExtensions();
        }
    }

    private async void ExtOnline_Click(object? sender, RoutedEventArgs e)
    {
        await RunExtAsync("拡張機能の一覧を読み込んでいます", "拡張機能の一覧を読み込めませんでした", async (_, ct) =>
        {
            await Catalog.RefreshOnlineAsync(PluginCatalog.Http, ct);
            if (Catalog.OnlineError is { } error) ShowExtError(PluginTexts.OnlineError(error), warning: true);
        });
    }

    private async void ExtInstallFile_Click(object? sender, RoutedEventArgs e)
    {
        if (_extDemo) return;
        var path = await Dialogs.OpenFileAsync(this, "入れる拡張機能のファイル", [new Dialogs.FileKind("GetText の拡張機能", "gtplugin")]);
        if (path == null) return;
        if (!await Dialogs.ConfirmAsync(this, PluginTexts.FileInstallWarning + "\n\n" + PluginPermissions.Notice, "拡張機能を入れる", "入れる")) return;
        await RunExtAsync("拡張機能を入れています", "拡張機能を入れられませんでした", async (_, ct) => await Catalog.InstallFileAsync(path, ct));
    }

    private async void ExtPrimary_Click(object? sender, RoutedEventArgs e)
    {
        if (_extDemo || (sender as Control)?.DataContext is not MacExtensionRow { Item: { Entry: { } entry } item }) return;
        bool update = item.Installed;
        if (!update && !await Dialogs.ConfirmAsync(this, PluginTexts.ConfirmInstall(item), "拡張機能を入れる", "入れる")) return;
        await RunExtAsync(update ? $"「{item.Name}」を更新しています" : $"「{item.Name}」を入れています",
            update ? $"「{item.Name}」を更新できませんでした" : $"「{item.Name}」を入れられませんでした",
            async (progress, ct) => await Catalog.InstallAsync(entry, PluginCatalog.Http, progress, ct));
    }

    private void ExtToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (_extDemo || (sender as Control)?.DataContext is not MacExtensionRow { Item: var item }) return;
        Catalog.SetEnabled(item.Id, !item.Enabled);
        RefreshExtensions();
    }

    private async void ExtRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (_extDemo || (sender as Control)?.DataContext is not MacExtensionRow { Item: var item }) return;
        if (item.PendingRemoval)
        {
            Catalog.CancelUninstall(item.Id);
        }
        else
        {
            if (!await Dialogs.ConfirmAsync(this, PluginTexts.ConfirmRemove(item), "拡張機能を消す", "消す")) return;
            Catalog.Uninstall(item.Id);
        }
        RefreshExtensions();
    }

    private async void ExtCopyDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not MacExtensionRow { Item: var item } || Catalog.Diagnostics(item) is not { } text) return;
        if (!await Dialogs.CopyAsync(this, text)) ShowExtError("クリップボードに入れられませんでした", warning: true);
    }

    private void ExtRestart_Click(object? sender, RoutedEventArgs e)
    {
        if (_extDemo) return;
        App.Restart();
    }
}

/// <summary>拡張機能の一覧の 1 行の表示 (PluginItem に色・アイコン・ボタンを足したもの。文は PluginTexts)。</summary>
public sealed class MacExtensionRow(PluginItem item, bool busy)
{
    public PluginItem Item { get; } = item;
    public string Name => Item.Name;
    public string Description => Item.Description;
    public AppIcon Icon => PluginTexts.Icon(Item);
    public string? Svg => Item.Icon.SvgPath;
    public bool CanAct => !busy;
    public bool Installed => Item.Installed;

    public string TrustLabel => Item.TrustLabel;
    public AppIcon TrustIcon => PluginTexts.TrustIcon(Item);
    public IBrush TrustBackground => MacTheme.BrushOf(p => PluginTexts.TrustBackground(Item, p));
    public IBrush TrustForeground => MacTheme.BrushOf(p => PluginTexts.TrustForeground(Item, p));

    public string StateLabel => Item.StateLabel;
    public bool HasState => StateLabel.Length > 0;
    public AppIcon StateIcon => PluginTexts.StateIcon(Item);
    public IBrush StateBackground => MacTheme.BrushOf(p => PluginTexts.StateBackground(Item, p));
    public IBrush StateForeground => MacTheme.BrushOf(p => PluginTexts.StateForeground(Item, p));

    public string Meta => PluginTexts.Meta(Item);
    public string? Detail => Item.Detail;
    public bool HasDetail => !string.IsNullOrEmpty(Item.Detail);
    public IBrush DetailForeground => MacTheme.BrushOf(p => PluginTexts.DetailForeground(Item, p));

    public IReadOnlyList<PermissionChip> Permissions => PluginTexts.Chips(Item);
    public bool HasPermissions => Item.Permissions.Count > 0;

    public string PrimaryLabel => PluginTexts.PrimaryLabel(Item);
    public bool HasPrimary => PluginTexts.HasPrimary(Item);
    public string ToggleLabel => PluginTexts.ToggleLabel(Item);
    public bool HasToggle => Item.Installed && !Item.PendingRemoval;
    public string RemoveLabel => PluginTexts.RemoveLabel(Item);
    public bool HasRemove => Item.Installed;

    public string AccessibleName => PluginTexts.AccessibleName(Item);
    public string DetailsText => PluginTexts.Details(Item);

    // ───────── 拡張機能の設定 (GetText の部品で描く) ─────────

    private sealed record ChoiceItem(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private Control? _settingsPanel;

    public bool HasSettings => Item.Loaded?.Context?.SettingsPages.Count > 0;

    public Control? SettingsPanel => _settingsPanel ??= Item.Loaded?.Context is { } context ? BuildSettings(context) : null;

    private static Control BuildSettings(PluginContext context)
    {
        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var page in context.SettingsPages)
        {
            if (context.SettingsPages.Count > 1)
                root.Children.Add(new TextBlock { Text = page.Title.For("ja"), Classes = { "settingHeader" } });
            foreach (var setting in page.Items.Where(s => !s.Advanced)) root.Children.Add(SettingRow(context.Settings, setting));
            var advanced = page.Items.Where(s => s.Advanced).ToList();
            if (advanced.Count > 0)
            {
                var more = new StackPanel();
                foreach (var setting in advanced) more.Children.Add(SettingRow(context.Settings, setting));
                root.Children.Add(new Expander { Header = "詳細設定", Content = more, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch });
            }
        }
        return root;
    }

    private static Control SettingRow(IPluginSettings settings, PluginSetting s)
    {
        var label = new StackPanel();
        label.Children.Add(new TextBlock { Text = s.Label.For("ja"), Classes = { "label" } });
        if (s.Description != null) label.Children.Add(new TextBlock { Text = s.Description.For("ja"), Classes = { "desc" } });
        string current = settings.Get(s.Key) ?? s.Default ?? "";
        Control control;
        switch (s.Kind)
        {
            case PluginSettingKind.Toggle:
                var toggle = new ToggleSwitch { IsChecked = bool.TryParse(current, out var b) && b, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
                toggle.IsCheckedChanged += (_, _) => settings.Set(s.Key, (toggle.IsChecked == true).ToString().ToLowerInvariant());
                control = toggle;
                break;
            case PluginSettingKind.Choice:
                var choices = s.Choices.Select(c => new ChoiceItem(c.Value, c.Label.For("ja"))).ToList();
                var combo = new ComboBox { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center, ItemsSource = choices };
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
                    VerticalAlignment = VerticalAlignment.Center,
                };
                text.LostFocus += (_, _) => settings.Set(s.Key, text.Text);
                control = text;
                break;
        }
        Avalonia.Automation.AutomationProperties.SetName(control, s.Label.For("ja"));
        control.Margin = new Thickness(16, 0, 0, 0);
        DockPanel.SetDock(control, Dock.Right);
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(control);
        row.Children.Add(label);
        return row;
    }
}
