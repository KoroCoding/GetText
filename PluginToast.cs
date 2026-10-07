using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GetText.Plugins;

namespace GetText;

/// <summary>
/// 画面の右下に出す GetText の知らせ (拡張機能の知らせ・進み具合)。色・字・角はデザイントークン、アイコンは AppIcon。
/// 前に出ても、作業中の窓からフォーカスを奪わない。知らせは 3 つまで重ねる (古いものから消す)。
/// </summary>
public sealed class PluginToast : Window
{
    private static readonly List<PluginToast> Open = [];
    private const int MaxOpen = 3;

    private readonly TextBlock _icon = new() { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Margin = new Thickness(0, 2, 12, 0) };
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _source = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 4, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _action = new() { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _timer = new();
    private Action? _onAction;
    private bool _closed;

    private PluginToast()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        SetResourceReference(FontFamilyProperty, "Gt.Font.Ui");

        var close = new Button
        {
            Content = WindowsIcons.Glyph(AppIcon.Close),
            FontFamily = _icon.FontFamily,
            FontSize = 10,
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = "閉じる",
        };
        System.Windows.Automation.AutomationProperties.SetName(close, "知らせを閉じる");
        close.SetResourceReference(ForegroundProperty, "Gt.TextSecondary");
        close.Click += (_, _) => Close();
        _action.Click += (_, _) =>
        {
            var action = _onAction;
            Close();
            try { action?.Invoke(); } catch (Exception ex) { App.Log("PluginToastAction", ex); }
        };

        _title.SetResourceReference(TextBlock.FontSizeProperty, "Gt.Font.BodyStrong");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Gt.TextPrimary");
        _message.SetResourceReference(TextBlock.FontSizeProperty, "Gt.Font.Body");
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Gt.TextSecondary");
        _source.SetResourceReference(TextBlock.FontSizeProperty, "Gt.Font.Caption");
        _source.SetResourceReference(TextBlock.ForegroundProperty, "Gt.TextTertiary");
        _progress.SetResourceReference(ForegroundProperty, "Gt.Accent");

        var text = new StackPanel();
        text.Children.Add(_title);
        text.Children.Add(_message);
        text.Children.Add(_progress);
        text.Children.Add(_source);
        text.Children.Add(_action);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(close, 2);
        grid.Children.Add(_icon);
        grid.Children.Add(text);
        grid.Children.Add(close);

        var card = new Border { Padding = new Thickness(16, 12, 8, 14), BorderThickness = new Thickness(1), Child = grid, Margin = new Thickness(8) };
        card.SetResourceReference(Border.BackgroundProperty, "Gt.Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Gt.Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "Gt.Radius.Dialog");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18 };
        Content = card;

        _timer.Tick += (_, _) => Close();
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            Open.Remove(this);
            Arrange();
        };
        SizeChanged += (_, _) => Arrange();
    }

    /// <summary>知らせを出す (UI のスレッドで呼ぶ)。</summary>
    public static void Show(string source, PluginNotification n)
    {
        var toast = Create();
        toast.Set(n.Title.For("ja"), n.Message?.For("ja"), n.Kind);
        toast._source.Text = source;
        if (n.ActionLabel != null && n.Action != null)
        {
            toast._action.Content = n.ActionLabel.For("ja");
            toast._onAction = n.Action;
            toast._action.Visibility = Visibility.Visible;
        }
        // 操作のボタンがある・問題の知らせは長めに出す
        toast.CloseAfter(TimeSpan.FromSeconds(n.Kind is PluginNotificationKind.Error or PluginNotificationKind.Warning || n.Action != null ? 15 : 6));
        if (n.PlaySound) (n.Kind == PluginNotificationKind.Error ? SystemSounds.Hand : SystemSounds.Asterisk).Play();
        toast.Display();
    }

    /// <summary>進み具合を出す (Complete で結果に変わり、少しして消える)。</summary>
    public static IPluginProgress Progress(string source, string title)
    {
        var toast = Create();
        toast.Set(title, null, PluginNotificationKind.Info);
        toast._source.Text = source;
        toast._progress.Visibility = Visibility.Visible;
        toast._progress.IsIndeterminate = true;
        var handle = new ProgressHandle(toast);
        toast._action.Content = "中止";
        toast._onAction = handle.Cancel;
        toast._action.Visibility = Visibility.Visible;
        toast.Display();
        return handle;
    }

    private static PluginToast Create()
    {
        while (Open.Count >= MaxOpen) Open[0].Close();
        var toast = new PluginToast();
        Open.Add(toast);
        return toast;
    }

    private void Set(string title, string? message, PluginNotificationKind kind)
    {
        _title.Text = title;
        _message.Text = message ?? "";
        _message.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        var (icon, color) = kind switch
        {
            PluginNotificationKind.Success => (AppIcon.Success, "Gt.Success"),
            PluginNotificationKind.Warning => (AppIcon.Warning, "Gt.Warning"),
            PluginNotificationKind.Error => (AppIcon.Error, "Gt.Critical"),
            _ => (AppIcon.Info, "Gt.AccentText"),
        };
        _icon.Text = WindowsIcons.Glyph(icon);
        _icon.SetResourceReference(TextBlock.ForegroundProperty, color);
        System.Windows.Automation.AutomationProperties.SetName(this, string.IsNullOrEmpty(message) ? title : $"{title}: {message}");
    }

    private void CloseAfter(TimeSpan time)
    {
        if (_closed) return; // (閉じた知らせのタイマーを動かさない)
        _timer.Stop();
        _timer.Interval = time;
        _timer.Start();
    }

    private void Display()
    {
        Show();
        Arrange();
    }

    /// <summary>右下から上へ積む。</summary>
    private static void Arrange()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom - 8;
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var t = Open[i];
            var height = t.ActualHeight > 0 ? t.ActualHeight : 120;
            t.Left = area.Right - t.Width - 8;
            t.Top = bottom - height;
            bottom = t.Top;
        }
    }

    private sealed class ProgressHandle(PluginToast toast) : IPluginProgress
    {
        private readonly CancellationTokenSource _cancel = new();
        private bool _done;

        public CancellationToken CancellationToken => _cancel.Token;

        public void Cancel() => _cancel.Cancel();

        public void Report(double? value, LocalizedText? detail = null) => toast.Dispatcher.BeginInvoke(() =>
        {
            if (_done || toast._closed) return;
            toast._progress.IsIndeterminate = value == null;
            if (value is { } v) toast._progress.Value = Math.Clamp(v, 0, 1) * 100;
            if (detail != null)
            {
                toast._message.Text = detail.For("ja");
                toast._message.Visibility = Visibility.Visible;
            }
        });

        public void Complete(LocalizedText? message = null, bool failed = false) => toast.Dispatcher.BeginInvoke(() =>
        {
            if (_done || toast._closed) return;
            _done = true;
            toast._progress.Visibility = Visibility.Collapsed;
            toast._action.Visibility = Visibility.Collapsed;
            toast.Set(toast._title.Text, message?.For("ja") ?? (failed ? "できませんでした" : "終わりました"),
                failed ? PluginNotificationKind.Error : PluginNotificationKind.Success);
            toast.CloseAfter(TimeSpan.FromSeconds(failed ? 15 : 4));
        });

        public void Dispose()
        {
            if (!_done) Complete();
        }
    }
}
