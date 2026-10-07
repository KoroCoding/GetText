using System.Runtime.InteropServices;
using System.Windows;
using GetText.Plugins;
using static GetText.NativeMethods;

namespace GetText;

/// <summary>
/// 拡張機能のための画面の取り込み (Windows)。モニター・範囲は画面から、窓は PrintWindow (PW_RENDERFULLCONTENT) で取り込む。
/// 取り込みを禁止された窓 (SetWindowDisplayAffinity・DRM の動画など) は黒く写り、それを回避しない。
/// </summary>
public sealed class WindowsScreenCapture : IScreenCaptureService
{
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);

    private const uint PwRenderFullContent = 2;

    public Task<IReadOnlyList<CaptureSource>> ListSourcesAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<CaptureSource>>(() =>
    {
        var list = new List<CaptureSource>();
        var monitors = ScreenUtil.AllMonitors();
        for (int i = 0; i < monitors.Count; i++)
        {
            var (b, primary) = monitors[i];
            list.Add(new CaptureSource(CaptureSourceKind.Monitor, $"monitor:{i}",
                $"画面 {i + 1}{(primary ? " (メイン)" : "")} ・ {b.Width:0}×{b.Height:0}", (int)b.X, (int)b.Y, (int)b.Width, (int)b.Height));
        }
        foreach (var w in WindowList.GetEachWindow())
            list.Add(new CaptureSource(CaptureSourceKind.Window, w.Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.IsNullOrEmpty(w.ProcessName) ? w.Title : $"{w.Title} ({w.ProcessName})"));
        return list;
    }, cancellationToken);

    public Task<CapturedImage?> CaptureAsync(CaptureSource source, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.Now;
        switch (source.Kind)
        {
            case CaptureSourceKind.Monitor:
            case CaptureSourceKind.Region:
                if (source.Width < 4 || source.Height < 4) return null;
                var rect = new Int32Rect(source.X, source.Y, source.Width, source.Height);
                return new CapturedImage(ScreenCapture.Capture(rect, source.Width, source.Height), source.Width, source.Height, now);
            case CaptureSourceKind.Window:
                return long.TryParse(source.Id, out var handle) ? CaptureWindow(new IntPtr(handle), now) : null;
            default:
                return null;
        }
    }, cancellationToken);

    private static CapturedImage? CaptureWindow(IntPtr hwnd, DateTimeOffset now)
    {
        if (!IsWindow(hwnd) || IsIconic(hwnd) || !GetWindowRect(hwnd, out var r)) return null;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        if (width < 4 || height < 4 || (long)width * height > 64_000_000) return null;
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // 負値で top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr bitmap = CreateDIBSection(screenDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            // (メモリが足りないなど: 落ちずに「取り込めなかった」とする)
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }
        IntPtr old = SelectObject(memDc, bitmap);
        try
        {
            if (!PrintWindow(hwnd, memDc, PwRenderFullContent)) return null;
            var buffer = new byte[width * height * 4];
            Marshal.Copy(bits, buffer, 0, buffer.Length);
            return new CapturedImage(buffer, width, height, now);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
