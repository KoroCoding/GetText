using System.Runtime.InteropServices;
using System.Windows;
using static GetText.NativeMethods;

namespace GetText;

internal static class ScreenCapture
{
    /// <summary>
    /// 読み直しが必要なほど画面が変わったか。文字入力カーソルの点滅 (数十ピクセル) 程度の変化は無視する。
    /// 1 文字の入力や削除 (百数十ピクセル以上) は変化として扱う。
    /// </summary>
    public static bool HasChanged(byte[] previous, byte[] current, int tolerancePixels = 64)
    {
        if (previous.Length != current.Length) return true;
        var a = MemoryMarshal.Cast<byte, int>(previous);
        var b = MemoryMarshal.Cast<byte, int>(current);
        if (a.SequenceEqual(b)) return false; // 大半はここで終わる (ベクトル化されていて速い)
        int changed = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i] && ++changed > tolerancePixels) return true;
        }
        return false;
    }

    /// <summary>
    /// 画面の指定領域(物理ピクセル)を dstWidth x dstHeight に拡縮し、BGRA32 (top-down) で取得する。
    /// </summary>
    public static byte[] Capture(Int32Rect src, int dstWidth, int dstHeight)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = dstWidth,
            biHeight = -dstHeight, // 負値で top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr bitmap = CreateDIBSection(screenDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            // (大きすぎる範囲・メモリ不足など: 落ちずに例外にする。読み取りは次の周期で試し直す)
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            throw new InvalidOperationException("画面を取り込む画像を用意できませんでした");
        }
        IntPtr old = SelectObject(memDc, bitmap);
        try
        {
            SetStretchBltMode(memDc, HALFTONE);
            SetBrushOrgEx(memDc, 0, 0, IntPtr.Zero);
            StretchBlt(memDc, 0, 0, dstWidth, dstHeight,
                screenDc, src.X, src.Y, src.Width, src.Height, SRCCOPY | CAPTUREBLT);

            var buffer = new byte[dstWidth * dstHeight * 4];
            Marshal.Copy(bits, buffer, 0, buffer.Length);
            return buffer;
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
