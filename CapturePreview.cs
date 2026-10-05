using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace GetText;

/// <summary>
/// 録画を始める前のプレビュー: 選んだウィンドウ (または画面全体) を、書き出さずに 1 秒に数回だけ取り込む
/// (<see cref="ScreenRecorder"/> と同じ Windows Graphics Capture。録画するときと同じ見え方になる)。
/// </summary>
public sealed class CapturePreview : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(160); // 約 6 枚/秒

    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private Windows.Graphics.SizeInt32 _poolSize;
    private readonly object _lock = new();
    private byte[]? _latest;
    private int _width, _height;
    private DateTime _lastFrame = DateTime.MinValue;
    private int _converting;
    private volatile bool _disposed;

    /// <summary>取り込んでいたウィンドウが閉じた。</summary>
    public event Action? Closed;

    private CapturePreview(GraphicsCaptureItem item)
    {
        _device = ScreenRecorder.CreateDevice();
        try
        {
            _poolSize = item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            _session = _pool.CreateCaptureSession(item);
            try { _session.IsCursorCaptureEnabled = false; } catch { }
            ScreenRecorder.HideYellowBorder(_session);
            item.Closed += (_, _) => Closed?.Invoke();
            _pool.FrameArrived += OnFrameArrived;
            _session.StartCapture();
        }
        catch
        {
            // (最小化・大きさ 0 の窓・GPU の切り替えなど。作った GPU の資源を残さない)
            try { _session?.Dispose(); } catch { }
            try { _pool?.Dispose(); } catch { }
            try { _device.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>プレビューを始める (取り込めないときは null)。</summary>
    public static CapturePreview? TryStart(IntPtr handle, bool isMonitor)
    {
        if (!ScreenRecorder.IsSupported || handle == IntPtr.Zero) return null;
        try
        {
            return new CapturePreview(isMonitor ? ScreenRecorder.CreateItemForMonitor(handle) : ScreenRecorder.CreateItemForWindow(handle));
        }
        catch (Exception ex)
        {
            App.Log("RecordPreview", ex);
            return null;
        }
    }

    /// <summary>新しい画面があれば受け取る (上の行から並んだ BGRA)。</summary>
    public bool TryTake(out byte[] pixels, out int width, out int height)
    {
        lock (_lock)
        {
            pixels = _latest!;
            width = _width;
            height = _height;
            _latest = null;
            return pixels != null;
        }
    }

    private async void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = pool.TryGetNextFrame();
            if (frame == null || _disposed) return;
            var content = frame.ContentSize;
            bool resized = content.Width != _poolSize.Width || content.Height != _poolSize.Height;
            if (DateTime.UtcNow - _lastFrame >= Interval && Interlocked.Exchange(ref _converting, 1) == 0)
            {
                try
                {
                    _lastFrame = DateTime.UtcNow;
                    using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore);
                    int srcW = bitmap.PixelWidth, srcH = bitmap.PixelHeight;
                    int w = Math.Min(content.Width, srcW), h = Math.Min(content.Height, srcH);
                    if (w > 0 && h > 0)
                    {
                        var source = new byte[srcW * srcH * 4];
                        bitmap.CopyToBuffer(source.AsBuffer());
                        var pixels = w == srcW ? source : new byte[w * h * 4];
                        if (pixels != source)
                            for (int y = 0; y < h; y++) Buffer.BlockCopy(source, y * srcW * 4, pixels, y * w * 4, w * 4);
                        lock (_lock)
                        {
                            _latest = pixels;
                            _width = w;
                            _height = h;
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _converting, 0);
                }
            }
            if (resized && !_disposed && content.Width > 0 && content.Height > 0)
            {
                _poolSize = content;
                _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content);
            }
        }
        catch (Exception ex) when (_disposed || ex is ObjectDisposedException)
        {
            // 止めた後
        }
        catch (Exception ex)
        {
            App.Log("RecordPreviewFrame", ex);
        }
        finally
        {
            frame?.Dispose();
        }
    }

    public void Dispose()
    {
        DisposeCore();
        try { _device.Dispose(); } catch { } // (GPU の資源。選び直すたびに作るので、すぐ片付ける)
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
    }
}
