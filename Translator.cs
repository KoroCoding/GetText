using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GetText;

public enum TranslationEngine
{
    Google,
    DeepL,
    Local,
}

public sealed class TranslationBlockedException(string message, TimeSpan retryAfter) : Exception(message)
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// 日本語への翻訳。翻訳済みの文はキャッシュし、キャッシュにない文だけをまとめて 1 回で送る。
/// </summary>
public sealed class Translator
{
    private const int CacheLimit = 5000;

    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly GoogleFreeTranslator _google = new();
    private readonly DeepLTranslator _deepl = new();
    private readonly LocalTranslator _local = new();

    /// <summary>この PC の中の翻訳 (拡張機能にも、同じ補助プロセスで使ってもらう)。</summary>
    public LocalTranslator Local => _local;

    public TranslationEngine Engine { get; set; } = TranslationEngine.Google;
    public string? DeepLKey { get => _deepl.ApiKey; set => _deepl.ApiKey = value; }
    public string TargetLanguage { get; set; } = "ja";

    /// <summary>連続リクエストの最小間隔。無料エンドポイントは短時間に送りすぎると制限される。</summary>
    public TimeSpan MinInterval => Engine switch
    {
        TranslationEngine.Google => TimeSpan.FromMilliseconds(900),
        TranslationEngine.DeepL => TimeSpan.FromMilliseconds(250),
        _ => TimeSpan.Zero, // ローカルは制限なし
    };

    public string EngineName => Engine switch
    {
        TranslationEngine.Google => "Google 翻訳",
        TranslationEngine.DeepL => "DeepL",
        _ => _local.Device == null ? "ローカル"
            : $"ローカル ({(_local.Device == "cuda" ? "GPU" : "CPU")}{(_local.LastMilliseconds > 0 ? $" ・ {_local.LastMilliseconds} ms" : "")})",
    };

    /// <summary>
    /// 最初の翻訳を速くするための準備。ローカルはモデルを読み込み、オンラインは TLS 接続を張る(翻訳内容は送らない)。
    /// </summary>
    public void WarmUp()
    {
        if (Engine == TranslationEngine.Local)
        {
            _ = _local.EnsureStartedAsync().ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return;
        }
        var url = Engine == TranslationEngine.Google ? "https://translate.googleapis.com/" : _deepl.BaseUrl;
        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var _ = await Http.SendAsync(req);
            }
            catch
            {
                // オフラインなどは実際の翻訳時にエラー表示する
            }
        });
    }

    public static string Key(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public bool TryGetCached(string text, out string translated) => _cache.TryGetValue(Key(text), out translated!);

    /// <summary>キャッシュにない文だけを翻訳してキャッシュに入れる。</summary>
    public async Task TranslateMissingAsync(IEnumerable<string> texts, CancellationToken ct)
    {
        var missing = texts.Select(Key).Where(k => k.Length > 0 && !_cache.ContainsKey(k)).Distinct().ToList();
        if (missing.Count == 0) return;
        int generation = _generation;

        string[] translated = Engine switch
        {
            TranslationEngine.DeepL => await _deepl.TranslateAsync(missing, TargetLanguage, ct),
            TranslationEngine.Local => await _local.TranslateAsync(missing, ct),
            _ => await _google.TranslateAsync(missing, TargetLanguage, ct),
        };

        // 訳している間に翻訳エンジンが切り替わっていたら (キャッシュを消した後なら)、古いエンジンの訳は入れない
        if (generation != _generation) return;
        if (_cache.Count > CacheLimit) _cache.Clear();
        for (int i = 0; i < missing.Count; i++) _cache[missing[i]] = translated[i];
    }

    private volatile int _generation;

    public void ClearCache()
    {
        _generation++;
        _cache.Clear();
    }

    private readonly ConcurrentDictionary<string, string> _offlineCache = new();

    /// <summary>PC 内の翻訳モデルが入っているか。</summary>
    public static bool OfflineAvailable => LocalTranslator.IsInstalled;

    /// <summary>
    /// 議事録用: 翻訳の設定にかかわらず PC 内のモデルで日本語に訳す (会議の内容を外部に送らないため)。
    /// 読み取り画面の翻訳と同じ翻訳プロセスを使う (依頼は順番に処理される)。
    /// </summary>
    public async Task<string[]> TranslateOfflineAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var keys = texts.Select(Key).ToList();
        var missing = keys.Where(k => k.Length > 0 && !_offlineCache.ContainsKey(k)).Distinct().ToList();
        if (missing.Count > 0)
        {
            var translated = await _local.TranslateAsync(missing, ct);
            if (_offlineCache.Count > CacheLimit) _offlineCache.Clear();
            for (int i = 0; i < missing.Count; i++) _offlineCache[missing[i]] = translated[i];
        }
        return keys.Select(k => k.Length == 0 ? "" : _offlineCache.GetValueOrDefault(k, "")).ToArray();
    }

    public void Shutdown() => _local.Dispose();
}

/// <summary>Google 翻訳の API キー不要エンドポイント。制限されたら予備のエンドポイントに切り替える。</summary>
internal sealed class GoogleFreeTranslator
{
    private const string Primary = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t&sl=auto&tl=";
    private const string Fallback = "https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl=";

    private DateTime _primaryBlockedUntil = DateTime.MinValue;
    private DateTime _allBlockedUntil = DateTime.MinValue;

    public async Task<string[]> TranslateAsync(IReadOnlyList<string> texts, string target, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now < _allBlockedUntil)
            throw new TranslationBlockedException("Google 翻訳から一時的に制限されています", _allBlockedUntil - now);

        if (now >= _primaryBlockedUntil)
        {
            try
            {
                return await PrimaryAsync(texts, target, ct);
            }
            catch (TranslationBlockedException)
            {
                _primaryBlockedUntil = DateTime.UtcNow.AddMinutes(10);
            }
        }

        try
        {
            return await FallbackAsync(texts, target, ct);
        }
        catch (TranslationBlockedException)
        {
            _allBlockedUntil = DateTime.UtcNow.AddSeconds(60);
            throw new TranslationBlockedException("Google 翻訳から一時的に制限されています (1 分後に再試行)", TimeSpan.FromSeconds(60));
        }
    }

    // 改行区切りでまとめて 1 リクエスト。行数が合わないときだけ 1 文ずつ送り直す
    private async Task<string[]> PrimaryAsync(IReadOnlyList<string> texts, string target, CancellationToken ct)
    {
        if (texts.Count > 1)
        {
            var parts = (await PrimaryOneAsync(string.Join("\n", texts), target, ct)).Split('\n');
            if (parts.Length == texts.Count) return parts.Select(p => p.Trim()).ToArray();
        }
        var results = new string[texts.Count];
        for (int i = 0; i < texts.Count; i++) results[i] = await PrimaryOneAsync(texts[i], target, ct);
        return results;
    }

    private static async Task<string> PrimaryOneAsync(string text, string target, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", text)]);
        using var response = await Translator.Http.PostAsync(Primary + target, content, ct);
        using var json = await ReadJsonAsync(response, ct);
        var sb = new StringBuilder();
        foreach (var segment in json.RootElement[0].EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array && segment[0].ValueKind == JsonValueKind.String)
                sb.Append(segment[0].GetString());
        }
        return sb.ToString();
    }

    // q を複数付けると文ごとの配列で返ってくる: [["訳","en"], ...]
    private static async Task<string[]> FallbackAsync(IReadOnlyList<string> texts, string target, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(texts.Select(t => new KeyValuePair<string, string>("q", t)));
        using var response = await Translator.Http.PostAsync(Fallback + target, content, ct);
        using var json = await ReadJsonAsync(response, ct);
        var items = json.RootElement.EnumerateArray().Select(e =>
            e.ValueKind == JsonValueKind.Array ? e[0].GetString() ?? "" : e.GetString() ?? "").ToArray();
        if (items.Length != texts.Count) throw new InvalidOperationException("翻訳結果の形式が想定と異なります");
        return items;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        // 制限されると 429 や「Sorry...」の HTML ページが返る
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
            || mediaType.Contains("html"))
            throw new TranslationBlockedException("Google 翻訳から一時的に制限されています", TimeSpan.FromSeconds(60));
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}

/// <summary>DeepL API (無料キーは末尾が ":fx")。</summary>
internal sealed class DeepLTranslator
{
    public string? ApiKey { get; set; }

    public string BaseUrl => ApiKey?.EndsWith(":fx", StringComparison.Ordinal) == true
        ? "https://api-free.deepl.com/"
        : "https://api.deepl.com/";

    private sealed record Request(string[] text, string target_lang);
    private sealed record Item(string text);
    private sealed record Response(Item[] translations);

    public async Task<string[]> TranslateAsync(IReadOnlyList<string> texts, string target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("DeepL の API キーが設定されていません (「キー設定」から入力)");

        var results = new List<string>(texts.Count);
        foreach (var chunk in texts.Chunk(50)) // 1 リクエスト最大 50 文
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "v2/translate")
            {
                Content = JsonContent.Create(new Request(chunk, target.ToUpperInvariant())),
            };
            request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + ApiKey);
            using var response = await Translator.Http.SendAsync(request, ct);

            switch (response.StatusCode)
            {
                case HttpStatusCode.Forbidden:
                    throw new InvalidOperationException("DeepL の API キーが正しくありません");
                case (HttpStatusCode)456:
                    throw new InvalidOperationException("DeepL の今月の翻訳文字数の上限に達しました");
                case HttpStatusCode.TooManyRequests:
                    throw new TranslationBlockedException("DeepL へのリクエストが多すぎます", TimeSpan.FromSeconds(5));
            }
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<Response>(ct)
                       ?? throw new InvalidOperationException("DeepL の応答が空です");
            results.AddRange(body.translations.Select(t => t.text));
        }
        return results.ToArray();
    }
}
