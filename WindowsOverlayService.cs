using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using GetText.Plugins;
using static GetText.NativeMethods;

namespace GetText;

/// <summary>
/// 画面の上に文字を重ねて出す (Windows)。拡張機能 (訳を重ねる など) のための GetText の部品。
/// 窓はクリックを下に通し (WS_EX_TRANSPARENT)、画面の取り込みに写らない (読み取りが自分の訳を読まないように)。
/// </summary>
public sealed class WindowsOverlayService(Func<bool> canShow) : IOverlayService
{
    private readonly Dictionary<string, OverlayWindow> _windows = [];

    public void Show(string owner, IReadOnlyList<OverlayLabel> labels) => Application.Current.Dispatcher.BeginInvoke(() =>
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

    public void Clear(string owner) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        if (_windows.Remove(owner, out var window)) window.CloseQuietly();
    });

    /// <summary>読み取りの画面を閉じたとき: すべて消す。</summary>
    public void ClearAll() => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        foreach (var w in _windows.Values) w.CloseQuietly();
        _windows.Clear();
    });

    private sealed class OverlayWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x20, WsExLayered = 0x80000, WsExToolWindow = 0x80, WsExNoActivate = 0x8000000;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        private readonly Canvas _canvas = new();
        public bool WasClosed { get; private set; }

        public OverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            IsHitTestVisible = false;
            Content = _canvas;
            SetResourceReference(FontFamilyProperty, "Gt.Font.Ui");
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate);
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); // 読み取りに写らない
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
            if (!IsVisible)
            {
                Width = 1;
                Height = 1;
                Show();
            }
            // 物理ピクセルの位置に置き、その画面の拡大率で DIP に直して描く
            ScreenUtil.MoveTo(this, new Point(left, top));
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Width = Math.Max(1, (right - left) / scale);
            Height = Math.Max(1, (bottom - top) / scale);
            _canvas.Children.Clear();
            foreach (var label in labels)
            {
                double w = Math.Max(8, (label.Right - label.Left) / scale), h = Math.Max(8, (label.Bottom - label.Top) / scale);
                var text = new TextBlock { Text = label.Text, FontSize = Math.Clamp(h * 0.72, 9, 48), TextWrapping = TextWrapping.NoWrap };
                text.SetResourceReference(TextBlock.ForegroundProperty, "Gt.TextPrimary");
                var card = new Border
                {
                    Width = w,
                    Height = h,
                    Padding = new Thickness(2, 0, 2, 0),
                    Opacity = 0.96,
                    // 長い訳は四角に収まるまで小さくする (はみ出して下の文字を隠さない)
                    Child = new Viewbox { Child = text, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left },
                };
                card.SetResourceReference(Border.BackgroundProperty, "Gt.Surface");
                card.SetResourceReference(Border.CornerRadiusProperty, "Gt.Radius.Small");
                Canvas.SetLeft(card, (label.Left - left) / scale);
                Canvas.SetTop(card, (label.Top - top) / scale);
                _canvas.Children.Add(card);
            }
        }
    }
}
