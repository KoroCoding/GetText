using Avalonia;
using Avalonia.Controls;

namespace GetText;

/// <summary>窓を画面の中に収める・枠の隣に置く (座標は Avalonia の画面座標 = ピクセル)。</summary>
internal static class WindowPlacement
{
    /// <summary>
    /// 枠 (anchor) の隣に size の大きさの窓を置く位置。右 → 左 → 下 → 上 の順に、画面に収まって枠と重ならない場所を探す
    /// (重なると、その窓自身を読み取ってしまうため)。どこにも入らなければ画面の中に収める。Windows 版の ScreenUtil.Beside と同じ。
    /// </summary>
    public static Point Beside(Rect anchor, Size size, double gap, Rect work)
    {
        Point[] candidates =
        [
            new(anchor.Right + gap, anchor.Top),
            new(anchor.Left - gap - size.Width, anchor.Top),
            new(anchor.Left, anchor.Bottom + gap),
            new(anchor.Left, anchor.Top - gap - size.Height),
        ];
        for (int i = 0; i < candidates.Length; i++)
        {
            var c = candidates[i];
            var p = Clamp(c, size, work);
            bool fits = i < 2 ? Math.Abs(p.X - c.X) < 0.5 : Math.Abs(p.Y - c.Y) < 0.5;
            if (fits && !new Rect(p, size).Intersects(anchor)) return p;
        }
        return Clamp(candidates[0], size, work);
    }

    public static Point Clamp(Point position, Size size, Rect work)
    {
        double x = Math.Min(position.X, work.Right - size.Width);
        double y = Math.Min(position.Y, work.Bottom - size.Height);
        return new Point(Math.Max(work.Left, x), Math.Max(work.Top, y));
    }

    /// <summary>窓の位置と大きさ (ピクセル)。</summary>
    public static Rect PixelBounds(Window w)
    {
        double scale = w.DesktopScaling > 0 ? w.DesktopScaling : 1;
        var size = w.FrameSize ?? w.ClientSize;
        return new Rect(w.Position.X, w.Position.Y, size.Width * scale, size.Height * scale);
    }

    /// <summary>窓のある (最も近い) 画面の作業領域 (ピクセル)。</summary>
    public static Rect WorkArea(Window w)
    {
        var screen = w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary ?? w.Screens.All.FirstOrDefault();
        if (screen == null) return new Rect(0, 0, 1920, 1080);
        var a = screen.WorkingArea;
        return new Rect(a.X, a.Y, a.Width, a.Height);
    }

    /// <summary>窓のタイトルの帯が、どの画面からも見えない (外したモニターの上など) なら、最も近い画面の中に戻す。</summary>
    public static void EnsureVisible(Window w)
    {
        if (w.WindowState != WindowState.Normal) return;
        var bounds = PixelBounds(w);
        double scale = w.DesktopScaling > 0 ? w.DesktopScaling : 1;
        var strip = new Rect(bounds.X, bounds.Y, bounds.Width, 28 * scale);
        foreach (var screen in w.Screens.All)
        {
            var a = screen.WorkingArea;
            var hit = strip.Intersect(new Rect(a.X, a.Y, a.Width, a.Height));
            if (hit.Width >= Math.Min(120 * scale, bounds.Width) && hit.Height >= 1) return;
        }
        var nearest = w.Screens.ScreenFromPoint(new PixelPoint((int)bounds.Center.X, (int)bounds.Center.Y))
                      ?? w.Screens.Primary ?? w.Screens.All.FirstOrDefault();
        if (nearest == null) return;
        var work = new Rect(nearest.WorkingArea.X, nearest.WorkingArea.Y, nearest.WorkingArea.Width, nearest.WorkingArea.Height);
        var p = Clamp(bounds.TopLeft, bounds.Size, work);
        w.Position = new PixelPoint((int)p.X, (int)p.Y);
    }
}
