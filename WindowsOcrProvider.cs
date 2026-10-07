using GetText.Plugins;
using Windows.Media.Ocr;

namespace GetText;

/// <summary>
/// Windows OCR を、読み取りの方式の 1 つ (IOcrProvider) として使う (拡張機能・Quick OCR 用)。
/// 読み取りの画面と同じ前処理 (OcrPipeline: 拡大・反転・多数決) を使う。日本語と英語があれば両方で読む。
/// </summary>
public sealed class WindowsOcrProvider : IOcrProvider
{
    private readonly OcrPipeline _pipeline = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OcrEngine? _engine;
    private OcrEngine? _latin;

    public string Id => "windows";
    public LocalizedText Name => "Windows OCR";
    public bool IsLocal => true;
    public bool IsAvailable => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    public async Task<GetText.Plugins.OcrResult> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_engine == null)
            {
                var langs = OcrEngine.AvailableRecognizerLanguages;
                var ja = langs.FirstOrDefault(x => x.LanguageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase));
                var en = langs.FirstOrDefault(x => x.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
                _engine = (ja ?? langs.FirstOrDefault()) is { } main ? OcrEngine.TryCreateFromLanguage(main) : OcrEngine.TryCreateFromUserProfileLanguages();
                _latin = ja != null && en != null ? OcrEngine.TryCreateFromLanguage(en) : null;
                if (_engine == null) throw new InvalidOperationException("Windows OCR の言語が入っていません (Windows の設定 → 言語 で追加できます)");
            }
            var output = await _pipeline.RecognizeAsync(_engine, _latin, bgra, width, height, 0);
            return OcrResults.From(output.Lines, Id);
        }
        finally
        {
            _gate.Release();
        }
    }
}
