using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GetText;

/// <summary>補助プログラム (helper/GetTextHelper.swift) が返したエラー。</summary>
public sealed class MacHelperException(string message) : Exception(message);

/// <summary>
/// Mac の機能を受け持つ補助プログラム (GetText.app/Contents/MacOS/GetTextHelper) との通信。
/// 要求は JSON 1 行で送り、同じ id の応答を待つ。音やショートカットの通知は <see cref="EventReceived"/> で届く (読み取り用のスレッドから)。
/// 補助プログラムが終わってしまったら、次の要求のときに起動し直す。
/// </summary>
public sealed class MacHelper : IDisposable
{
    public static MacHelper Instance { get; } = new();

    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "GetTextHelper");

    /// <summary>Mac で、補助プログラムがアプリに入っているか (Windows で画面を試すときは false)。</summary>
    public static bool IsAvailable => OperatingSystem.IsMacOS() && File.Exists(HelperPath);

    public event Action<JsonObject>? EventReceived;

    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = [];
    private Process? _process;
    private int _nextId;
    private bool _disposed;

    private Process EnsureStarted()
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MacHelper));
            if (_process is { HasExited: false }) return _process;
            if (!IsAvailable) throw new MacHelperException("Mac の補助プログラム (GetTextHelper) が見つかりません");
            var psi = new ProcessStartInfo(HelperPath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var process = Process.Start(psi) ?? throw new MacHelperException("補助プログラムを起動できませんでした");
            process.StandardInput.AutoFlush = true;
            _process = process;
            new Thread(() => ReadLoop(process)) { IsBackground = true, Name = "MacHelper" }.Start();
            new Thread(() =>
            {
                try
                {
                    while (process.StandardError.ReadLine() is { } line) App.Log("MacHelper", new Exception(line));
                }
                catch
                {
                    // 終わった
                }
            }) { IsBackground = true, Name = "MacHelperErr" }.Start();
            return process;
        }
    }

    private void ReadLoop(Process process)
    {
        try
        {
            while (process.StandardOutput.ReadLine() is { } line)
            {
                JsonObject? json;
                try
                {
                    json = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }
                if (json == null) continue;
                if (json["event"] is not null)
                {
                    try
                    {
                        EventReceived?.Invoke(json);
                    }
                    catch (Exception ex)
                    {
                        App.Log("MacHelperEvent", ex);
                    }
                    continue;
                }
                if (json["id"]?.GetValueKind() != JsonValueKind.Number) continue;
                int id = json["id"]!.GetValue<int>();
                TaskCompletionSource<JsonObject>? tcs;
                lock (_gate)
                {
                    if (_pending.Remove(id, out tcs) == false) continue;
                }
                if (json["ok"]?.GetValue<bool>() == false)
                    tcs!.TrySetException(new MacHelperException(json["error"]?.GetValue<string>() ?? "補助プログラムのエラー"));
                else
                    tcs!.TrySetResult(json);
            }
        }
        catch (Exception ex)
        {
            App.Log("MacHelperRead", ex);
        }
        // 終わった: 待っている要求はすべて失敗にする
        List<TaskCompletionSource<JsonObject>> waiting;
        lock (_gate)
        {
            waiting = [.. _pending.Values];
            _pending.Clear();
            if (_process == process) _process = null;
        }
        foreach (var t in waiting) t.TrySetException(new MacHelperException("補助プログラムが終了しました"));
        // 落ちたことを知らせる (音の取り込みを止める・ショートカットを登録し直すため。次の要求で起動し直す)
        bool disposed;
        lock (_gate) disposed = _disposed;
        if (!disposed)
        {
            try
            {
                EventReceived?.Invoke(new JsonObject { ["event"] = "helper_exited" });
            }
            catch (Exception ex)
            {
                App.Log("MacHelperEvent", ex);
            }
        }
    }

    /// <summary>要求を送って応答を待つ (timeout を過ぎたら失敗)。</summary>
    public async Task<JsonObject> RequestAsync(string cmd, JsonObject? args = null, TimeSpan? timeout = null)
    {
        var process = EnsureStarted();
        var request = args ?? new JsonObject();
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        int id;
        lock (_gate)
        {
            id = ++_nextId;
            _pending[id] = tcs;
        }
        request["id"] = id;
        request["cmd"] = cmd;
        try
        {
            lock (_gate) process.StandardInput.WriteLine(request.ToJsonString());
        }
        catch (Exception ex)
        {
            lock (_gate) _pending.Remove(id);
            throw new MacHelperException("補助プログラムに送れませんでした: " + ex.Message);
        }
        var wait = timeout ?? TimeSpan.FromSeconds(30);
        if (await Task.WhenAny(tcs.Task, Task.Delay(wait)) != tcs.Task)
        {
            lock (_gate) _pending.Remove(id);
            throw new MacHelperException($"補助プログラムが応答しません ({cmd})");
        }
        return await tcs.Task;
    }

    /// <summary>応答を待つ (UI スレッドから同期で呼ぶとき用。応答は別のスレッドで受け取るので固まらない)。</summary>
    public JsonObject Request(string cmd, JsonObject? args = null, TimeSpan? timeout = null) =>
        Task.Run(() => RequestAsync(cmd, args, timeout)).GetAwaiter().GetResult();

    private static int _lastToken = Environment.TickCount & 0xFFFFF;

    /// <summary>録画の番号 (画面の録画と議事録の録画を取り違えないよう、GetText が決めて補助プログラムに渡す)。</summary>
    public static int NewRecordingToken() => Interlocked.Increment(ref _lastToken);

    public void Dispose()
    {
        Process? process;
        lock (_gate)
        {
            _disposed = true;
            process = _process;
            _process = null;
        }
        // 待つ間も補助プログラムの出力は読み続ける (ロックを持ったまま待つと、読む処理が止まり、補助プログラムの書き込みも止まる)
        try
        {
            process?.StandardInput.Close(); // 標準入力を閉じると補助プログラムは終わる
            // 録画中なら書き終えてから終わる (補助プログラムは最大 10 秒待つ。何もなければすぐ終わる)。途中で止めると録画のファイルが壊れる
            if (process is { } p && !p.WaitForExit(15000)) p.Kill();
        }
        catch
        {
            // 終わっている
        }
    }
}
