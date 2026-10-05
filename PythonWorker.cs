using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GetText;

/// <summary>
/// offline フォルダの Python スクリプトを常駐する子プロセスとして起動し、標準入出力で通信する。
/// 要求: JSON 1 行 (+ "len" バイトの生データ)、応答: JSON 1 行。起動完了時に {"ready": true, ...} を返す。
/// </summary>
public sealed class PythonWorker(string scriptName, TimeSpan startTimeout) : IDisposable
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "offline");

    public static string PythonPath => OperatingSystem.IsWindows()
        ? Path.Combine(Root, "venv", "Scripts", "python.exe")
        : Path.Combine(Root, "venv", "bin", "python3");

    /// <summary>Python の追加パッケージの置き場所 (入っているかの判定用)。</summary>
    public static string SitePackages => OperatingSystem.IsWindows()
        ? Path.Combine(Root, "venv", "Lib", "site-packages")
        : Path.Combine(Root, "venv", "lib", "python3.11", "site-packages");

    /// <summary>
    /// アプリに同梱した Python のスクリプト (offline) の場所。Windows は exe と同じフォルダー、
    /// Mac のアプリ (GetText.app) では Contents/Resources/offline。
    /// </summary>
    public static string ScriptDir
    {
        get
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "offline");
            var resources = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "offline"));
            return Directory.Exists(beside) || !Directory.Exists(resources) ? beside : resources;
        }
    }

    private string ScriptPath => Path.Combine(ScriptDir, scriptName);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Queue<string> _stderr = new();
    private readonly object _gate = new();
    private Process? _process;
    private Task? _starting;
    private int _nextId;

    /// <summary>起動完了時に受け取った情報 (ready 行)。</summary>
    /// <summary>
    /// 計算のスレッドが、仕事を待つ間も CPU を回し続けないようにする (OpenMP・OpenBLAS の既定の待ち方)。
    /// 速さは変わらず、正確さ優先の議事録 (CPU) で CPU 782% → 603% に減った。
    /// </summary>
    public static void QuietThreads(System.Diagnostics.ProcessStartInfo psi)
    {
        psi.Environment["OMP_WAIT_POLICY"] = "PASSIVE";
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1"; // Mac のアプリの中 (署名した場所) に __pycache__ を作らない
        psi.Environment["OPENBLAS_NUM_THREADS"] = "1"; // 話者の聞き分けの行列の計算は小さいので 1 本で足りる
    }

    public JsonNode? ReadyInfo { get; private set; }

    /// <summary>バックグラウンドで起動しておく (モデルの読み込みに時間がかかるため)。</summary>
    public Task EnsureStartedAsync()
    {
        lock (_gate)
        {
            if (_starting is { IsCompleted: false }) return _starting;
            if (_starting is { IsCompletedSuccessfully: true } && _process is { HasExited: false }) return _starting;
            if (_disposed) return Task.FromException(new ObjectDisposedException(scriptName));
            int generation = _generation; // 起動を頼んだときの番号 (この後に Dispose されたら、起動したプロセスを止める)
            _starting = Task.Run(() => StartAsync(generation));
            return _starting;
        }
    }

    private int _generation; // Dispose のたびに増やす (起動の途中で Dispose されたら、起動したプロセスを止める)

    private bool _disposed;

    private async Task StartAsync(int generation)
    {
        Stop();
        var psi = new ProcessStartInfo(PythonPath, $"-u \"{ScriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        QuietThreads(psi);

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"{scriptName} を起動できませんでした");
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_stderr)
            {
                _stderr.Enqueue(e.Data);
                while (_stderr.Count > 20) _stderr.Dequeue();
            }
        };
        process.BeginErrorReadLine();
        lock (_gate)
        {
            if (generation != _generation)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
                throw new ObjectDisposedException(scriptName, "起動の途中で止められました");
            }
            _process = process;
        }

        using var timeout = new CancellationTokenSource(startTimeout);
        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token)
                           ?? throw new InvalidOperationException("補助プロセスが終了しました: " + LastError());
                var json = JsonNode.Parse(line);
                if (json?["ready"]?.GetValue<bool>() == true)
                {
                    ReadyInfo = json;
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            StopOwn(process); // 起動に時間がかかりすぎた補助プロセスは止める (残すとメモリや GPU を使い続ける)
            throw new TimeoutException($"{scriptName} の準備が {startTimeout.TotalSeconds:0} 秒で終わりませんでした");
        }
        catch
        {
            StopOwn(process);
            throw;
        }
    }

    /// <summary>この起動で作ったプロセスだけを止める (止めている間に、別の起動が新しいプロセスを作っていることがある)。</summary>
    private void StopOwn(Process process)
    {
        bool current;
        lock (_gate) current = ReferenceEquals(_process, process);
        if (current)
        {
            Stop();
            return;
        }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }

    /// <summary>1 つの要求の応答を待つ上限。これを過ぎたら補助プロセスが固まったとみなして止め、次の要求で起動し直す。</summary>
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>要求を 1 つ送り、同じ id の応答を返す。要求は 1 本ずつ順番に処理される。</summary>
    public async Task<JsonNode> RequestAsync(JsonObject request, byte[]? payload, CancellationToken ct)
    {
        await EnsureStartedAsync();
        await _lock.WaitAsync(ct);
        try
        {
            var process = _process;
            if (process == null || process.HasExited)
                throw new InvalidOperationException("補助プロセスが終了しています: " + LastError());

            int id = ++_nextId;
            request["id"] = id;
            if (payload != null) request["len"] = payload.Length;
            var stream = process.StandardInput.BaseStream;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request.ToJsonString() + "\n"), ct);
            if (payload != null) await stream.WriteAsync(payload, ct);
            await stream.FlushAsync(ct);

            // 送信後のキャンセルでは読み取りを止めない (応答の順番がずれるため)。ただし固まったときのために上限を設ける
            using var reply = new CancellationTokenSource(ReplyTimeout);
            while (true)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(reply.Token);
                }
                catch (OperationCanceledException)
                {
                    lock (_gate)
                    {
                        Stop();
                        _starting = null; // 次の要求で起動し直す
                    }
                    throw new TimeoutException($"{scriptName} が {ReplyTimeout.TotalSeconds:0} 秒応答しないので、起動し直します");
                }
                if (line == null) throw new InvalidOperationException("補助プロセスが終了しました: " + LastError());
                var json = JsonNode.Parse(line);
                if (json?["id"]?.GetValue<int>() != id) continue;
                if (json["error"] is JsonNode error) throw new InvalidOperationException(error.GetValue<string>());
                return json;
            }
        }
        catch
        {
            if (_process is { HasExited: true }) lock (_gate) _starting = null; // 次回は起動し直す
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private string LastError()
    {
        lock (_stderr)
            return _stderr.Count == 0 ? "(詳細なし)" : string.Join(" / ", _stderr.TakeLast(3));
    }

    private void Stop()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 既に終了している
        }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _generation++;
            _disposed = true;
        }
        Stop();
    }

    /// <summary>補助プロセスを止めてメモリを空ける (Dispose と違い、次に使うときに起動し直せる)。</summary>
    public void Release()
    {
        lock (_gate)
        {
            _generation++; // (起動の途中なら、起動したプロセスを止める)
            _starting = null;
            ReadyInfo = null;
        }
        Stop();
    }

    /// <summary>補助プロセスを起動している・起動の途中か。</summary>
    public bool IsStarted
    {
        get
        {
            lock (_gate) return _starting is { IsCompleted: false } || _process is { HasExited: false };
        }
    }
}
