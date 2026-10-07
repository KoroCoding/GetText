using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GetText.Plugins;

namespace GetText;

/// <summary>
/// 画面の上に文字を重ねて出す (Mac)。拡張機能 (訳を重ねる など) のための GetText の部品。
/// 窓はクリックを下に通す (ignoresMouseEvents)。GetText の窓は読み取りの取り込みに写らない (補助プログラムが除く)。
/// </summary>
public sealed class MacOverlayService(Func<bool> canShow) : IOverlayService
{
    private readonly Dictionary<string, OverlayWindow> _windows = [];

    public void Show(string owner, IReadOnlyList<OverlayLabel> labels) => Dispatcher.UIThread.Post(() =>
    {
        // 読み取りの画面を隠した・閉じた後に届いた分 (訳し終えるのが遅れたなど) は出さない
        if (labels.Count == 0 || !canShow())
        {
            Clear(owner);
            return;
        }
        if (!_windows.TryGetValue(owner, out var window) || window.WasClosed)
            _windows[owner] = window = new OverlayWindow();
        window.SetLabels(labels);
    });

    public void Clear(string owner) => Dispatcher.UIThread.Post(() =>
    {
        if (_windows.Remove(owner, out var window)) window.CloseQuietly();
    });

    /// <summary>読み取りの画面を閉じたとき: すべて消す。</summary>
    public void ClearAll() => Dispatcher.UIThread.Post(() =>
    {
        foreach (var w in _windows.Values) w.CloseQuietly();
        _windows.Clear();
    });

    private sealed class OverlayWindow : Window
    {
        private readonly Canvas _canvas = new();
        public bool WasClosed { get; private set; }

        public OverlayWindow()
        {
            SystemDecorations = SystemDecorations.None;
            CanResize = false;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Background = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            IsHitTestVisible = false;
            Content = _canvas;
            Opened += (_, _) =>
            {
                MacNative.SetIgnoresMouseEvents(this, true);
                MacNative.ShowOnAllSpaces(this);
            };
            Closed += (_, _) => WasClosed = true;
        }

        public void CloseQuietly()
        {
            if (!WasClosed) Close();
        }

        public void SetLabels(IReadOnlyList<OverlayLabel> labels)
        {
            if (WasClosed) return;
            double left = labels.Min(l => l.Left), top = labels.Min(l => l.Top);
            double right = labels.Max(l => l.Right), bottom = labels.Max(l => l.Bottom);
            if (!IsVisible) Show();
            double scale = DesktopScaling > 0 ? DesktopScaling : 1;
            Position = new PixelPoint((int)left, (int)top);
            Width = Math.Max(1, (right - left) / scale);
            Height = Math.Max(1, (bottom - top) / scale);
            _canvas.Children.Clear();
            foreach (var label in labels)
            {
                double w = Math.Max(8, (label.Right - label.Left) / scale), h = Math.Max(8, (label.Bottom - label.Top) / scale);
                var text = new TextBlock { Text = label.Text, FontSize = Math.Clamp(h * 0.72, 9, 48), TextWrapping = TextWrapping.NoWrap };
                text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("Gt.TextPrimary"));
                var card = new Border
                {
                    Width = w,
                    Height = h,
                    Padding = new Thickness(2, 0, 2, 0),
                    Opacity = 0.96,
                    CornerRadius = new CornerRadius(DesignTokens.MacRadii.Small),
                    // 長い訳は四角に収まるまで小さくする (はみ出して下の文字を隠さない)
                    Child = new Viewbox { Child = text, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left },
                };
                card.Bind(Border.BackgroundProperty, card.GetResourceObservable("Gt.Surface"));
                Canvas.SetLeft(card, (label.Left - left) / scale);
                Canvas.SetTop(card, (label.Top - top) / scale);
                _canvas.Children.Add(card);
            }
        }
    }
}
