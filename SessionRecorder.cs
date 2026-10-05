using System.IO;
using System.Text;

namespace GetText;

/// <summary>
/// 記録中の会議の音声を WAV (16kHz・モノラル・16bit) に書く。ウィンドウとマイクの音を混ぜて 1 本にする。
/// どちらの音も「受け取ったサンプル数 = 記録を始めてからの時間」になっている (取り込み元が無音を補う) ので、
/// 同じ位置のサンプルどうしを足せば時刻がそろう。落ちても途中まで聞けるよう、数秒ごとに WAV の長さを書き直す。
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    public const int SampleRate = AudioSources.SampleRate;

    private readonly FileStream _file;
    private readonly Dictionary<string, Queue<short>> _queues = [];
    private readonly object _lock = new();
    private long _samples;
    private long _sinceHeader;
    private bool _closed;

    public string Path { get; }
    public DateTime Start { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)_samples / SampleRate);

    /// <param name="sources">混ぜる音 ("win" / "mic")。</param>
    public SessionRecorder(string path, DateTime start, IEnumerable<string> sources)
    {
        Path = path;
        Start = start;
        foreach (var s in sources) _queues[s] = new Queue<short>();
        if (_queues.Count == 0) throw new ArgumentException("音の種類がありません", nameof(sources));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        WriteHeader();
    }

    /// <summary>16bit PCM の音を加える (source は "win" / "mic")。</summary>
    /// <summary>書き込みに失敗した理由 (ディスクが一杯など)。失敗したら保存をやめる (記録の文字起こしは続く)。</summary>
    public Exception? WriteError { get; private set; }

    public void Add(string source, byte[] pcm)
    {
        lock (_lock)
        {
            if (_closed || !_queues.TryGetValue(source, out var queue)) return;
            for (int i = 0; i + 1 < pcm.Length; i += 2) queue.Enqueue((short)(pcm[i] | (pcm[i + 1] << 8)));
            Safely(() => Mix(final: false));
        }
    }

    // 書き込みの失敗は記録して保存をやめる (例外を取り込みのスレッドに出すとアプリごと落ちるため)
    private void Safely(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
        {
            WriteError ??= ex;
            _closed = true;
            try { _file.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 取り込めなくなった音 (ヘッドセットを抜いたなど) を外す。残りはそれまでの分を書き、以後はもう一方の音だけで続ける
    /// (外さないと、もう一方の音がたまる一方で WAV に書かれなくなる)。
    /// </summary>
    public void RemoveSource(string source)
    {
        lock (_lock)
        {
            if (_closed || !_queues.ContainsKey(source) || _queues.Count == 1) return;
            Safely(() => Mix(final: true));
            _queues.Remove(source);
        }
    }

    // すべての音がそろった分だけ混ぜて書く (最後は短い方を無音として残りも書く)
    private void Mix(bool final)
    {
        int n = final ? _queues.Values.Max(q => q.Count) : _queues.Values.Min(q => q.Count);
        if (n <= 0) return;
        var bytes = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            int sum = 0;
            foreach (var q in _queues.Values)
                if (q.Count > 0) sum += q.Dequeue();
            short v = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)v;
            bytes[i * 2 + 1] = (byte)(v >> 8);
        }
        _file.Write(bytes);
        _samples += n;
        _sinceHeader += n;
        if (_sinceHeader >= SampleRate * 5)
        {
            WriteHeader();
            _file.Flush();
            _sinceHeader = 0;
        }
    }

    private void WriteHeader()
    {
        long position = _file.Position;
        long dataBytes = _samples * 2;
        _file.Position = 0;
        using (var w = new BinaryWriter(_file, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write((uint)Math.Min(uint.MaxValue, 36 + dataBytes));
            w.Write("WAVEfmt "u8);
            w.Write(16);
            w.Write((short)1);          // PCM
            w.Write((short)1);          // モノラル
            w.Write(SampleRate);
            w.Write(SampleRate * 2);    // 1 秒あたりのバイト数
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write((uint)Math.Min(uint.MaxValue, dataBytes));
        }
        _file.Position = Math.Max(position, 44);
    }

    /// <summary>残りを書いて閉じる。</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_closed) return;
            try
            {
                Safely(() =>
                {
                    Mix(final: true);
                    WriteHeader();
                });
            }
            finally
            {
                _closed = true;
                try { _file.Dispose(); } catch { }
            }
        }
    }
}
