using GetText.Plugins;

namespace GetText;

/// <summary>GetText の読み取りの結果 (OcrLineData) を、拡張機能に渡す形 (OcrResult) にする。行の文字は読み取りの画面と同じつなぎ方。</summary>
public static class OcrResults
{
    public static OcrResult From(IReadOnlyList<OcrLineData> lines, string providerId, bool joinCjk = true)
    {
        var doc = OcrDocument.From(lines, joinCjk, correct: false);
        return new OcrResult(doc.Lines
            .Select(l => new OcrResultLine(l.Text, l.Left, l.Top, l.Right, l.Bottom, null,
                l.Spans?.Select(s => new OcrResultSpan(s.Text, s.Left, s.Top, s.Right, s.Bottom)).ToList()))
            .ToList(), providerId);
    }
}

/// <summary>
/// AI OCR (RapidOCR + PP-OCRv6) を、読み取りの方式の 1 つ (IOcrProvider) として使う adapter。
/// 読み取りの画面と同じ補助プロセスを使う (要求は順番に処理される)。
/// </summary>
public sealed class AiOcrProvider(AiOcr ai) : IOcrProvider
{
    public string Id => "ai";
    public LocalizedText Name => "AI OCR (PP-OCRv6)";
    public bool IsLocal => true;
    public bool IsAvailable => AiOcr.IsInstalled;

    public async Task<OcrResult> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken) =>
        OcrResults.From(await ai.RecognizeAsync(bgra, width, height, cancellationToken), Id);
}

/// <summary>この PC の中の翻訳を、拡張機能に使ってもらう adapter (読み取りの画面と同じ補助プロセス)。オンラインの翻訳には送らない。</summary>
public sealed class LocalTranslationService(LocalTranslator local) : ITranslationService
{
    public bool IsAvailable => LocalTranslator.IsInstalled;

    public async Task<IReadOnlyList<string>> TranslateToJapaneseAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (!IsAvailable) throw new InvalidOperationException("この PC の中の翻訳が入っていません (設定 → モデルとセットアップ で入れられます)");
        if (texts.Count == 0) return [];
        return await local.TranslateAsync(texts, cancellationToken);
    }
}

/// <summary>
/// 拡張機能・Quick OCR から使う読み取り (IOcrService)。GetText の方式 (OS の文字認識・AI OCR) と、拡張機能が加えた方式を選べる。
/// 方式を指定しなければ、利用者が選んだ方式 → ほかの使える方式の順。PC の外に送る方式 (IsLocal = false) は、指定されたときだけ使う。
/// </summary>
public sealed class HostOcrService(IReadOnlyList<IOcrProvider> builtIn, Func<string?> preferred) : IOcrService
{
    public IReadOnlyList<IOcrProvider> Providers =>
        [.. builtIn, .. PluginRuntime.Plugins.Where(p => p.Loaded && p.Context != null).SelectMany(p => p.Context!.OcrProviders)];

    public async Task<OcrResult> RecognizeAsync(byte[] bgra, int width, int height, string? providerId, CancellationToken cancellationToken)
    {
        if (width < 1 || height < 1 || bgra.Length < width * height * 4) throw new ArgumentException("画像の大きさが合いません");
        var providers = Providers;
        IOcrProvider? provider;
        if (providerId != null)
        {
            provider = providers.FirstOrDefault(p => p.Id == providerId) ?? throw new InvalidOperationException($"読み取りの方式「{providerId}」はありません");
            if (!provider.IsAvailable) throw new InvalidOperationException($"「{provider.Name.For("ja")}」は今は使えません (セットアップが必要な場合があります)");
        }
        else
        {
            var want = preferred();
            provider = providers.FirstOrDefault(p => p.Id == want && p.IsLocal && p.IsAvailable)
                       ?? builtIn.FirstOrDefault(p => p.IsLocal && p.IsAvailable)
                       ?? throw new InvalidOperationException("使える読み取りの方式がありません");
        }
        return await provider.RecognizeAsync(bgra, width, height, cancellationToken);
    }
}
