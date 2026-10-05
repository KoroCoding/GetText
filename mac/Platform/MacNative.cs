using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace GetText;

/// <summary>
/// Mac の窓 (NSWindow) を直接操作する小さな機能。Avalonia にない「クリックを下の窓に通す」「窓の番号」と、
/// マウスの位置 (画面全体の座標) を使う。Mac 以外では何もしない。
/// </summary>
internal static class MacNative
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtr(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNInt(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendByte(IntPtr obj, IntPtr sel, byte value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendNIntArg(IntPtr obj, IntPtr sel, nint value);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X, Y;
    }

    [DllImport(CoreGraphics)] private static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(CoreGraphics)] private static extern CGPoint CGEventGetLocation(IntPtr evt);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr obj);

    /// <summary>Avalonia の窓の NSWindow (Mac 以外・取れないときは 0)。</summary>
    private static IntPtr NSWindow(Window window)
    {
        if (!OperatingSystem.IsMacOS()) return IntPtr.Zero;
        try
        {
            var handle = window.TryGetPlatformHandle();
            if (handle == null || handle.Handle == IntPtr.Zero) return IntPtr.Zero;
            // NSView が返る版もあるので、そのときは view.window を使う
            return handle.HandleDescriptor == "NSView" ? SendPtr(handle.Handle, sel_registerName("window")) : handle.Handle;
        }
        catch (Exception ex)
        {
            App.Log("MacNative", ex);
            return IntPtr.Zero;
        }
    }

    /// <summary>窓の番号 (CGWindowID。補助プログラムが窓の位置を調べるのに使う)。取れなければ 0。</summary>
    public static long WindowNumber(Window window)
    {
        var w = NSWindow(window);
        return w == IntPtr.Zero ? 0 : SendNInt(w, sel_registerName("windowNumber"));
    }

    /// <summary>true にすると、窓の上のクリックがすべて下の窓に通る。</summary>
    public static void SetIgnoresMouseEvents(Window window, bool ignore)
    {
        var w = NSWindow(window);
        if (w != IntPtr.Zero) SendByte(w, sel_registerName("setIgnoresMouseEvents:"), ignore ? (byte)1 : (byte)0);
    }

    /// <summary>
    /// すべての操作スペース (フルスクリーンのアプリの上も含む) に出す。読み取り枠をフルスクリーンの動画や資料の上でも使えるように。
    /// </summary>
    public static void ShowOnAllSpaces(Window window)
    {
        var w = NSWindow(window);
        // NSWindowCollectionBehaviorCanJoinAllSpaces (1) | FullScreenAuxiliary (256)
        if (w != IntPtr.Zero) SendNIntArg(w, sel_registerName("setCollectionBehavior:"), 1 | 256);
    }

    /// <summary>マウスの位置 (画面全体のポイント座標、左上が原点)。Mac 以外では null。</summary>
    public static Point? MouseLocation()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var e = CGEventCreate(IntPtr.Zero);
        if (e == IntPtr.Zero) return null;
        try
        {
            var p = CGEventGetLocation(e);
            return new Point(p.X, p.Y);
        }
        finally
        {
            CFRelease(e);
        }
    }
}
