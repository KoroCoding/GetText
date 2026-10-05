using System.IO;
using System.Text.Json.Nodes;

namespace GetText;

/// <summary>
/// オフライン翻訳 (offline\translate_server.py、CTranslate2)。回数制限なし。
/// </summary>
public sealed class LocalTranslator : IDisposable
{
    private readonly PythonWorker _worker = new("translate_server.py", TimeSpan.FromSeconds(90));

    public static bool IsInstalled =>
        File.Exists(PythonWorker.PythonPath)
        && File.Exists(Path.Combine(PythonWorker.Root, "models", "fugumt-en-ja", "model.bin"));

    public string? Device => _worker.ReadyInfo?["device"]?.GetValue<string>();
    public int LastMilliseconds { get; private set; }

    public Task EnsureStartedAsync()
    {
        if (!IsInstalled)
            return Task.FromException(new InvalidOperationException(
                "オフライン翻訳が未セットアップです (設定の「セットアップを実行」で入れてください)"));
        return _worker.EnsureStartedAsync();
    }

    public async Task<string[]> TranslateAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        await EnsureStartedAsync();
        var request = new JsonObject { ["texts"] = new JsonArray(texts.Select(t => (JsonNode?)t).ToArray()) };
        JsonNode response;
        try
        {
            response = await _worker.RequestAsync(request, null, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("オフライン翻訳エラー: " + ex.Message, ex);
        }
        LastMilliseconds = response["ms"]?.GetValue<int>() ?? 0;
        return response["translations"]!.AsArray().Select(t => t?.GetValue<string>() ?? "").ToArray();
    }

    public void Dispose() => _worker.Dispose();
}
