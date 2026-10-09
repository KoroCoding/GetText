namespace GetText.Plugins;

/// <summary>OCR の 1 行の結果 (座標は入力画像のピクセル)。Spans は文字・単語の位置 (エンジンが返すときだけ)。</summary>
public sealed record OcrResultLine(string Text, double Left, double Top, double Right, double Bottom, double? Confidence = null,
    IReadOnlyList<OcrResultSpan>? Spans = null);

public sealed record OcrResultSpan(string Text, double Left, double Top, double Right, double Bottom);

public sealed record OcrResult(IReadOnlyList<OcrResultLine> Lines, string ProviderId)
{
    public string Text => string.Join("\n", Lines.Select(l => l.Text));
}

/// <summary>OCR の提供元 (Windows OCR・Mac の文字認識・AI OCR など。拡張機能も加えられる)。</summary>
public interface IOcrProvider
{
    string Id { get; }
    LocalizedText Name { get; }
    /// <summary>PC の中だけで動くか (false ならインターネットに画像を送る。利用者に知らせる)。</summary>
    bool IsLocal { get; }
    /// <summary>今使えるか (モデルが入っている・OS が対応しているなど)。</summary>
    bool IsAvailable { get; }
    Task<OcrResult> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken);
}

/// <summary>GetText の OCR (利用者が選んだ方式。使えなければ OS の標準の文字認識)。</summary>
public interface IOcrService
{
    IReadOnlyList<IOcrProvider> Providers { get; }

    /// <summary>providerId が null なら、利用者が選んだ方式 → OS の標準の順で使えるもの。</summary>
    Task<OcrResult> RecognizeAsync(byte[] bgra, int width, int height, string? providerId, CancellationToken cancellationToken);
}

/// <summary>GetText の翻訳。この PC の中の翻訳モデルだけを使い、オンラインの翻訳には送らない (利用者が読み取りの画面でオンラインを選んでいても)。</summary>
public interface ITranslationService
{
    /// <summary>この PC の中の翻訳モデルが入っているか。</summary>
    bool IsAvailable { get; }

    /// <summary>外国語の文を日本語に訳す (順番は入力と同じ)。使えなければ InvalidOperationException。</summary>
    Task<IReadOnlyList<string>> TranslateToJapaneseAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

/// <summary>画像のファイル・PDF のページを、読み取れる画像 (BGRA) にする (OS の標準の部品で。外部の部品・通信なし)。</summary>
public interface IDocumentService
{
    /// <summary>読める画像の拡張子 (先頭の点なし・小文字)。</summary>
    IReadOnlyList<string> ImageExtensions { get; }

    /// <summary>画像を読む (写真の向き (EXIF) に合わせる。長い辺が maxSize を超えれば縮める)。</summary>
    Task<CapturedImage> LoadImageAsync(string path, int maxSize, CancellationToken cancellationToken);

    /// <summary>PDF のページの数。パスワードのかかった PDF・壊れた PDF は InvalidOperationException。</summary>
    Task<int> GetPdfPageCountAsync(string path, CancellationToken cancellationToken);

    /// <summary>PDF の 1 ページを画像にする (pageIndex は 0 から。白い背景。長い辺が maxSize を超えれば縮める)。</summary>
    Task<CapturedImage> RenderPdfPageAsync(string path, int pageIndex, double dpi, int maxSize, CancellationToken cancellationToken);
}

/// <summary>画面の上に重ねて出す文字 1 つ (位置は画面の物理ピクセル。この四角に収まるように GetText が文字の大きさを決め、折り返す)。</summary>
public sealed record OverlayLabel(double Left, double Top, double Right, double Bottom, string Text)
{
    /// <summary>四角が覆う元の文字の行の数 (文字の大きさの目安。折り返した文をまとめた四角なら 2 以上)。</summary>
    public int Lines { get; init; } = 1;
}

/// <summary>
/// 画面の上に文字を重ねて出す (訳など)。描くのは GetText (色・字・角はデザインのとおり)。
/// クリックは下の窓に通り、GetText の読み取りには写らない。読み取りの画面を閉じると消える。
/// </summary>
public interface IOverlayService
{
    /// <summary>出す (同じ owner の前の分は置き換える)。</summary>
    void Show(string owner, IReadOnlyList<OverlayLabel> labels);

    void Clear(string owner);
}

/// <summary>書き出しの提供元 (TXT・Markdown・JSON など)。</summary>
public interface IExportProvider
{
    string Id { get; }
    LocalizedText Name { get; }
    /// <summary>拡張子 (先頭の点なし)。</summary>
    string Extension { get; }
    Task ExportAsync(OcrExportDocument document, Stream output, CancellationToken cancellationToken);
}

/// <summary>書き出す文書 (ページごとの OCR の結果)。</summary>
public sealed record OcrExportDocument(string Title, IReadOnlyList<OcrExportPage> Pages);

public sealed record OcrExportPage(string Name, IReadOnlyList<OcrResultLine> Lines, int Width, int Height);

/// <summary>画面の取り込み (OS の正式な取り込みだけ。取り込みを禁止された窓は黒くなり、それを回避しない)。</summary>
public interface IScreenCaptureService
{
    /// <summary>取り込める画面 (モニター) と窓。</summary>
    Task<IReadOnlyList<CaptureSource>> ListSourcesAsync(CancellationToken cancellationToken);

    /// <summary>BGRA・上から。取り込めなければ null。</summary>
    Task<CapturedImage?> CaptureAsync(CaptureSource source, CancellationToken cancellationToken);
}

public enum CaptureSourceKind
{
    Monitor,
    Window,
    Region,
}

/// <summary>取り込むもの。Region は画面の物理ピクセルの範囲、Window・Monitor は Handle (OS ごとの番号)。</summary>
public sealed record CaptureSource(CaptureSourceKind Kind, string Id, string Name, int X = 0, int Y = 0, int Width = 0, int Height = 0);

public sealed record CapturedImage(byte[] Bgra, int Width, int Height, DateTimeOffset Time);

/// <summary>GetText の画面の部品を使う操作 (ファイルを選ぶ・進み具合を出す)。</summary>
public interface IPluginUi
{
    Task<IReadOnlyList<string>> PickFilesAsync(LocalizedText title, IReadOnlyList<(string Name, string[] Extensions)> filters, bool multiple, CancellationToken cancellationToken);

    Task<string?> PickFolderAsync(LocalizedText title, CancellationToken cancellationToken);

    /// <summary>保存先を選ぶ (取り消しなら null)。</summary>
    Task<string?> PickSaveFileAsync(LocalizedText title, string suggestedName, IReadOnlyList<(string Name, string[] Extensions)> filters, CancellationToken cancellationToken);

    /// <summary>画面の範囲を選んでもらい、その範囲の画像を返す (GetText の範囲の選び方。Esc でやめたら null)。</summary>
    Task<CapturedImage?> SelectScreenRegionAsync(LocalizedText title, CancellationToken cancellationToken);

    /// <summary>いくつかの中から 1 つを選んでもらう (GetText の画面で出す)。取り消しなら null。</summary>
    Task<int?> ChooseAsync(LocalizedText title, LocalizedText? message, IReadOnlyList<LocalizedText> options, CancellationToken cancellationToken);

    /// <summary>時間のかかる作業の進み具合 (GetText が帯で出し、中止のボタンを付ける)。</summary>
    IPluginProgress BeginProgress(LocalizedText title);

    /// <summary>フォルダ・ファイルを OS の画面で開く (シェルの文字列は使わない)。</summary>
    void Reveal(string path);

    /// <summary>クリップボードに文字を入れる。</summary>
    Task<bool> CopyTextAsync(string text);

    /// <summary>URL を開く (利用者が押したときだけ使うこと)。https と http だけ。</summary>
    void OpenUrl(Uri url);
}

public interface IPluginProgress : IDisposable
{
    CancellationToken CancellationToken { get; }

    /// <summary>value は 0〜1 (分からなければ null)。</summary>
    void Report(double? value, LocalizedText? detail = null);

    void Complete(LocalizedText? message = null, bool failed = false);
}
