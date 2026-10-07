using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using GetText.Plugins;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace GetText;

/// <summary>
/// 画像のファイル・PDF のページを画像にする (Windows)。画像は Windows の画像の部品 (BitmapDecoder)、PDF は Windows.Data.Pdf。
/// 外部の部品・通信は使わない。
/// </summary>
public sealed class WindowsDocumentService : IDocumentService
{
    public IReadOnlyList<string> ImageExtensions { get; } = ["png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff", "jxr", "ico", "heic", "webp"];

    public async Task<CapturedImage> LoadImageAsync(string path, int maxSize, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(cancellationToken);
        using var stream = await file.OpenReadAsync().AsTask(cancellationToken);
        BitmapDecoder decoder;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            throw new InvalidOperationException($"画像を読めません ({Path.GetFileName(path)})", ex);
        }
        return await Decode(decoder, maxSize, cancellationToken);
    }

    public async Task<int> GetPdfPageCountAsync(string path, CancellationToken cancellationToken) =>
        (int)(await Open(path, cancellationToken)).PageCount;

    public async Task<CapturedImage> RenderPdfPageAsync(string path, int pageIndex, double dpi, int maxSize, CancellationToken cancellationToken)
    {
        var pdf = await Open(path, cancellationToken);
        if (pageIndex < 0 || pageIndex >= pdf.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        using var page = pdf.GetPage((uint)pageIndex);
        // page.Size は DIP (1/96 インチ。回転を含む)。出したいピクセルの大きさを決める
        double scale = Math.Clamp(dpi, 36, 600) / 96;
        double longest = Math.Max(page.Size.Width, page.Size.Height) * scale;
        if (longest > maxSize) scale *= maxSize / longest;
        uint width = (uint)Math.Max(1, Math.Round(page.Size.Width * scale));
        uint height = (uint)Math.Max(1, Math.Round(page.Size.Height * scale));
        // 描く大きさ (DestinationWidth) も DIP で、画面の拡大率の分だけ大きく描かれる。拡大率で割って頼み、最後に決めた大きさにそろえる
        double system = GetDpiForSystem() is > 0 and var d ? d / 96.0 : 1;
        using var output = new InMemoryRandomAccessStream();
        BitmapDecoder decoder;
        try
        {
            await page.RenderToStreamAsync(output, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(width / system)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(height / system)),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            }).AsTask(cancellationToken);
            output.Seek(0);
            decoder = await BitmapDecoder.CreateAsync(output).AsTask(cancellationToken);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException($"PDF の {pageIndex + 1} ページを画像にできません", ex);
        }
        return await Decode(decoder, int.MaxValue, cancellationToken, exact: (width, height));
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    private static async Task<PdfDocument> Open(string path, CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(cancellationToken);
            return await PdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException)
        {
            // パスワードのかかった PDF・壊れた PDF
            throw new InvalidOperationException($"PDF を開けません ({Path.GetFileName(path)})。パスワードのかかった PDF は読めません", ex);
        }
    }

    /// <param name="exact">この大きさにそろえる (PDF のページ。向きの直しは無い)。</param>
    private static async Task<CapturedImage> Decode(BitmapDecoder decoder, int maxSize, CancellationToken cancellationToken, (uint Width, uint Height)? exact = null)
    {
        // 写真の向き (EXIF) で 90 度回っているときは、出てくる画像の幅と高さが入れ替わる
        bool rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
        uint w = decoder.PixelWidth, h = decoder.PixelHeight;
        var transform = new BitmapTransform();
        double longest = Math.Max(w, h);
        if (exact is { } e && (e.Width != w || e.Height != h))
        {
            (w, h) = e;
            transform.ScaledWidth = w;
            transform.ScaledHeight = h;
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }
        else if (longest > maxSize)
        {
            double s = maxSize / longest;
            w = (uint)Math.Max(1, Math.Round(w * s));
            h = (uint)Math.Max(1, Math.Round(h * s));
            // (縮める大きさは、向きを直す前の大きさで指定する)
            transform.ScaledWidth = w;
            transform.ScaledHeight = h;
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }
        var (width, height) = rotated ? (h, w) : (w, h);
        PixelDataProvider data;
        try
        {
            data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException("画像を読めません (壊れているか、読めない形式です)", ex);
        }
        var bgra = data.DetachPixelData();
        if (bgra.Length != width * height * 4) throw new InvalidOperationException("画像の大きさが合いません");
        return new CapturedImage(bgra, (int)width, (int)height, DateTimeOffset.Now);
    }
}
