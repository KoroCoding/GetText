using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GetText;

/// <summary>
/// 文字起こしの 1 発言。Start/End は録音開始からの秒数。Speaker は 0 = 自分 (マイク)、1 以降 = 話者番号。
/// Lang は話された言語 (ja, en など)。
/// </summary>
public sealed record TranscriptSegment(string Source, double Start, double End, int Speaker, string Text, int Id = 0, string Lang = "ja")
{
    /// <summary>この発言の記録が始まった時刻 (Start/End の基準)。分からなければ null (今の記録の時刻を使う)。</summary>
    public DateTime? SessionStart { get; init; }
}

/// <summary>
/// 発言の補正。Ids の発言を 1 つにまとめて Text に置き換える。
/// Stage は "asr" (まとめて認識し直した) か "llm" (前後の文脈で聞き間違いを直した)。
/// </summary>
public sealed record TranscriptRevision(IReadOnlyList<int> Ids, string Text, string Stage, string Before);

/// <summary>
/// 選んだアプリの音声 (とマイク) を取り込み、offline\transcribe_server.py に流して文字起こしの結果を受け取る。
/// 結果はサーバーから届いた順に <see cref="SegmentReceived"/> で通知する (取り込み用のスレッドから呼ばれる)。
/// </summary>
public sealed class TranscriptionService : IDisposable
{
    private const int SendChunkBytes = AudioSources.SampleRate * 2 / 5; // 0.2 秒ごとに送る (表示を早くするため)

    /// <summary>英語など日本語以外の音声認識モデル (Whisper large-v3 turbo) が入っているか。</summary>
    public static bool IsMultilingualInstalled =>
        File.Exists(Path.Combine(PythonWorker.Root, "models", "whisper-large-v3-turbo", "model.bin"));

    public static bool IsInstalled =>
        File.Exists(PythonWorker.PythonPath)
        && (File.Exists(Path.Combine(PythonWorker.Root, "models", "kotoba-whisper-v2.0", "model.bin")) || IsFastInstalled)
        && File.Exists(Path.Combine(PythonWorker.Root, "models", "speaker-campplus", "model.onnx"));

    /// <summary>GPU の無い PC 向けの速い日本語の音声認識 (ReazonSpeech) が入っているか。</summary>
    public static bool IsFastInstalled =>
        new[] { "encoder-epoch-99-avg-1.int8.onnx", "decoder-epoch-99-avg-1.int8.onnx", "joiner-epoch-99-avg-1.int8.onnx", "tokens.txt" }
            .All(name => File.Exists(Path.Combine(PythonWorker.Root, "models", "reazonspeech-k2-v2", name)));

    /// <summary>起動するときの速さ ("auto" / "light" / "accurate")。起動後は <see cref="SetSpeed"/> で変える。</summary>
    /// <summary>今の処理を起動したときの速さ (準備中に変えたら、準備ができてから送り直すため)。</summary>
    public string InitialSpeedUsed { get; private set; } = "auto";

    public string InitialSpeed { get; set; } = Environment.GetEnvironmentVariable("GETTEXT_SPEED") ?? "auto"; // 動作確認では環境変数で選べる

    /// <summary>いまの速さ ("light" / "accurate")、GPU があるか、速いモデルが入っているか。</summary>
    public event Action<string, bool, bool>? SpeedStatus;

    public event Action<TranscriptSegment>? SegmentReceived;
    public event Action<TranscriptRevision>? Revised;
    /// <summary>全体を分け直した結果、話者が変わった以前の発言 {発言の番号: 新しい話者番号}。</summary>
    public event Action<IReadOnlyDictionary<int, int>>? Respeaker;
    /// <summary>話している途中の暫定の文字 (source, speaker, text)。text が空なら消す。</summary>
    public event Action<string, int, string>? Partial;
    /// <summary>文脈補正の LLM の状態: loading / gpu / cpu / unavailable / off。</summary>
    public event Action<string>? LlmStatus;
    /// <summary>処理が音声に追いついていない秒数 (追いついたら 0)。</summary>
    public event Action<double>? Lag;
    /// <summary>ファイルの文字起こしの進み具合 (処理済みの秒数, 全体の秒数)。</summary>
    public event Action<double, double>? Progress;
    /// <summary>文脈補正が最後の発言まで終わった。</summary>
    public event Action? Refined;
    /// <summary>英語などの音声認識モデルの状態: loading / ready / missing。</summary>
    public event Action<string>? MultilingualStatus;
    /// <summary>言語が日本語の設定なのに、日本語以外が話されているらしい。</summary>
    public event Action? ForeignSpeech;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "logs", "minutes.log");
    public event Action<string>? Error;
    /// <summary>音量 (0〜1)。ウィンドウ側・マイク側。</summary>
    public event Action<string, double>? Level;

    /// <summary>記録中に、ウィンドウかマイクの音を取り込めなくなった (もう一方は続ける)。</summary>
    public event Action<string>? CaptureLost;

    /// <summary>覚えた声に似た話者が見つかった (話者の番号, 名前)。名前が null なら、前に知らせた名前の取り消し。</summary>
    public event Action<int, string?>? VoiceMatch;

    /// <summary>覚えた声の人数が変わった。</summary>
    public event Action<int>? VoicesChanged;

    /// <summary>起動中に読み込んでいるモデル ("asr" = 日本語の音声認識、"speakers" = 話者の聞き分け)。</summary>
    public event Action<string>? LoadingStep;

    /// <summary>起動が終わって、すぐに記録・文字起こしできる。</summary>
    public bool IsReady
    {
        get
        {
            try
            {
                return _starting is { IsCompletedSuccessfully: true } && _process is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false; // ちょうど別のスレッドで止めたところ
            }
        }
    }

    private readonly object _writeLock = new();
    private Process? _process;
    private Task? _starting;
    private IAudioSource? _window;
    private IAudioSource? _mic;
    private readonly Dictionary<string, MemoryStream> _pending = new() { ["win"] = new(), ["mic"] = new() };
    private TaskCompletionSource? _flushed;
    private int _flushSeq;
    private TaskCompletionSource<(double Duration, bool Cancelled)>? _fileDone;
    private TaskCompletionSource<string?>? _summary;
    private SessionRecorder? _recorder;
    private readonly Dictionary<string, TaskCompletionSource> _encoding = [];

    /// <summary>次の記録の音声を保存する WAV の場所 (null なら保存しない)。記録を始めるときに使う。</summary>
    public string? RecordAudioPath { get; set; }

    /// <summary>最後に止めた記録の音声 (保存していなければ null)。</summary>
    public SessionRecorder? LastRecording { get; private set; }
    private int _summaryJob;
    private Action<int, int>? _summaryProgress;

    public string? Device { get; private set; }

    /// <summary>いまの速さ ("light" = 速い日本語の音声認識 / "accurate" = kotoba-whisper)。</summary>
    public string Speed { get; private set; } = "accurate";

    /// <summary>NVIDIA の GPU が使えるか。</summary>
    public bool HasGpu { get; private set; }

    /// <summary>今回の録音を始めた時刻。発言の Start/End はここからの秒数。</summary>
    public DateTime SessionStart { get; private set; }

    // 記録の番号 → 始まった時刻 (発言はどの記録のものかを番号で返してくる)
    private readonly Dictionary<int, DateTime> _sessionStarts = [];
    private int _session;

    /// <summary>新しい記録の番号を決めて、始まった時刻を覚える (Python に送る reset / restart / file に付ける)。</summary>
    private int BeginSession()
    {
        SessionStart = DateTime.Now;
        lock (_sessionStarts)
        {
            _sessionStarts[++_session] = SessionStart;
            _sessionStarts.Remove(_session - 20); // 古い記録は覚えておかない
            return _session;
        }
    }
    public bool IsRecording => _window != null || _mic != null;
    public bool IsTranscribingFile => _fileDone is { Task.IsCompleted: false };

    /// <summary>文字起こしできるファイルの種類 (PyAV / FFmpeg で読めるもの)。</summary>
    public const string FileFilter =
        "動画・音声ファイル|*.mp4;*.m4a;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.mp3;*.wav;*.flac;*.ogg;*.opus;*.aac;*.wma|すべてのファイル|*.*";

    public Task EnsureStartedAsync()
    {
        // 閉じた議事録の画面から、後になって処理を起動し直さない
        if (_disposed) return Task.FromException(new ObjectDisposedException(nameof(TranscriptionService)));
        if (!IsInstalled)
            return Task.FromException(new InvalidOperationException(
                "議事録の音声認識が未セットアップです (設定の「セットアップを実行」で入れてください)"));
        lock (_writeLock)
        {
            if (_starting is { IsCompleted: false }) return _starting;
            if (_starting is { IsCompletedSuccessfully: true } && _process is { HasExited: false }) return _starting;
            _starting = Task.Run(StartProcessAsync);
            return _starting;
        }
    }

    private async Task StartProcessAsync()
    {
        var script = Path.Combine(PythonWorker.ScriptDir, "transcribe_server.py");
        var psi = new ProcessStartInfo(PythonWorker.PythonPath, $"-u \"{script}\"")
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
        PythonWorker.QuietThreads(psi);
        InitialSpeedUsed = InitialSpeed;
        psi.Environment["GETTEXT_SPEED"] = InitialSpeed; // 使わない方の音声認識モデルは読み込まない (起動が速く、メモリも少なくて済む)
        if (_process is { HasExited: true }) KillProcess(); // 落ちた前の処理の後始末
        var process = Process.Start(psi) ?? throw new InvalidOperationException("文字起こしのプロセスを起動できませんでした");
        _process = process; // 準備中に Dispose されても止められるように
        if (_disposed)
        {
            KillProcess();
            throw new ObjectDisposedException(nameof(TranscriptionService));
        }
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderr) stderr.AppendLine(e.Data);
            WriteLog(e.Data); // 動作の記録 (話した内容は含まれない)
        };
        process.BeginErrorReadLine();

        // 遅い PC (HDD・メモリが少ない) では初回の読み込みに時間がかかるので、3 分まで待つ
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line == null)
                {
                    string detail;
                    lock (stderr) detail = stderr.ToString().Trim().Split('\n').LastOrDefault() ?? "";
                    throw new InvalidOperationException("文字起こしのプロセスが終了しました: " + detail);
                }
                var json = JsonNode.Parse(line);
                if (json?["ready"]?.GetValue<bool>() == true)
                {
                    Device = json["device"]?.GetValue<string>();
                    Speed = json["speed"]?.GetValue<string>() ?? "accurate";
                    HasGpu = json["gpu"]?.GetValue<bool>() ?? Device == "cuda";
                    break;
                }
                if (json?["event"]?.GetValue<string>() == "loading" && json["step"]?.GetValue<string>() is { } step)
                    LoadingStep?.Invoke(step);
            }
        }
        catch (OperationCanceledException)
        {
            KillProcess(process); // 止めずに次を起動すると、2 つ目のプロセスがメモリを二重に使う
            throw new InvalidOperationException("音声認識の準備が 3 分たっても終わりませんでした (PC の負荷が高いか、モデルのファイルが壊れている可能性があります)");
        }
        catch
        {
            KillProcess(process);
            throw;
        }
        if (_disposed) throw new ObjectDisposedException(nameof(TranscriptionService));
        _ = Task.Run(() => ReadEvents(process));
    }

    private bool _disposed;

    /// <summary>文字起こしの処理が途中で終わった (落ちた)。記録中なら止めて知らせる。</summary>
    public event Action? ProcessExited;

    private async Task ReadEvents(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                JsonNode? json;
                try
                {
                    json = JsonNode.Parse(line);
                }
                catch (System.Text.Json.JsonException)
                {
                    WriteLog("[app] 読めない行を飛ばしました");
                    continue;
                }
                try
                {
                switch (json?["event"]?.GetValue<string>())
                {
                    case "segment":
                        DateTime? sessionStart = null;
                        if (json["session"]?.GetValue<int>() is int session)
                            lock (_sessionStarts) sessionStart = _sessionStarts.TryGetValue(session, out var at) ? at : null;
                        SegmentReceived?.Invoke(new TranscriptSegment(
                            json["src"]!.GetValue<string>(), json["start"]!.GetValue<double>(), json["end"]!.GetValue<double>(),
                            json["speaker"]!.GetValue<int>(), json["text"]!.GetValue<string>(), json["id"]?.GetValue<int>() ?? 0,
                            json["lang"]?.GetValue<string>() ?? "ja") { SessionStart = sessionStart });
                        break;
                    case "revise":
                        Revised?.Invoke(new TranscriptRevision(
                            json["ids"]!.AsArray().Select(i => i!.GetValue<int>()).ToArray(), json["text"]!.GetValue<string>(),
                            json["stage"]?.GetValue<string>() ?? "", json["before"]?.GetValue<string>() ?? ""));
                        break;
                    case "partial":
                        Partial?.Invoke(json["src"]!.GetValue<string>(), json["speaker"]?.GetValue<int>() ?? -1,
                            json["text"]?.GetValue<string>() ?? "");
                        break;
                    case "respeaker":
                        Respeaker?.Invoke(json["speakers"]!.AsObject()
                            .ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value!.GetValue<int>()));
                        break;
                    case "status":
                        if (json["llm"]?.GetValue<string>() is { } llm) LlmStatus?.Invoke(llm);
                        if (json["lag"] is { } lag) Lag?.Invoke(lag.GetValue<double>());
                        if (json["multilingual"]?.GetValue<string>() is { } multi) MultilingualStatus?.Invoke(multi);
                        if (json["speed"]?.GetValue<string>() is { } speed)
                        {
                            Speed = speed;
                            SpeedStatus?.Invoke(speed, json["gpu"]?.GetValue<bool>() ?? false, json["fast"]?.GetValue<bool>() ?? false);
                        }
                        if (json["foreign"]?.GetValue<bool>() == true) ForeignSpeech?.Invoke();
                        break;
                    case "flushed":
                        // 前の停止の遅れた返事で、今回の停止が早く終わらないように番号を確かめる
                        if ((json["seq"]?.GetValue<int>() ?? _flushSeq) == _flushSeq) _flushed?.TrySetResult();
                        break;
                    case "progress":
                        Progress?.Invoke(json["done"]!.GetValue<double>(), json["total"]!.GetValue<double>());
                        break;
                    case "file_done":
                        if (json["error"]?.GetValue<string>() is { } fileError)
                            _fileDone?.TrySetException(new InvalidOperationException(fileError));
                        else
                            _fileDone?.TrySetResult((json["duration"]?.GetValue<double>() ?? 0, json["cancelled"]?.GetValue<bool>() ?? false));
                        break;
                    case "refined":
                        Refined?.Invoke();
                        break;
                    case "summary":
                        OnSummaryEvent(json);
                        break;
                    case "translated":
                        OnTranslated(json);
                        break;
                    case "voice_match":
                        VoiceMatch?.Invoke(json["speaker"]!.GetValue<int>(), json["name"]?.GetValue<string>());
                        break;
                    case "voices":
                        VoicesChanged?.Invoke(json["count"]?.GetValue<int>() ?? 0);
                        if (json["error"]?.GetValue<string>() is { } voiceError) Error?.Invoke(voiceError);
                        break;
                    case "encoded":
                        TaskCompletionSource? done;
                        lock (_encoding)
                        {
                            var dst = json["dst"]?.GetValue<string>() ?? "";
                            _encoding.Remove(dst, out done);
                        }
                        if (json["ok"]?.GetValue<bool>() == true) done?.TrySetResult();
                        else done?.TrySetException(new InvalidOperationException(json["message"]?.GetValue<string>() ?? "変換できませんでした"));
                        break;
                    case "burn_progress":
                    case "burned":
                        (TaskCompletionSource Done, Action<double, double>? Progress) burn;
                        var burnDst = json["dst"]?.GetValue<string>() ?? "";
                        lock (_burning)
                        {
                            if (!_burning.TryGetValue(burnDst, out burn)) break;
                            if (json["event"]!.GetValue<string>() == "burned") _burning.Remove(burnDst);
                        }
                        if (json["event"]!.GetValue<string>() == "burn_progress")
                            burn.Progress?.Invoke(json["done"]?.GetValue<double>() ?? 0, json["total"]?.GetValue<double>() ?? 0);
                        else if (json["ok"]?.GetValue<bool>() == true) burn.Done.TrySetResult();
                        else burn.Done.TrySetException(new InvalidOperationException(json["message"]?.GetValue<string>() ?? "字幕を焼き込めませんでした"));
                        break;
                    case "error":
                        Error?.Invoke(json["message"]?.GetValue<string>() ?? "不明なエラー");
                        break;
                }
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException or KeyNotFoundException or OverflowException)
                {
                    // 形の違う行 (ライブラリが出力した文字など) は飛ばして読み続ける (読むのをやめると処理が固まって見える)
                    WriteLog("[app] 読めない知らせを飛ばしました: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
        _readerFinished = process; // (これより後に頼まれたものは、待たずに失敗にする)
        _flushed?.TrySetResult();
        var exited = new InvalidOperationException("文字起こしのプロセスが終了しました");
        _fileDone?.TrySetException(exited);
        _summary?.TrySetException(exited);
        lock (_translations)
        {
            foreach (var t in _translations.Values) t.TrySetException(exited);
            _translations.Clear();
        }
        lock (_encoding)
        {
            foreach (var t in _encoding.Values) t.TrySetException(exited);
            _encoding.Clear();
        }
        lock (_burning)
        {
            foreach (var t in _burning.Values) t.Done.TrySetException(exited);
            _burning.Clear();
        }
        // 読めなくなった処理は止める (残すと、次に使うときも起動し直さずに返事を待ち続ける)
        lock (_writeLock) KillProcess(process);
        if (!_disposed)
        {
            WriteLog("[app] 文字起こしのプロセスが終了しました");
            ProcessExited?.Invoke();
        }
    }

    private void OnSummaryEvent(JsonNode json)
    {
        if (json["job"]?.GetValue<int>() != _summaryJob || _summary == null) return;
        switch (json["state"]?.GetValue<string>())
        {
            case "loading":
                _summaryProgress?.Invoke(0, 0);
                break;
            case "progress":
                _summaryProgress?.Invoke(json["done"]!.GetValue<int>(), json["total"]!.GetValue<int>());
                break;
            case "done":
                _summary.TrySetResult(json["text"]?.GetValue<string>() ?? "");
                break;
            case "cancelled":
                _summary.TrySetResult(null);
                break;
            default:
                _summary.TrySetException(new InvalidOperationException(json["message"]?.GetValue<string>() ?? "要約を作れませんでした"));
                break;
        }
    }

    public bool IsSummarizing => _summary is { Task.IsCompleted: false };

    private readonly Dictionary<int, TaskCompletionSource<string[]>> _translations = [];
    private int _translateJob;

    private void OnTranslated(JsonNode json)
    {
        TaskCompletionSource<string[]>? done;
        lock (_translations) _translations.Remove(json["job"]?.GetValue<int>() ?? -1, out done);
        if (done == null) return;
        if (json["texts"] is JsonArray texts) done.TrySetResult(texts.Select(t => t?.GetValue<string>() ?? "").ToArray());
        else done.TrySetException(new InvalidOperationException(json["error"]?.GetValue<string>() ?? "AI で訳せませんでした"));
    }

    /// <summary>議事録の日本語訳を AI で作る (直前の発言 context を文脈に使い、登録した用語の表記で訳す)。</summary>
    public async Task<string[]> TranslateWithAiAsync(IReadOnlyList<(string Text, IReadOnlyList<string> Context)> items)
    {
        await EnsureStartedAsync();
        var done = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        int job;
        lock (_translations)
        {
            job = ++_translateJob;
            _translations[job] = done;
        }
        if (ProcessGone())
        {
            // (待ち始める前に処理が終わっていたら、終わったときの片付けはもう済んでいるので、ずっと待たない)
            lock (_translations) _translations.Remove(job);
            throw new InvalidOperationException("文字起こしのプロセスが終了しました");
        }
        var array = new JsonArray(items.Select(i => (JsonNode)new JsonObject
        {
            ["text"] = i.Text,
            ["context"] = new JsonArray(i.Context.Select(c => (JsonNode)c).ToArray()),
        }).ToArray());
        Send(new JsonObject { ["cmd"] = "translate", ["job"] = job, ["items"] = array }, null);
        return await done.Task;
    }

    /// <summary>
    /// 議事録の要約を PC 内の AI で作る (概要・決定事項・やること・主な論点の Markdown)。中止したら null。
    /// progress は (済んだ数, 全体の数)。AI の読み込み中は (0, 0)。
    /// </summary>
    public async Task<string?> SummarizeAsync(string transcript, Action<int, int>? progress = null)
    {
        if (IsSummarizing) throw new InvalidOperationException("要約を作っているところです");
        var summary = _summary = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _summarySent = false;
        _summaryProgress = progress;
        try
        {
            await EnsureStartedAsync();
        }
        catch (Exception ex)
        {
            summary.TrySetException(ex);
            return await summary.Task;
        }
        if (summary.Task.IsCompleted) return await summary.Task; // 準備中に中止された
        if (ProcessGone())
        {
            summary.TrySetException(new InvalidOperationException("文字起こしのプロセスが終了しました"));
            return await summary.Task;
        }
        Send(new JsonObject { ["cmd"] = "summarize", ["job"] = ++_summaryJob, ["text"] = transcript }, null);
        _summarySent = true;
        return await summary.Task;
    }

    private bool _summarySent;

    public void CancelSummary()
    {
        if (!IsSummarizing) return;
        if (_summarySent) Send(new JsonObject { ["cmd"] = "summary_cancel" }, null);
        else _summary?.TrySetResult(null); // まだ頼んでいなければ、ここで中止にする
    }

    /// <summary>
    /// 録音を始める。processId が null ならマイクだけ。
    /// excludeProcess なら processId 以外 (とその子プロセス以外) のすべての音を取り込む。
    /// </summary>
    public async Task StartAsync(int? processId, bool excludeProcess, bool includeMic, bool newSession)
    {
        await EnsureStartedAsync();
        if (_disposed) throw new ObjectDisposedException(nameof(TranscriptionService));
        // 新しい議事録なら話者の記憶も消す。続きの録音なら時刻の基準だけ取り直す
        lock (_pending) foreach (var b in _pending.Values) b.SetLength(0);
        Send(new JsonObject { ["cmd"] = newSession ? "reset" : "restart", ["session"] = BeginSession() }, null);
        WriteLog($"[app] 記録開始 window={(processId?.ToString() ?? "なし")}{(excludeProcess ? " (すべての音)" : "")} mic={includeMic}");
        LastRecording = null;
        if (RecordAudioPath is { } wav)
        {
            try
            {
                var sources = new List<string>();
                if (processId != null) sources.Add("win");
                if (includeMic) sources.Add("mic");
                _recorder = new SessionRecorder(wav, SessionStart, sources);
            }
            catch (Exception ex)
            {
                _recorder = null;
                Error?.Invoke("音声を保存できません (記録は続けます): " + ex.Message);
            }
        }

        var started = new List<IAudioSource>();
        _recorderErrorReported = false;
        _liveSources = (processId is int ? 1 : 0) + (includeMic ? 1 : 0);
        try
        {
            if (processId is int pid)
            {
                _window = AudioSources.Window(pid, excludeProcess);
                _window.DataAvailable += data => OnAudio("win", data);
                _window.Failed += ex => OnCaptureFailed("win", "アプリの音を取り込めなくなりました: " + ex.Message);
                _window.Start();
                started.Add(_window);
            }
            if (includeMic)
            {
                _mic = AudioSources.Microphone();
                _mic.DataAvailable += data => OnAudio("mic", data);
                _mic.Failed += ex => OnCaptureFailed("mic", "マイクの音を取り込めなくなりました (マイクを抜いたなど): " + ex.Message);
                _mic.Start();
                started.Add(_mic);
            }
        }
        catch
        {
            foreach (var c in started) c.Dispose();
            _window = _mic = null;
            _recorder?.Dispose();
            _recorder = null;
            throw;
        }
    }

    /// <summary>
    /// 動作確認用 (--selftest-minutes): 音を取り込む代わりに <see cref="FeedAudio"/> で渡す記録を始める。
    /// 取り込み以外 (音声の保存・文字起こし・話者の聞き分け) は普通の記録と同じ経路を通る。
    /// </summary>
    internal async Task StartSimulatedAsync(bool newSession = true)
    {
        await EnsureStartedAsync();
        lock (_pending) foreach (var b in _pending.Values) b.SetLength(0);
        Send(new JsonObject { ["cmd"] = newSession ? "reset" : "restart", ["session"] = BeginSession() }, null);
        LastRecording = null;
        if (RecordAudioPath is { } wav) _recorder = new SessionRecorder(wav, SessionStart, ["win"]);
    }

    /// <summary>動作確認用: 止めた記録の音声が最後まで文字になった (StopAsync は 30 秒までしか待たない)。</summary>
    internal Task Flushed => _flushed?.Task ?? Task.CompletedTask;

    /// <summary>動作確認用: ウィンドウ (またはマイク) から届いた音として渡す (16kHz・モノラル・16bit)。</summary>
    internal void FeedAudio(string source, byte[] pcm) => OnAudio(source, pcm);

    /// <summary>取り込みだけをすぐ止めて、記録した音声を閉じる (アプリを閉じるとき。残りの文字起こしは待たない)。</summary>
    public void StopCapture()
    {
        _window?.Dispose();
        _mic?.Dispose();
        _window = _mic = null;
        if (_recorder != null)
        {
            _recorder.Dispose();
            LastRecording = _recorder;
            _recorder = null;
        }
    }

    /// <summary>録音を止め、話し途中の音声も文字起こしが終わるまで待つ。</summary>
    public async Task StopAsync()
    {
        _window?.Dispose();
        _mic?.Dispose();
        _window = _mic = null;
        if (_recorder != null)
        {
            _recorder.Dispose();
            LastRecording = _recorder;
            _recorder = null;
        }
        foreach (var src in _pending.Keys.ToList()) FlushPending(src);
        WriteLog("[app] 記録停止");
        if (ProcessGone()) return; // 処理が落ちていれば、残りを待っても届かない
        _flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(new JsonObject { ["cmd"] = "flush", ["seq"] = ++_flushSeq }, null);
        await Task.WhenAny(_flushed.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    }

    /// <summary>処理を止めて、次に使うときに起動し直す (準備に失敗したときの「もう一度試す」)。</summary>
    public void Restart()
    {
        if (IsRecording || IsTranscribingFile || _starting is { IsCompleted: false }) return;
        lock (_writeLock)
        {
            KillProcess();
            _starting = null;
        }
    }

    /// <summary>
    /// 動画・音声ファイルを文字起こしする。発言の Start/End はファイルの先頭からの秒数で、
    /// 結果は録音と同じイベント (SegmentReceived など) で届く。終わるか中止すると (長さ, 中止したか) を返す。
    /// </summary>
    public async Task<(double Duration, bool Cancelled)> TranscribeFileAsync(string path)
    {
        if (IsRecording) throw new InvalidOperationException("記録中はファイルを文字起こしできません");
        await EnsureStartedAsync();
        WriteLog("[app] ファイルの文字起こしを開始");
        _fileDone = new TaskCompletionSource<(double, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ProcessGone()) _fileDone.TrySetException(new InvalidOperationException("文字起こしのプロセスが終了しました"));
        Send(new JsonObject { ["cmd"] = "file", ["path"] = path, ["session"] = BeginSession() }, null);
        return await _fileDone.Task;
    }

    /// <summary>新しい会議として、話者の記憶などを消す (ファイルの文字起こしの前など)。</summary>
    public void ResetSession() => Send(new JsonObject { ["cmd"] = "reset" }, null);

    /// <summary>ファイルの文字起こしを途中でやめる (そこまでの発言は残る)。</summary>
    public void CancelFile()
    {
        if (IsTranscribingFile) Send(new JsonObject { ["cmd"] = "cancel" }, null);
    }

    /// <summary>画面で話者 from を話者 into にまとめたことを伝える (以後の分け直しでも 1 人として扱う)。</summary>
    public void MergeSpeakers(int from, int into) =>
        Send(new JsonObject { ["cmd"] = "config", ["merge"] = new JsonArray(from, into) }, null);

    /// <summary>声の聞き分けの細かさ (-1 = まとめる 〜 +1 = 細かく分ける、0 が既定)。</summary>
    public void SetSpeakerSensitivity(double sensitivity) =>
        Send(new JsonObject { ["cmd"] = "config", ["sensitivity"] = sensitivity }, null);

    /// <summary>文字にする言語 (1 つならその言語、複数なら発言ごとにその中から判定)。</summary>
    public void SetLanguages(IEnumerable<string> languages) =>
        Send(new JsonObject { ["cmd"] = "config", ["languages"] = new JsonArray(languages.Select(l => (JsonNode)l).ToArray()) }, null);

    /// <summary>
    /// 用語の登録 (人名・社名・専門用語)。1 行に 1 語。「九条(くじょう)」で読みを添えられ、「誤り→正しい」は置き換えの決まりになる。
    /// </summary>
    public void SetTerms(IEnumerable<string> terms) =>
        Send(new JsonObject { ["cmd"] = "config", ["terms"] = new JsonArray(terms.Select(t => (JsonNode)t).ToArray()) }, null);

    /// <summary>ウィンドウ側で聞き分ける人数 (0 = 自動)。</summary>
    public void SetExpectedSpeakers(int count) =>
        Send(new JsonObject { ["cmd"] = "config", ["speakers"] = count }, null);

    /// <summary>話者 speaker の声を name として覚える (次の会議で、似た声の話者にこの名前を付ける)。</summary>
    public void EnrollVoice(int speaker, string name) =>
        Send(new JsonObject { ["cmd"] = "config", ["enroll"] = new JsonObject { ["speaker"] = speaker, ["name"] = name } }, null);

    /// <summary>覚えた声を忘れる (name が null ならすべて)。</summary>
    public void ForgetVoice(string? name) =>
        Send(new JsonObject { ["cmd"] = "config", ["forget"] = name ?? "" }, null);

    /// <summary>前後の文脈による聞き間違いの補正 (LLM) を使うか。</summary>
    /// <summary>速さを変える ("auto" / "light" / "accurate")。次の発言から切り替わる。</summary>
    public void SetSpeed(string speed) =>
        Send(new JsonObject { ["cmd"] = "config", ["speed"] = speed }, null);

    public void SetContextCorrection(bool enabled) =>
        Send(new JsonObject { ["cmd"] = "config", ["llm"] = enabled }, null);

    // 音量の表示は 1 秒に約 20 回まで (音の届くたび (約 100 回/秒・2 系統) に画面へ送ると、画面の処理が忙しくなる)
    private readonly Dictionary<string, (long At, double Peak)> _levels = [];

    private bool _recorderErrorReported;
    private int _liveSources; // 取り込みを続けている音の数 (0 になったら記録が止まったことを知らせる)

    /// <summary>取り込める音がすべて無くなった (マイクだけで記録中にマイクを抜いたなど)。記録を止める必要がある。</summary>
    public event Action<string>? CaptureEnded;

    private void OnCaptureFailed(string source, string message)
    {
        _recorder?.RemoveSource(source);
        if (Interlocked.Decrement(ref _liveSources) > 0) CaptureLost?.Invoke(message);
        else CaptureEnded?.Invoke(message);
    }

    private void OnAudio(string source, byte[] data)
    {
        double level = Rms(data);
        long now = Environment.TickCount64;
        bool send;
        lock (_levels)
        {
            var (at, peak) = _levels.GetValueOrDefault(source);
            peak = Math.Max(peak, level);
            send = now - at >= 50;
            _levels[source] = send ? (now, 0) : (at, peak);
            level = peak;
        }
        if (send) Level?.Invoke(source, level);
        if (_recorder is { } recorder)
        {
            recorder.Add(source, data);
            if (recorder.WriteError is { } writeError && !_recorderErrorReported)
            {
                _recorderErrorReported = true;
                Error?.Invoke("会議の音声を保存できなくなりました (ディスクの空きを確かめてください。文字起こしは続けます): " + writeError.Message);
            }
        }
        lock (_pending)
        {
            var buffer = _pending[source];
            buffer.Write(data);
            if (buffer.Length < SendChunkBytes) return;
        }
        FlushPending(source);
    }

    private void FlushPending(string source)
    {
        byte[] chunk;
        lock (_pending)
        {
            var buffer = _pending[source];
            if (buffer.Length == 0) return;
            chunk = buffer.ToArray();
            buffer.SetLength(0);
        }
        Send(new JsonObject { ["cmd"] = "audio", ["src"] = source }, chunk);
    }

    private void Send(JsonObject header, byte[]? payload)
    {
        var process = _process;
        try
        {
            if (process == null || process.HasExited) return;
        }
        catch (InvalidOperationException)
        {
            return; // 片付け済み
        }
        if (payload != null) header["len"] = payload.Length;
        try
        {
            lock (_writeLock)
            {
                var stream = process.StandardInput.BaseStream;
                stream.Write(Encoding.UTF8.GetBytes(header.ToJsonString() + "\n"));
                if (payload != null) stream.Write(payload);
                stream.Flush();
            }
        }
        catch (IOException ex)
        {
            Error?.Invoke("文字起こしのプロセスに送れませんでした: " + ex.Message);
        }
    }

    private static readonly object LogLock = new();

    private static void WriteLog(string line)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2_000_000) File.Delete(LogPath);
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\r\n");
            }
        }
        catch
        {
            // 記録できなくても続行する
        }
    }

    private static double Rms(byte[] pcm)
    {
        if (pcm.Length < 2) return 0;
        double sum = 0;
        int n = pcm.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double s = BitConverter.ToInt16(pcm, i * 2) / 32768.0;
            sum += s * s;
        }
        return Math.Min(1, Math.Sqrt(sum / n) * 4); // 話し声が 0.3〜0.8 くらいになるよう強調
    }

    /// <summary>
    /// 保存した WAV を小さな音声ファイル (AAC の .m4a) にする。1 時間で約 115MB → 約 14MB。
    /// 変換は裏の Python (PyAV) で行う。
    /// </summary>
    private readonly Dictionary<string, (TaskCompletionSource Done, Action<double, double>? Progress)> _burning = [];

    /// <summary>
    /// 録画した動画に字幕を焼き込んだ動画を作る (Python の subtitle_video.py。音はそのまま、映像は GPU があれば GPU で作り直す)。
    /// cues は動画の先頭からの時間。ct で中止できる。
    /// </summary>
    public async Task BurnSubtitlesAsync(string video, string output, IEnumerable<MinutesDocument.SubtitleCue> cues,
        Action<double, double>? progress, CancellationToken ct)
    {
        await EnsureStartedAsync();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_burning) _burning[output] = (done, progress);
        // 待ち始める前に処理が終わっていたら (終わったときの片付けはもう済んでいる)、ずっと待たない
        if (ProcessGone())
        {
            lock (_burning) _burning.Remove(output);
            throw new InvalidOperationException("文字起こしのプロセスが終了しました");
        }
        var list = new JsonArray(cues.Select(c => (JsonNode)new JsonArray(c.Start.TotalSeconds, c.End.TotalSeconds, c.Display)).ToArray());
        Send(new JsonObject { ["cmd"] = "burn", ["src"] = video, ["dst"] = output, ["cues"] = list }, null);
        using (ct.Register(() => Send(new JsonObject { ["cmd"] = "burn_cancel", ["dst"] = output }, null)))
            await done.Task;
    }

    private volatile Process? _readerFinished; // 知らせを読み終えた (終わった) プロセス

    /// <summary>文字起こしのプロセスが終わった・知らせを読めなくなった (頼んでも返事が来ない)。</summary>
    private bool ProcessGone()
    {
        try
        {
            var process = _process;
            return process is not { HasExited: false } || ReferenceEquals(_readerFinished, process);
        }
        catch (InvalidOperationException)
        {
            return true; // (ほかの処理が片付けたところ)
        }
    }

    public async Task EncodeAudioAsync(string wav, string m4a)
    {
        await EnsureStartedAsync();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_encoding) _encoding[m4a] = done;
        if (ProcessGone())
        {
            lock (_encoding) _encoding.Remove(m4a);
            throw new InvalidOperationException("文字起こしのプロセスが終了しました");
        }
        Send(new JsonObject { ["cmd"] = "encode", ["src"] = wav, ["dst"] = m4a }, null);
        await done.Task;
    }

    public void Dispose()
    {
        _disposed = true;
        _window?.Dispose();
        _mic?.Dispose();
        _window = _mic = null;
        _recorder?.Dispose();
        _recorder = null;
        KillProcess();
    }

    /// <summary>only を渡したら、それが今の処理のときだけ止める (古い起動の後始末で、新しく起動した処理を止めないように)。</summary>
    private void KillProcess(Process only)
    {
        if (!ReferenceEquals(_process, only))
        {
            try { if (!only.HasExited) only.Kill(entireProcessTree: true); } catch { }
            only.Dispose();
            return;
        }
        KillProcess();
    }

    private void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000); // (書きかけのファイルを放してから、呼んだ側が片付けられるように)
            }
        }
        catch
        {
            // 既に終了している
        }
        _process?.Dispose();
        _process = null;
    }
}
