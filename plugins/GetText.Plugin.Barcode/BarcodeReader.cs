using ZXing;
using ZXing.Common;

namespace GetText.Plugins.Barcode;

/// <summary>見つけたコード 1 つ。</summary>
public sealed record BarcodeHit(string Text, string Format)
{
    /// <summary>開いてよい URL (http・https だけ。ユーザー名入りは除く)。</summary>
    public Uri? Url => Uri.TryCreate(Text.Trim(), UriKind.Absolute, out var u)
                       && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
                       && string.IsNullOrEmpty(u.UserInfo) && !string.IsNullOrEmpty(u.Host) ? u : null;

    /// <summary>画面に出す種類の名前。</summary>
    public string FormatLabel => Format switch
    {
        "QR_CODE" => "QR コード",
        "DATA_MATRIX" => "データマトリックス",
        "AZTEC" => "Aztec",
        "PDF_417" => "PDF417",
        "EAN_13" or "EAN_8" => "JAN / EAN",
        "UPC_A" or "UPC_E" => "UPC",
        "CODE_128" => "Code 128",
        "CODE_39" => "Code 39",
        "ITF" => "ITF",
        "CODABAR" => "NW-7",
        _ => Format,
    };
}

/// <summary>画像 (BGRA) から QR コード・バーコードを探す (いくつあってもよい)。読めるものだけ返す。</summary>
public static class BarcodeScanner
{
    private static readonly List<BarcodeFormat> Formats =
    [
        BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.PDF_417,
        BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.UPC_E,
        BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.ITF, BarcodeFormat.CODABAR,
    ];

    public static List<BarcodeHit> Scan(byte[] bgra, int width, int height)
    {
        if (width < 8 || height < 8 || bgra.Length < width * height * 4) return [];
        var source = new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true, PossibleFormats = Formats },
        };
        var results = reader.DecodeMultiple(source) ?? [];
        return results
            .Where(r => !string.IsNullOrEmpty(r.Text))
            .Select(r => new BarcodeHit(r.Text, r.BarcodeFormat.ToString()))
            .DistinctBy(h => (h.Text, h.Format))
            .ToList();
    }
}
