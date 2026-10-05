using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace GetText;

/// <summary>
/// 読み取り枠 (Mac 版)。上のバーで移動、右下の角・右と下の縁で大きさを変える。中は透明で、クリックは下の窓に通る
/// (マウスがバーや縁の上にあるときだけ枠がクリックを受け取る)。
/// </summary>
public partial class CaptureWindow : Window
{
    private static readonly Color NormalColor = Color.FromRgb(0x2B, 0x6C, 0xB0);
    private static readonly Color PausedColor = Color.FromRgb(0xD9, 0x77, 0x06);

    /// <summary>枠の内側 (読み取る所) までの幅 (ポイント): 左・上・右・下。</summary>
    public static readonly double[] Inset = [5, 24, 5, 5];

    public event EventHandler? PauseRequested;
    public event EventHandler? CloseRequested;
    /// <summary>移動・大きさの変更が終わった (窓の位置を調べ直したあと)。</summary>
    public event EventHandler? Moved;

    /// <summary>アプリを閉じるときだけ true にする。</summary>
    public bool AllowClose { get; set; }

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private Rect? _boundsPoints; // 補助プログラムで調べた窓の位置 (画面全体のポイント座標)
    private bool _ignoring;
    private Point? _resizeStart;
    private Size _resizeFrom;
    private string _resizeEdge = "";

    public CaptureWindow() : this(new AppSettings()) { }

    public CaptureWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Width = Math.Max(MinWidth, settings.CaptureWidth);
        Height = Math.Max(MinHeight, settings.CaptureHeight);
        if (settings.CapturePixel is [var x, var y])
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        Closing += (_, e) =>
        {
            if (AllowClose) return;
            e.Cancel = true; // 枠だけは閉じず、× ボタンと同じくアプリを閉じる
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };
        Opened += (_, _) =>
        {
            WindowPlacement.EnsureVisible(this);
            MacNative.ShowOnAllSpaces(this);
            UpdateSizeText();
            _ = RefreshBoundsAsync();
            if (OperatingSystem.IsMacOS()) _hover.Start();
        };
        Closed += (_, _) => _hover.Stop();
        PositionChanged += (_, _) => _ = RefreshBoundsAsync();
        SizeChanged += (_, _) =>
        {
            UpdateSizeText();
            _ = RefreshBoundsAsync();
        };
        _hover.Tick += (_, _) => UpdateClickThrough();
    }

    /// <summary>窓の番号 (補助プログラムが枠の位置を調べるのに使う)。</summary>
    public long WindowNumber => MacNative.WindowNumber(this);

    /// <summary>読み取る範囲の大きさ (ポイント)。</summary>
    public Size CaptureSize => new(Math.Max(0, Bounds.Width - Inset[0] - Inset[2]), Math.Max(0, Bounds.Height - Inset[1] - Inset[3]));

    // 補助プログラムで窓の位置 (ポイント) を調べ直す (マウスがバーや縁の上かを判断するため)
    private async Task RefreshBoundsAsync()
    {
        if (!MacHelper.IsAvailable) return;
        try
        {
            var number = WindowNumber;
            if (number == 0) return;
            var b = await MacHelper.Instance.RequestAsync("window_bounds", new JsonObject { ["window"] = number }, TimeSpan.FromSeconds(3));
            _boundsPoints = new Rect(b["x"]!.GetValue<double>(), b["y"]!.GetValue<double>(), b["w"]!.GetValue<double>(), b["h"]!.GetValue<double>());
            Moved?.Invoke(this, EventArgs.Empty);
        }
        catch (MacHelperException)
        {
            // 窓が画面に出る前など。次の移動・大きさの変更のときに調べ直す
        }
        catch (Exception ex)
        {
            App.Log("CaptureBounds", ex);
        }
    }

    // マウスがバー・縁・角の上ならクリックを受け取り、それ以外 (枠の中) なら下の窓に通す
    private void UpdateClickThrough()
    {
        if (_boundsPoints is not { } b || _resizeStart != null) return;
        if (MacNative.MouseLocation() is not { } m) return;
        var local = new Point(m.X - b.X, m.Y - b.Y);
        bool inside = local.X >= 0 && local.Y >= 0 && local.X <= b.Width && local.Y <= b.Height;
        bool onFrame = inside && (local.Y <= Inset[1] || local.X <= 8 || local.X >= b.Width - 8 || local.Y >= b.Height - 8
                                  || (local.X >= b.Width - 20 && local.Y >= b.Height - 20)); // 右下の大きさを変える角 (20×20) 全体
        bool ignore = !onFrame;
        if (ignore == _ignoring) return;
        _ignoring = ignore;
        MacNative.SetIgnoresMouseEvents(this, ignore);
    }

    public void SetPaused(bool paused)
    {
        var color = paused ? PausedColor : NormalColor;
        TopBar.Background = new SolidColorBrush(color);
        InnerFrame.BorderBrush = new SolidColorBrush(color);
        Grip.Fill = new SolidColorBrush(color);
        OuterFrame.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B));
        PauseButton.Content = paused ? "▶" : "❚❚";
        ToolTip.SetTip(PauseButton, paused ? "再開" : "一時停止");
        TitleText.Text = paused ? "GetText (停止中)" : "GetText";
    }

    private void UpdateSizeText()
    {
        double scale = DesktopScaling > 0 ? DesktopScaling : 1;
        var s = CaptureSize;
        SizeText.Text = s.Width < 1 ? "" : $"{s.Width * scale:0} × {s.Height * scale:0}";
    }

    private void TopBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
    }

    // 右下の角・右と下の縁のドラッグで大きさを変える (左上は動かさない)
    private void Grip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _resizeEdge = sender == RightEdge ? "R" : sender == BottomEdge ? "B" : "RB";
        _resizeStart = e.GetPosition(this);
        _resizeFrom = Bounds.Size;
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void Grip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeStart is not { } start) return;
        var p = e.GetPosition(this);
        if (_resizeEdge.Contains('R')) Width = Math.Max(MinWidth, _resizeFrom.Width + p.X - start.X);
        if (_resizeEdge.Contains('B')) Height = Math.Max(MinHeight, _resizeFrom.Height + p.Y - start.Y);
    }

    private void Grip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizeStart == null) return;
        _resizeStart = null;
        e.Pointer.Capture(null);
        _ = RefreshBoundsAsync();
    }

    private void Pause_Click(object? sender, RoutedEventArgs e) => PauseRequested?.Invoke(this, EventArgs.Empty);

    private void Close_Click(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
