using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static GetText.NativeMethods;

namespace GetText;

public partial class CaptureWindow : Window
{
    private static readonly Color NormalColor = Color.FromRgb(0x2B, 0x6C, 0xB0);
    private static readonly Color PausedColor = Color.FromRgb(0xB4, 0x53, 0x09); // 白い文字が読める濃さ (コントラスト 5:1)

    public event EventHandler? PauseRequested;
    public event EventHandler? CloseRequested;

    /// <summary>アプリを閉じるときだけ true にする (枠だけを Alt+F4 などで閉じると、読み取りが止まったままになるため)。</summary>
    public bool AllowClose { get; set; }

    private readonly AppSettings _settings;

    public CaptureWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Closing += (_, e) =>
        {
            if (AllowClose) return;
            e.Cancel = true; // 枠だけは閉じず、× ボタンと同じくアプリを閉じる
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };

        Width = settings.CaptureWidth;
        Height = settings.CaptureHeight;
        if (settings.CaptureLeft is double l && settings.CaptureTop is double t)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Max(0, SystemParameters.WorkArea.Width / 2 - Width - 10);
            Top = Math.Max(0, SystemParameters.WorkArea.Height / 2 - Height / 2);
        }

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => UpdateSizeText();
        Loaded += (_, _) => UpdateSizeText();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // 枠そのものがキャプチャ画像に写り込まないようにする (Windows 10 2004 以降)
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        // 拡大率の違うモニターでも前回と同じ場所に戻す (物理ピクセルで保存してある場合)
        ScreenUtil.RestorePixelPosition(this, _settings.CapturePixel);
        ScreenUtil.EnsureVisible(this);
    }

    /// <summary>枠の内側(透明部分)の画面座標 (物理ピクセル)。</summary>
    public Int32Rect GetCaptureRectPixels()
    {
        if (!CaptureArea.IsLoaded || CaptureArea.ActualWidth < 1 || CaptureArea.ActualHeight < 1)
            return Int32Rect.Empty;

        var tl = CaptureArea.PointToScreen(new Point(0, 0));
        var br = CaptureArea.PointToScreen(new Point(CaptureArea.ActualWidth, CaptureArea.ActualHeight));
        int x = (int)Math.Round(tl.X), y = (int)Math.Round(tl.Y);
        return new Int32Rect(x, y, (int)Math.Round(br.X) - x, (int)Math.Round(br.Y) - y);
    }

    public void SetPaused(bool paused)
    {
        var color = paused ? PausedColor : NormalColor;
        Resources["AccentBrush"] = new SolidColorBrush(color);
        OuterFrame.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B));
        PauseButton.Content = paused ? "" : ""; // Play / Pause
        PauseButton.ToolTip = paused ? "再開" : "一時停止";
        TitleText.Text = paused ? "GetText (停止中)" : "GetText";
    }

    private System.Windows.Threading.DispatcherTimer? _flashTimer;

    /// <summary>枠の上のバーに、短い知らせ (コピーしました など) を 1.5 秒出す。</summary>
    public void Flash(string text)
    {
        SizeText.Text = text;
        SizeText.FontWeight = FontWeights.SemiBold;
        _flashTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _flashTimer.Stop();
        _flashTimer.Tick -= OnFlashEnd;
        _flashTimer.Tick += OnFlashEnd;
        _flashTimer.Start();
    }

    private void OnFlashEnd(object? sender, EventArgs e)
    {
        _flashTimer?.Stop();
        SizeText.FontWeight = FontWeights.Normal;
        UpdateSizeText();
    }

    private void UpdateSizeText()
    {
        if (_flashTimer is { IsEnabled: true }) return;
        var r = GetCaptureRectPixels();
        SizeText.Text = r.IsEmpty ? "" : $"{r.Width} × {r.Height}";
    }

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => PauseRequested?.Invoke(this, EventArgs.Empty);

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    // 縁・四隅のつまみのドラッグでサイズを変える。Tag は動かす辺 (L/R/T/B の組み合わせ)
    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var edges = (string)((FrameworkElement)sender).Tag;
        double left = Left, top = Top, width = ActualWidth, height = ActualHeight;

        if (edges.Contains('L'))
        {
            double w = Math.Max(MinWidth, width - e.HorizontalChange);
            left += width - w;
            width = w;
        }
        if (edges.Contains('R')) width = Math.Max(MinWidth, width + e.HorizontalChange);
        if (edges.Contains('T'))
        {
            double h = Math.Max(MinHeight, height - e.VerticalChange);
            top += height - h;
            height = h;
        }
        if (edges.Contains('B')) height = Math.Max(MinHeight, height + e.VerticalChange);

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }
}
