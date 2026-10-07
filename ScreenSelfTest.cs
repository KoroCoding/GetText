using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GetText;

/// <summary>
/// 窓の置き場所の確認 (GetText.exe --selftest-screen 結果.txt)。拡大率の違うモニターでの保存と復元・枠の隣への追従、
/// 外したモニターの上に残った窓を戻す動きを、実際の窓で確かめる。窓は透明にし、押せず、前面にも出さない
/// (利用者の画面には何も映らず、操作も邪魔しない)。
/// </summary>
internal static class ScreenSelfTest
{
    public static async Task<int> RunAsync(string reportPath)
    {
        var report = new StringBuilder();
        int failed = 0, skipped = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine((ok ? "OK  " : "NG  ") + what);
            if (!ok) failed++;
        }
        void Skip(string what)
        {
            report.AppendLine("--  " + what);
            skipped++;
        }

        App.ApplyTheme(AppTheme.Light);
        var monitors = ScreenUtil.AllWorkAreas().Select(w => (Work: w, ScreenUtil.MonitorAt(w).Scale)).ToList();
        foreach (var m in monitors)
            report.AppendLine($"    モニター: 作業領域 {R(m.Work)} ・ 拡大率 {m.Scale * 100:0}%");
        // a: メインのモニター (左上が (0, 0))、b: それと拡大率の違うモニター (以前の保存のしかたでずれていた側)
        var a = monitors.FirstOrDefault(m => m.Work.Contains(new Point(0, 0)));
        if (a.Work.Width <= 0) a = monitors[0];
        var b = monitors.FirstOrDefault(m => Math.Abs(m.Scale - a.Scale) > 0.01);
        bool mixed = b.Work.Width > 0;
        if (!mixed) b = monitors.Count > 1 ? monitors[1] : a;

        AppSettings NewSettings(bool follow) => new()
        {
            OcrEngine = OcrEngineKind.Ai, Translate = false, Follow = follow, TextTopmost = false, HideOcrDuringMinutes = false,
        };

        // 1. 拡大率の違うモニターに置いた枠と文字の画面を閉じて開き直すと、同じ場所・同じ大きさに戻る
        {
            var s = NewSettings(follow: false);
            var (cap, text) = await Open(s);
            ScreenUtil.MoveTo(cap, new Point(b.Work.Left + 100, b.Work.Top + 100));
            await Idle(text);
            var textSize = ScreenUtil.PixelBounds(text)!.Value.Size;
            ScreenUtil.MoveTo(text, new Point(b.Work.Right - textSize.Width - 60, b.Work.Top + 80));
            await Idle(text);
            var capBefore = ScreenUtil.PixelBounds(cap)!.Value;
            var textBefore = ScreenUtil.PixelBounds(text)!.Value;
            text.Close(); // 閉じるときに位置を設定に書く (見本の動作なので設定ファイルには保存しない)
            await Idle(text);

            var (cap2, text2) = await Open(s);
            var capAfter = ScreenUtil.PixelBounds(cap2)!.Value;
            var textAfter = ScreenUtil.PixelBounds(text2)!.Value;
            Check(Near(capBefore, capAfter), $"枠を同じ場所・大きさに戻す (前 {R(capBefore)} → 後 {R(capAfter)})");
            Check(Near(textBefore, textAfter), $"文字の画面を同じ場所・大きさに戻す (前 {R(textBefore)} → 後 {R(textAfter)})");
            Check(Math.Abs(textAfter.Width - s.TextWidth * b.Scale) < 3,
                $"文字の画面の大きさがモニターの拡大率に合う (幅 {textAfter.Width:0} px = {s.TextWidth:0} × {b.Scale:0.##})");

            // 参考: 以前の保存のしかた (WPF の Left/Top) で戻した場合のずれ
            var old = new Window { WindowStartupLocation = WindowStartupLocation.Manual, Left = s.TextLeft ?? 0, Top = s.TextTop ?? 0, Width = s.TextWidth, Height = s.TextHeight };
            Hide(old);
            old.Show();
            await Idle(old);
            var oldAt = ScreenUtil.PixelBounds(old)!.Value;
            report.AppendLine($"    (参考: 以前の保存のしかたでは {R(oldAt)} に戻り、{Math.Abs(oldAt.Left - textBefore.Left):0} px ずれる{(mixed ? "" : " ・ 拡大率が同じなのでずれない")})");
            old.Close();
            text2.Close();
            await Idle(text2);
            if (!mixed) Skip("(拡大率の違うモニターが無いので、1 は同じ拡大率のモニターで確かめた)");
        }

        // 2. 枠を拡大率の違うモニターへ動かしても、文字の画面がすぐ隣 (重ならず、画面の中) に付いていく
        {
            var s = NewSettings(follow: true);
            var (cap, text) = await Open(s);
            foreach (var (m, where) in new[] { (a, "1 つ目のモニター"), (b, "2 つ目のモニター"), (a, "1 つ目のモニターに戻す") })
            {
                ScreenUtil.MoveTo(cap, new Point(m.Work.Left + 100, m.Work.Top + 100));
                await Idle(text);
                var c = ScreenUtil.PixelBounds(cap)!.Value;
                var t = ScreenUtil.PixelBounds(text)!.Value;
                double gap = t.Left - c.Right;
                Check(Math.Abs(gap - 8 * m.Scale) <= 2 && Math.Abs(t.Top - c.Top) <= 2 && Inside(t, m.Work) && Math.Abs(t.Width - s.TextWidth * m.Scale) < 3,
                    $"{where}: 枠の右隣に付く (枠 {R(c)} ・ 文字 {R(t)} ・ すき間 {gap:0} px)");
            }
            // 参考: 以前の追従のしかた (WPF の Left/Top で計算) で、2 つ目のモニターの枠の隣に置いた場合
            {
                ScreenUtil.MoveTo(cap, new Point(b.Work.Left + 100, b.Work.Top + 100));
                await Idle(text);
                var anchor = new Rect(cap.Left, cap.Top, cap.ActualWidth, cap.ActualHeight);
                var p = ScreenUtil.Beside(anchor, new Size(text.ActualWidth, text.ActualHeight), 8, ScreenUtil.WorkArea(anchor, cap));
                text.Left = p.X;
                text.Top = p.Y;
                await Idle(text);
                var c = ScreenUtil.PixelBounds(cap)!.Value;
                var t = ScreenUtil.PixelBounds(text)!.Value;
                report.AppendLine($"    (参考: 以前の追従のしかたでは 枠 {R(c)} ・ 文字 {R(t)} ・ すき間 {t.Left - c.Right:0} px{(Rect.Intersect(c, t).IsEmpty ? "" : " ・ 枠と重なる")})");
            }
            // モニターの右端では左側に回る
            var capSize = ScreenUtil.PixelBounds(cap)!.Value.Size;
            ScreenUtil.MoveTo(cap, new Point(b.Work.Right - capSize.Width - 10, b.Work.Top + 120));
            await Idle(text);
            var c2 = ScreenUtil.PixelBounds(cap)!.Value;
            var t2 = ScreenUtil.PixelBounds(text)!.Value;
            Check(t2.Right <= c2.Left && Inside(t2, b.Work), $"モニターの右端では左側に回る (枠 {R(c2)} ・ 文字 {R(t2)})");
            text.Close();
            await Idle(text);
        }

        // 3. 前回の場所が今は無いモニターの上なら、画面の中に戻して開く
        {
            var s = NewSettings(follow: false);
            s.CapturePixel = [60000, 60000];
            s.TextPixel = [-60000, 60000];
            s.CaptureLeft = 40000; s.CaptureTop = 40000; s.TextLeft = -40000; s.TextTop = 40000;
            var (cap, text) = await Open(s);
            var all = ScreenUtil.AllWorkAreas();
            var c = ScreenUtil.PixelBounds(cap)!.Value;
            var t = ScreenUtil.PixelBounds(text)!.Value;
            Check(all.Any(w => Inside(c, w)), $"外したモニターの上にあった枠を画面の中に戻す ({R(c)})");
            Check(all.Any(w => Inside(t, w)), $"外したモニターの上にあった文字の画面を画面の中に戻す ({R(t)})");
            text.Close();
            await Idle(text);
        }

        // 4. 使っている途中でモニターを外した (窓が画面の外に残った) とき、モニターの変更の通知で戻す
        {
            var s = NewSettings(follow: false);
            var (cap, text) = await Open(s);
            var minutes = new MinutesWindow(text, s);
            Hide(minutes);
            minutes.Show();
            await Idle(minutes);
            ScreenUtil.MoveTo(cap, new Point(60000, 100));
            ScreenUtil.MoveTo(text, new Point(60500, 100));
            ScreenUtil.MoveTo(minutes, new Point(-60000, 100));
            typeof(TextWindow).GetField("_minutesWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(text, minutes);
            // 本物と同じく、Windows からの通知 (WM_DISPLAYCHANGE) を送る
            SendMessage(new WindowInteropHelper(text).Handle, 0x007E, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(2500);
            await Idle(text);
            var all = ScreenUtil.AllWorkAreas();
            var c = ScreenUtil.PixelBounds(cap)!.Value;
            var t = ScreenUtil.PixelBounds(text)!.Value;
            var mm = ScreenUtil.PixelBounds(minutes)!.Value;
            Check(all.Any(w => Inside(c, w)) && all.Any(w => Inside(t, w)) && all.Any(w => Inside(mm, w)),
                $"モニターの変更の通知で、画面の外の窓を戻す (枠 {R(c)} ・ 文字 {R(t)} ・ 議事録 {R(mm)})");
            // 2 つのモニターにまたがって置いた窓は、つかめるのでそのままにする
            if (monitors.Count > 1)
            {
                var spanning = new Point(monitors[1].Work.Left - 200, monitors[1].Work.Top + 50);
                ScreenUtil.MoveTo(minutes, spanning);
                var before = ScreenUtil.PixelBounds(minutes)!.Value;
                text.OnDisplayChanged();
                await Idle(text);
                var after = ScreenUtil.PixelBounds(minutes)!.Value;
                Check(Near(before, after), $"モニターにまたがって置いた窓は動かさない (前 {R(before)} → 後 {R(after)})");
            }
            else Skip("(モニターが 1 つなので、またがって置いた窓の確認は省いた)");
            minutes.Hide();
            typeof(TextWindow).GetField("_minutesWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(text, null);

            // 5. 拡大率の違うモニターに置いた議事録の画面も、開き直すと同じ場所に戻る
            ScreenUtil.MoveTo(minutes, new Point(b.Work.Left + 150, b.Work.Top + 60));
            minutes.Show();
            await Idle(minutes);
            var mBefore = ScreenUtil.PixelBounds(minutes)!.Value;
            minutes.StorePlacement();
            minutes.Hide();
            var minutes2 = new MinutesWindow(text, s);
            Hide(minutes2);
            minutes2.Show();
            await Idle(minutes2);
            var mAfter = ScreenUtil.PixelBounds(minutes2)!.Value;
            Check(Near(mBefore, mAfter), $"議事録の画面を同じ場所・大きさに戻す (前 {R(mBefore)} → 後 {R(mAfter)})");
            minutes2.Hide();
            text.Close();
            await Idle(text);
        }

        report.Insert(0, $"窓の置き場所の確認 ・ {(failed == 0 ? "すべて合格" : $"不合格 {failed} 件")}{(mixed ? " (拡大率の違うモニターで確認)" : "")}\n");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        return failed == 0 ? 0 : 2;
    }

    private static async Task<(CaptureWindow, TextWindow)> Open(AppSettings s)
    {
        var cap = new CaptureWindow(s);
        var text = new TextWindow(cap, s);
        Hide(cap);
        Hide(text);
        cap.Show();
        text.Show();
        await Idle(text);
        return (cap, text);
    }

    // 透明で押せず、前面にも出ず、タスクバーにも出ない窓にする (作った直後、表示される前に)
    private static void Hide(Window w)
    {
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        if (w.AllowsTransparency)
        {
            w.Opacity = 0;
            return;
        }
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
        };
    }

    private static bool Near(Rect x, Rect y) =>
        Math.Abs(x.Left - y.Left) <= 2 && Math.Abs(x.Top - y.Top) <= 2 && Math.Abs(x.Width - y.Width) <= 3 && Math.Abs(x.Height - y.Height) <= 3;

    private static bool Inside(Rect r, Rect work) =>
        r.Left >= work.Left - 1 && r.Top >= work.Top - 1 && r.Right <= work.Right + 1 && r.Bottom <= work.Bottom + 1;

    private static string R(Rect r) => $"({r.Left:0}, {r.Top:0}) {r.Width:0}×{r.Height:0}";

    private static async Task Idle(Window w)
    {
        for (int i = 0; i < 3; i++) await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(200);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    private const uint LWA_ALPHA = 2;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
