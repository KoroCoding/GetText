using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GetText;

/// <summary>
/// ウィンドウを置けるモニターの範囲 (タスクバーを除く) を求め、画面の外に出ないようにする。
/// 拡大率の違うモニターが並んでいると、WPF の Left/Top は「その窓が今いるモニターの拡大率」で読み替えられ、
/// 窓ごとに座標がずれる。そのため位置の計算・保存・復元は物理ピクセルで行う。
/// </summary>
public static class ScreenUtil
{
    /// <summary>rect (DIP) に最も近いモニターの作業領域 (DIP)。窓がまだ無いときの大まかな位置決めに使う。</summary>
    public static Rect WorkArea(Rect rect, Visual reference)
    {
        var dpi = VisualTreeHelper.GetDpi(reference);
        var physical = new RECT
        {
            Left = (int)(rect.Left * dpi.DpiScaleX),
            Top = (int)(rect.Top * dpi.DpiScaleY),
            Right = (int)(rect.Right * dpi.DpiScaleX),
            Bottom = (int)(rect.Bottom * dpi.DpiScaleY),
        };
        var monitor = MonitorFromRect(ref physical, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return SystemParameters.WorkArea;
        var w = info.rcWork;
        return new Rect(w.Left / dpi.DpiScaleX, w.Top / dpi.DpiScaleY,
            (w.Right - w.Left) / dpi.DpiScaleX, (w.Bottom - w.Top) / dpi.DpiScaleY);
    }

    /// <summary>
    /// 枠 (anchor) の隣に size の大きさのウィンドウを置く位置。右 → 左 → 下 → 上 の順に、画面に収まって
    /// 枠と重ならない場所を探す (重なると、そのウィンドウ自身を OCR で読んでしまうため)。
    /// どこにも入らなければ画面の中に収める。単位は何でもよい (すべて同じ単位でそろえる)。
    /// </summary>
    public static Point Beside(Rect anchor, Size size, double gap, Rect work)
    {
        Point[] candidates =
        [
            new(anchor.Right + gap, anchor.Top),                 // 右
            new(anchor.Left - gap - size.Width, anchor.Top),     // 左
            new(anchor.Left, anchor.Bottom + gap),               // 下
            new(anchor.Left, anchor.Top - gap - size.Height),    // 上
        ];
        for (int i = 0; i < candidates.Length; i++)
        {
            var c = candidates[i];
            // 右・左に置くときは縦だけ、上・下に置くときは横だけ、はみ出す分をずらして収める
            var p = Clamp(c, size, work);
            bool fits = i < 2 ? Math.Abs(p.X - c.X) < 0.5 : Math.Abs(p.Y - c.Y) < 0.5;
            if (fits && !new Rect(p, size).IntersectsWith(anchor)) return p;
        }
        return Clamp(candidates[0], size, work);
    }

    /// <summary>ウィンドウが画面の外に出ないようにした位置。</summary>
    public static Point Clamp(Point position, Size size, Rect work)
    {
        double x = Math.Min(position.X, work.Right - size.Width);
        double y = Math.Min(position.Y, work.Bottom - size.Height);
        return new Point(Math.Max(work.Left, x), Math.Max(work.Top, y));
    }

    /// <summary>
    /// 窓のタイトルの帯 (上端の高さ title) が、どれかのモニターで幅 minVisible 以上見えているか。
    /// 見えていれば窓をつかんで動かせるので、2 つのモニターにまたがって置いた窓などはそのままにする。
    /// </summary>
    public static bool IsReachable(Rect window, IReadOnlyList<Rect> workAreas, double title, double minVisible)
    {
        var strip = new Rect(window.Left, window.Top, Math.Max(1, window.Width), Math.Max(1, title));
        foreach (var work in workAreas)
        {
            var hit = Rect.Intersect(strip, work);
            if (!hit.IsEmpty && hit.Width >= Math.Min(minVisible, window.Width) && hit.Height >= 1) return true;
        }
        return false;
    }

    // ───────── 物理ピクセルでの位置 ─────────

    /// <summary>窓の位置と大きさ (物理ピクセル)。窓のハンドルがまだ無ければ null。</summary>
    public static Rect? PixelBounds(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
        return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>物理ピクセルの矩形に最も近いモニターの作業領域 (物理ピクセル) と拡大率 (1 = 100%)。</summary>
    public static (Rect Work, double Scale) MonitorAt(Rect pixels)
    {
        var r = new RECT { Left = (int)pixels.Left, Top = (int)pixels.Top, Right = (int)pixels.Right, Bottom = (int)pixels.Bottom };
        var monitor = MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return (new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight), 1);
        double scale = GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        var w = info.rcWork;
        return (new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top), scale);
    }

    /// <summary>すべてのモニターの作業領域 (物理ピクセル)。</summary>
    public static List<Rect> AllWorkAreas()
    {
        var list = new List<Rect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref RECT bounds, IntPtr data) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
                list.Add(new Rect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// 窓の左上を物理ピクセルの位置に動かす (大きさは変えない)。拡大率の違うモニターへ移ると Windows が窓の大きさを
    /// 変え、そのとき位置も少しずれることがあるので、ずれていればもう一度合わせる。
    /// </summary>
    public static void MoveTo(Window window, Point pixels)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int x = (int)Math.Round(pixels.X), y = (int)Math.Round(pixels.Y);
        for (int i = 0; i < 2; i++)
        {
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            if (GetWindowRect(hwnd, out var r) && r.Left == x && r.Top == y) break;
        }
    }

    /// <summary>
    /// 窓をつかめない位置 (外したモニターの上・画面の外など) にあれば、最も近いモニターの中に戻す。
    /// 窓のハンドルがまだ無いときは、WPF の座標で大まかに収める。
    /// </summary>
    public static void EnsureVisible(Window window)
    {
        if (PixelBounds(window) is not { } bounds)
        {
            var size = new Size(window.Width, window.Height);
            var work = WorkArea(new Rect(new Point(window.Left, window.Top), size), window);
            var p = Clamp(new Point(window.Left, window.Top), size, work);
            window.Left = p.X;
            window.Top = p.Y;
            return;
        }
        if (window.WindowState != WindowState.Normal) return;
        var (nearest, scale) = MonitorAt(bounds);
        if (IsReachable(bounds, AllWorkAreas(), 32 * scale, 120 * scale)) return;
        MoveTo(window, Clamp(bounds.TopLeft, bounds.Size, nearest));
    }

    /// <summary>
    /// 窓が今いるモニターの作業領域より大きければ縮めて、画面の中に収める
    /// (1280×720・拡大率 150% のノート PC などで、タイトルバーや下のボタンが画面の外に出ないように)。
    /// </summary>
    public static void FitToWorkArea(Window window)
    {
        if (window.WindowState != WindowState.Normal || PixelBounds(window) is not { } bounds) return;
        var (work, scale) = MonitorAt(bounds);
        double width = work.Width / scale, height = work.Height / scale;
        bool changed = false;
        if (window.Width > width) { window.Width = Math.Max(window.MinWidth, width); changed = true; }
        if (window.Height > height) { window.Height = Math.Max(window.MinHeight, height); changed = true; }
        if (changed && PixelBounds(window) is { } now) MoveTo(window, Clamp(now.TopLeft, now.Size, work));
    }

    /// <summary>窓の左上 (物理ピクセル) を [x, y] で返す (設定への保存用)。最大化・最小化中や窓が無いときは null。</summary>
    public static int[]? PixelPosition(Window window) =>
        window.WindowState == WindowState.Normal && PixelBounds(window) is { } b && b.Left > -30000
            ? [(int)b.Left, (int)b.Top]
            : null;

    /// <summary>保存した物理ピクセルの位置 [x, y] に窓を戻す (つかめない位置なら画面の中に戻す)。</summary>
    public static void RestorePixelPosition(Window window, int[]? position)
    {
        if (position is not { Length: 2 }) return;
        MoveTo(window, new Point(position[0], position[1]));
        EnsureVisible(window);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT bounds, IntPtr data);

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
