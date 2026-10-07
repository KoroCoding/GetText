using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace GetText;

/// <summary>コマンドの一覧の 1 行 (表示用)。</summary>
public sealed class PaletteItem(AppCommand command)
{
    public AppCommand Command { get; } = command;
    public string Title => Command.Title;
    public string Subtitle => Command.Subtitle ?? "";
    public string Shortcut => Command.Shortcut ?? "";
    public bool HasShortcut => !string.IsNullOrEmpty(Command.Shortcut);
    public string Category => CommandSearch.CategoryName(Command.Category);
    public AppIcon Icon => Command.Icon.Semantic;
    public string? Svg => Command.Icon.SvgPath;
}

/// <summary>
/// コマンドの一覧 (⌘K)。機能・操作・設定・最近の議事録を検索して、キーボードだけで実行できる (Mac 版)。
/// 今使っている画面に関係するものを上に出す。
/// </summary>
public partial class CommandPalette : Window
{
    private readonly CommandRegistry _registry;
    private readonly CommandContext _context;
    private static CommandPalette? _open;
    private bool _closing;

    public CommandPalette() : this(new CommandRegistry(), CommandContext.Any) { }

    public CommandPalette(CommandRegistry registry, CommandContext context)
    {
        InitializeComponent();
        _registry = registry;
        _context = context;
        QueryBox.TextChanged += (_, _) => Refresh();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Results.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && Results.SelectedItem != null) RunSelected();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        Deactivated += (_, _) => { if (!_closing && IsVisible && !App.DemoMode) Close(); };
        Closed += (_, _) =>
        {
            _search?.Cancel();
            if (_open == this) _open = null;
        };
        Opened += (_, _) => QueryBox.Focus();
        Refresh();
    }

    public static void Toggle(Window? owner, CommandRegistry registry, CommandContext context)
    {
        if (_open != null)
        {
            _open.Close();
            return;
        }
        var palette = new CommandPalette(registry, context);
        if (owner is { IsVisible: true })
        {
            double scale = owner.DesktopScaling > 0 ? owner.DesktopScaling : 1;
            var left = owner.Position.X + Math.Max(0, (owner.Bounds.Width - palette.Width) / 2) * scale;
            var top = owner.Position.Y + Math.Min(96, Math.Max(24, owner.Bounds.Height * 0.12)) * scale;
            palette.Position = new PixelPoint((int)left, (int)top);
            palette.Topmost = owner.Topmost;
        }
        else palette.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open = palette;
        palette.Show();
        palette.Activate();
    }

    internal static CommandPalette? Current => _open;

    private CancellationTokenSource? _search;

    private void Refresh()
    {
        var query = QueryBox.Text ?? "";
        var items = _registry.Search(query, _context).Select(c => new PaletteItem(c)).ToList();
        Show(items, keepSelection: false);
        SearchPluginsAsync(query, items);
    }

    private void Show(List<PaletteItem> items, bool keepSelection)
    {
        int selected = Results.SelectedIndex;
        Results.ItemsSource = items;
        if (items.Count > 0) Results.SelectedIndex = keepSelection && selected >= 0 && selected < items.Count ? selected : 0;
        EmptyState.IsVisible = items.Count == 0;
        Results.IsVisible = items.Count > 0;
        CountText.Text = items.Count == 0 ? "" : $"{items.Count} 件";
    }

    /// <summary>拡張機能の検索 (読み取りの履歴など) の結果を、少し待ってからコマンドの下に足す (打っている間は探さない)。</summary>
    private async void SearchPluginsAsync(string query, List<PaletteItem> commands)
    {
        _search?.Cancel();
        if (query.Trim().Length < PluginSearch.MinQueryLength || !PluginSearch.HasProviders) return;
        var cts = _search = new CancellationTokenSource();
        try
        {
            await Task.Delay(200, cts.Token);
            var found = await PluginSearch.SearchAsync(query, 20, cts.Token, copied: PluginSearch.CopyAndNotify);
            if (cts.IsCancellationRequested || !IsVisible || found.Count == 0) return;
            Show([.. commands, .. found.Select(c => new PaletteItem(c))], keepSelection: true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal void SetQuery(string text) => QueryBox.Text = text;

    internal IReadOnlyList<PaletteItem> Items => (IReadOnlyList<PaletteItem>?)Results.ItemsSource ?? [];

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        int count = Results.ItemCount;
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Down when count > 0:
                Results.SelectedIndex = (Results.SelectedIndex + 1) % count;
                Results.ScrollIntoView(Results.SelectedIndex);
                break;
            case Key.Up when count > 0:
                Results.SelectedIndex = (Results.SelectedIndex - 1 + count) % count;
                Results.ScrollIntoView(Results.SelectedIndex);
                break;
            case Key.Enter:
                RunSelected();
                break;
            default:
                if (AppCommands.HandleKey(this, e, _context)) return; // (⌘K でもう一度押すと閉じる)
                return;
        }
        e.Handled = true;
    }

    private void RunSelected()
    {
        if (Results.SelectedItem is not PaletteItem item) return;
        _closing = true;
        Close();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // 拡張機能の検索で見つかったもの (登録していないコマンド) は、そのまま行う
                if (item.Command.Category == CommandCategory.SearchResult) item.Command.Execute();
                else if (!_registry.TryRun(item.Command.Id)) App.Log("Command", new InvalidOperationException("今は使えないコマンドです: " + item.Command.Id));
            }
            catch (Exception ex)
            {
                App.Log("Command", ex);
            }
        });
    }
}
