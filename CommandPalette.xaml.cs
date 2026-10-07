using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

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
    public string Glyph => Command.Icon.SvgPath == null ? WindowsIcons.Glyph(Command.Icon.Semantic) : "";
    public bool HasGeometry => Command.Icon.SvgPath != null;
    public Geometry? Geometry => Command.Icon.SvgPath is { } d ? System.Windows.Media.Geometry.Parse(d) : null;
}

/// <summary>
/// コマンドの一覧 (Ctrl+K)。機能・操作・設定・最近の議事録を検索して、キーボードだけで実行できる。
/// 今使っている画面に関係するものを上に出す。拡張機能のコマンドも App.Commands に登録すればここに出る。
/// </summary>
public partial class CommandPalette : Window
{
    private readonly CommandRegistry _registry;
    private readonly CommandContext _context;
    private static CommandPalette? _open;

    public CommandPalette(CommandRegistry registry, CommandContext context)
    {
        InitializeComponent();
        _registry = registry;
        _context = context;
        PreviewKeyDown += OnPreviewKeyDown;
        // 一覧の外を押したら閉じる (一度前に出たあとだけ。画面の画像・動作確認のように前に出さないときは閉じない)
        Activated += (_, _) =>
        {
            _wasActive = true;
            QueryBox.Focus();
        };
        Deactivated += (_, _) => { if (_wasActive && !_closing) CloseIfOpen(); };
        Closed += (_, _) =>
        {
            _closed = true;
            _search?.Cancel();
            if (_open == this) _open = null;
        };
        Refresh();
    }

    private bool _wasActive;
    private bool _closed;

    /// <summary>まだ開いていれば閉じる (閉じた窓をもう一度閉じると例外になるため)。</summary>
    internal void CloseIfOpen()
    {
        if (!_closed) Close();
    }

    /// <summary>開く (開いていれば閉じる)。owner の上の方の中央に出す。</summary>
    public static void Toggle(Window? owner, CommandRegistry registry, CommandContext context)
    {
        if (_open != null)
        {
            _open.CloseIfOpen();
            return;
        }
        var palette = new CommandPalette(registry, context);
        if (owner is { IsVisible: true })
        {
            palette.Owner = owner;
            palette.Topmost = owner.Topmost;
            palette.Left = owner.Left + Math.Max(0, (owner.ActualWidth - palette.Width) / 2);
            palette.Top = owner.Top + Math.Min(96, Math.Max(24, owner.ActualHeight * 0.12));
        }
        else
        {
            var area = SystemParameters.WorkArea;
            palette.Left = area.Left + (area.Width - palette.Width) / 2;
            palette.Top = area.Top + area.Height * 0.18;
        }
        _open = palette;
        palette.Show();
        palette.Activate();
        // 画面の外にはみ出さない (モニターの端に寄せた窓から開いたとき)
        palette.Dispatcher.BeginInvoke(() => ScreenUtil.EnsureVisible(palette));
    }

    /// <summary>開いている一覧 (動作確認用)。</summary>
    internal static CommandPalette? Current => _open;

    private CancellationTokenSource? _search;

    private void Refresh()
    {
        var query = QueryBox.Text;
        QueryPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var items = _registry.Search(query, _context).Select(c => new PaletteItem(c)).ToList();
        Show(items, keepSelection: false);
        SearchPluginsAsync(query, items);
    }

    private void Show(List<PaletteItem> items, bool keepSelection)
    {
        int selected = Results.SelectedIndex;
        Results.ItemsSource = items;
        if (items.Count > 0) Results.SelectedIndex = keepSelection && selected >= 0 && selected < items.Count ? selected : 0;
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Results.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
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
            if (cts.IsCancellationRequested || _closed || found.Count == 0) return;
            Show([.. commands, .. found.Select(c => new PaletteItem(c))], keepSelection: true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>検索の文字 (動作確認用)。</summary>
    internal void SetQuery(string text) => QueryBox.Text = text;

    internal IReadOnlyList<PaletteItem> Items => (IReadOnlyList<PaletteItem>?)Results.ItemsSource ?? [];

    private void QueryBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        int count = Results.Items.Count;
        switch (e.Key)
        {
            case Key.Escape:
                CloseIfOpen();
                break;
            case Key.Down when count > 0:
                Results.SelectedIndex = (Results.SelectedIndex + 1) % count;
                Results.ScrollIntoView(Results.SelectedItem);
                break;
            case Key.Up when count > 0:
                Results.SelectedIndex = (Results.SelectedIndex - 1 + count) % count;
                Results.ScrollIntoView(Results.SelectedItem);
                break;
            case Key.PageDown when count > 0:
                Results.SelectedIndex = Math.Min(count - 1, Results.SelectedIndex + 8);
                Results.ScrollIntoView(Results.SelectedItem);
                break;
            case Key.PageUp when count > 0:
                Results.SelectedIndex = Math.Max(0, Results.SelectedIndex - 8);
                Results.ScrollIntoView(Results.SelectedItem);
                break;
            case Key.Enter:
                RunSelected();
                break;
            case Key.K when Keyboard.Modifiers == ModifierKeys.Control:
                CloseIfOpen();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Item_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBoxItem { DataContext: PaletteItem item })
        {
            Results.SelectedItem = item;
            RunSelected();
        }
    }

    private bool _closing;

    private void RunSelected()
    {
        if (Results.SelectedItem is not PaletteItem item) return;
        _closing = true;
        var owner = Owner;
        CloseIfOpen();
        // 一覧を閉じて元の窓に戻ってから実行する (実行した操作が開く窓・ダイアログが、一覧の後ろに隠れないように)
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            owner?.Activate();
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
