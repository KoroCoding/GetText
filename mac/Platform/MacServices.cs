using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace GetText;

/// <summary>取り込み元に選べるウィンドウ (アプリごとに 1 つ)。</summary>
public sealed record WindowInfo(long Handle, string Title, int ProcessId, string ProcessName, bool IsPlaying = false, int Area = 0)
{
    /// <summary>「GetText 以外のすべての音」を表す特別な項目。</summary>
    public static readonly WindowInfo AllAudio = new(0, "すべての音 (GetText 以外)", 0, "", false);

    public bool IsAll => ReferenceEquals(this, AllAudio);

    public override string ToString() =>
        IsAll ? "🔈 " + Title : $"{(IsPlaying ? "🔊 " : "　 ")}{Title}  —  {ProcessName}";
}

/// <summary>Mac の機能 (補助プログラム経由) を、共有のコードに登録する。</summary>
internal static class MacServices
{
    [ModuleInitializer]
    internal static void Register()
    {
        if (!OperatingSystem.IsMacOS()) return; // Windows で画面を試すときは何も登録しない
        AudioSources.Window = (pid, exclude) => new MacAudioSource(pid, exclude);
        AudioSources.Microphone = () => new MacAudioSource(null, false);
        AppSettings.SecretStore = new MacKeychain();
    }

    /// <summary>表示中のウィンドウ (音を出しているものを先に)。補助プログラムが無いときは空。</summary>
    public static List<WindowInfo> GetWindows()
    {
        if (!MacHelper.IsAvailable) return [];
        try
        {
            var reply = MacHelper.Instance.Request("windows", timeout: TimeSpan.FromSeconds(5));
            return reply["windows"]!.AsArray()
                .Select(w => new WindowInfo(
                    w!["id"]!.GetValue<long>(), w["title"]!.GetValue<string>(), w["pid"]!.GetValue<int>(),
                    w["app"]!.GetValue<string>(), w["playing"]!.GetValue<bool>(), w["area"]!.GetValue<int>()))
                .OrderByDescending(w => w.IsPlaying)
                .ThenByDescending(w => w.Area)
                .ToList();
        }
        catch (Exception ex)
        {
            App.Log("MacWindows", ex);
            return [];
        }
    }

    /// <summary>画面収録 (と音の取り込み)・マイクの許可の状態。</summary>
    public static async Task<(bool Screen, string Mic)> PermissionsAsync()
    {
        if (!MacHelper.IsAvailable) return (false, "unavailable");
        var reply = await MacHelper.Instance.RequestAsync("permissions");
        return (reply["screen"]!.GetValue<bool>(), reply["mic"]!.GetValue<string>());
    }
}

/// <summary>アプリの音・すべての音・マイクを、補助プログラムで取り込む (16kHz・モノラル・16bit、無音も補われて届く)。</summary>
public sealed class MacAudioSource : IAudioSource
{
    private static int _next;
    private readonly string _stream;
    private readonly int? _pid;
    private readonly bool _exclude;
    private bool _started;

    public event Action<byte[]>? DataAvailable;
    public event Action<Exception>? Failed;

    /// <param name="pid">取り込むアプリ (null ならマイク)</param>
    /// <param name="exclude">true なら、そのアプリ以外のすべての音</param>
    public MacAudioSource(int? pid, bool exclude)
    {
        _pid = pid;
        _exclude = exclude;
        _stream = (pid == null ? "mic" : "win") + "-" + Interlocked.Increment(ref _next);
    }

    public void Start()
    {
        if (_started) return;
        MacHelper.Instance.EventReceived += OnEvent;
        var args = new JsonObject { ["stream"] = _stream };
        if (_pid == null) args["mic"] = true;
        else
        {
            // 「すべての音」は GetText 自身 (このプロセス) を除く
            args["pid"] = _pid.Value;
            args["exclude"] = _exclude;
        }
        try
        {
            MacHelper.Instance.Request("audio_start", args, TimeSpan.FromSeconds(20));
            _started = true;
        }
        catch
        {
            MacHelper.Instance.EventReceived -= OnEvent;
            // 待ちきれなかったときは、後から始まった取り込みが残らないよう止めておく
            try { MacHelper.Instance.Request("audio_stop", new JsonObject { ["stream"] = _stream }, TimeSpan.FromSeconds(5)); } catch { }
            throw;
        }
    }

    private void OnEvent(JsonObject json)
    {
        if (json["event"]?.GetValue<string>() == "helper_exited")
        {
            // 補助プログラムが落ちると、この取り込みも止まる (「記録中」のまま音が来ない状態にしない)
            if (_started) Failed?.Invoke(new MacHelperException("音を取り込む補助プログラムが止まりました"));
            return;
        }
        if (json["stream"]?.GetValue<string>() != _stream) return;
        switch (json["event"]!.GetValue<string>())
        {
            case "audio":
                DataAvailable?.Invoke(Convert.FromBase64String(json["data"]!.GetValue<string>()));
                break;
            case "audio_error":
                Failed?.Invoke(new MacHelperException(json["message"]?.GetValue<string>() ?? "音を取り込めなくなりました"));
                break;
        }
    }

    public void Dispose()
    {
        MacHelper.Instance.EventReceived -= OnEvent;
        if (!_started) return;
        _started = false;
        try
        {
            MacHelper.Instance.Request("audio_stop", new JsonObject { ["stream"] = _stream }, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            App.Log("MacAudioStop", ex);
        }
    }
}

/// <summary>DeepL のキーなどをキーチェーンに置く。</summary>
public sealed class MacKeychain : ISecretStore
{
    public string? Get(string name)
    {
        try
        {
            return MacHelper.Instance.Request("secret_get", new JsonObject { ["name"] = name })["value"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            App.Log("Keychain", ex);
            return null;
        }
    }

    public void Set(string name, string value) =>
        MacHelper.Instance.Request("secret_set", new JsonObject { ["name"] = name, ["value"] = value });

    public void Delete(string name) =>
        MacHelper.Instance.Request("secret_delete", new JsonObject { ["name"] = name });
}
