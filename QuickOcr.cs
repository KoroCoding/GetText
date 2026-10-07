using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GetText.Plugins;

namespace GetText;

/// <summary>
/// Quick OCR (Windows): 画面の範囲をドラッグで選び、その範囲の文字を 1 回だけ読んでクリップボードに入れる (読み取りの画面は開かない)。
/// どのアプリを使っていても Ctrl+Alt+Q (設定で変えられる) か、コマンドの一覧から使える。
/// 読み取りは GetText の読み取りの方式 (利用者が選んだ方式 → 使える方式) で、この PC の中で行う。
/// </summary>
public static class QuickOcr
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);

    private struct CursorPoint { public int X, Y; }

    private static bool _running;

    public static async void Run()
    {
        if (_running || App.DemoMode) return;
        _running = true;
        try
        {
            if (await SelectAndCaptureAsync("読み取る範囲をドラッグで選ぶ ・ Esc でやめる") is not { } shot) return;
            var ocr = PluginRuntime.GetService<IOcrService>() ?? throw new InvalidOperationException("読み取りの準備ができていません");
            var result = await ocr.RecognizeAsync(shot.Bgra, shot.Width, shot.Height, null, CancellationToken.None);
            var text = result.Text.Trim();
            if (text.Length == 0)
            {
                PluginToast.Show("Quick OCR", new PluginNotification("文字が見つかりませんでした", "範囲を広げるか、文字の上を選んでください。", PluginNotificationKind.Warning));
                return;
            }
            bool copied = await CopyAsync(text);
            int lines = result.Lines.Count;
            PluginToast.Show("Quick OCR", copied
                ? new PluginNotification($"{lines} 行をコピーしました", text.Length > 80 ? text[..80] + "…" : text, PluginNotificationKind.Success)
                : new PluginNotification("コピーできませんでした", "ほかのアプリがクリップボードを使っています。", PluginNotificationKind.Warning));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            App.Log("QuickOcr", ex);
            PluginToast.Show("Quick OCR", new PluginNotification("読み取れませんでした", ex.Message, PluginNotificationKind.Error));
        }
        finally
        {
            _running = false;
        }
    }

    private static async Task<bool> CopyAsync(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                await Task.Delay(40);
            }
        }
        return false;
    }

    /// <summary>範囲を選んでもらい、その範囲を撮る (Esc でやめたら null)。拡張機能の SelectScreenRegionAsync もこれを使う。</summary>
    public static async Task<CapturedImage?> SelectAndCaptureAsync(string hintText)
    {
        var rect = SelectRegion(hintText);
        if (rect is not { } r || r.Width < 4 || r.Height < 4) return null;
        await Task.Delay(120); // (選ぶ画面が消えてから撮る)
        return new CapturedImage(ScreenCapture.Capture(r, r.Width, r.Height), r.Width, r.Height, DateTimeOffset.Now);
    }

    /// <summary>画面全体に薄い幕を出し、ドラッグで範囲を選んでもらう。Esc・右クリックでやめる。範囲は画面の物理ピクセル。</summary>
    private static Int32Rect? SelectRegion(string hintText)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
            Topmost = true,
            ShowInTaskbar = false,
            Left = SystemParameters.VirtualScreenLeft,
            Top = SystemParameters.VirtualScreenTop,
            Width = SystemParameters.VirtualScreenWidth,
            Height = SystemParameters.VirtualScreenHeight,
            Cursor = Cursors.Cross,
            Title = "Quick OCR",
        };
        System.Windows.Automation.AutomationProperties.SetName(window, "読み取る範囲を選ぶ");
        var canvas = new Canvas();
        var selection = new Rectangle { StrokeThickness = 2, Visibility = Visibility.Collapsed, Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) };
        selection.SetResourceReference(Shape.StrokeProperty, "Gt.Accent");
        var hint = new Border
        {
            Padding = new Thickness(14, 8, 14, 8),
            Child = new TextBlock { Text = hintText, Foreground = Brushes.White, FontSize = 14 },
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(6),
        };
        canvas.Children.Add(selection);
        canvas.Children.Add(hint);
        window.Content = canvas;

        Point? start = null;
        Int32Rect? result = null;
        window.Loaded += (_, _) =>
        {
            // 案内は、いま使っている画面 (マウスのある画面) の上の方に出す
            var mouse = GetCursorPos(out var cursor) ? window.PointFromScreen(new Point(cursor.X, cursor.Y)) : new Point(window.Width / 2, 120);
            hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(hint, Math.Max(8, mouse.X - hint.DesiredSize.Width / 2));
            Canvas.SetTop(hint, Math.Max(8, mouse.Y - 80));
            window.Activate();
            window.Focus();
        };
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
        window.MouseRightButtonUp += (_, _) => window.Close();
        window.MouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(canvas);
            selection.Visibility = Visibility.Visible;
            hint.Visibility = Visibility.Collapsed;
            window.CaptureMouse();
        };
        window.MouseMove += (_, e) =>
        {
            if (start is not { } s) return;
            var p = e.GetPosition(canvas);
            Canvas.SetLeft(selection, Math.Min(s.X, p.X));
            Canvas.SetTop(selection, Math.Min(s.Y, p.Y));
            selection.Width = Math.Abs(p.X - s.X);
            selection.Height = Math.Abs(p.Y - s.Y);
        };
        window.MouseLeftButtonUp += (_, e) =>
        {
            if (start is not { } s) return;
            window.ReleaseMouseCapture();
            var p = e.GetPosition(canvas);
            // 物理ピクセルに直す (拡大率の違うモニターでも合うよう、角を 1 つずつ変換する)
            var a = window.PointToScreen(new Point(Math.Min(s.X, p.X), Math.Min(s.Y, p.Y)));
            var b = window.PointToScreen(new Point(Math.Max(s.X, p.X), Math.Max(s.Y, p.Y)));
            result = new Int32Rect((int)Math.Round(a.X), (int)Math.Round(a.Y), (int)Math.Round(b.X - a.X), (int)Math.Round(b.Y - a.Y));
            window.Close();
        };
        window.ShowDialog();
        return result;
    }
}
