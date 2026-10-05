using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using IRandomAccessStream = Windows.Storage.Streams.IRandomAccessStream;

namespace GetText;

/// <summary>録画の設定。</summary>
public sealed record RecordOptions(int Fps = 30, bool HighQuality = false, bool WindowAudio = true, bool Microphone = false, string MicrophoneDevice = "");

/// <summary>
/// 選んだウィンドウ (または画面全体) を、その音も入れて MP4 (H.264 + AAC) に録画する。
/// ・画面: Windows Graphics Capture。GPU で描くアプリ (会議アプリ・ブラウザ) も黒くならずに取り込める。
///   会議アプリには何も伝わらない (会議アプリ自身の「レコーディング」とは別)。
///   ただし、取り込みを禁止しているウィンドウ (主催者の「画面キャプチャの防止」・著作権保護の動画) は Windows が黒く渡すので黒くなる。
/// ・音: そのアプリ (プロセスとその子) が再生している音だけ (WASAPI のプロセス ループバック)。マイクも足せる。
/// ・書き出し: Windows の Media Foundation (GPU があればハードウェアで H.264)。追加のソフトは要らない。
/// </summary>
public sealed class ScreenRecorder
{
    private const int AudioRate = 48000, AudioChannels = 2, AudioBlock = AudioChannels * 2;
    private const int ChunkFrames = AudioRate / 50; // 20 ms ずつ渡す
    // 取り込んだ音が届くまでの余裕。これより遅れた分だけを無音で補う (取り込み側も 0.2 秒遅れで無音を補うので、それより長くする)
    private static readonly TimeSpan AudioLatency = TimeSpan.FromMilliseconds(350);

    public string Path { get; }

    /// <summary>録画を始めた時刻 (動画の 0 秒。議事録の発言の時刻から字幕の時刻を出すのに使う)。</summary>
    public DateTime StartedAt { get; }
    public int Width { get; }
    public int Height { get; }
    public RecordOptions Options { get; }

    /// <summary>録画が途中で止まった (ウィンドウが閉じた・書き出しに失敗したなど)。UI のスレッドとは限らない。</summary>
    public event Action<string>? Failed;

    private readonly IDirect3DDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private Windows.Graphics.SizeInt32 _poolSize;
    private readonly long _startTicks; // Stopwatch の時刻 (録画の 0 秒)
    private readonly TimeSpan _startRelative; // 取り込んだ画面の時刻 (SystemRelativeTime) の 0 秒
    private readonly TimeSpan _minFrameGap;

    private AudioCapture? _windowAudio, _micAudio;
    private readonly ByteQueue _windowQueue = new(), _micQueue = new();
    private long _audioFrames; // 渡した音の長さ (サンプル数)

    private readonly object _videoLock = new();
    private readonly Stack<Canvas> _freeCanvases = new();
    private Canvas _latest;
    private long _latestVersion, _servedVersion = -1;
    private long _capturedFrames; // 取り込んだ (変わった) 画面の数
    private long _arrivedFrames;  // Windows から届いた画面の数 (間引く前)

    /// <summary>Windows から届いた画面の 1 秒あたりの数 (間引く前)。</summary>
    public double ArrivedFps => Duration.TotalSeconds > 0.5 ? Interlocked.Read(ref _arrivedFrames) / Duration.TotalSeconds : 0;
    private TimeSpan _latestTime, _lastVideoTime = TimeSpan.FromTicks(-1), _lastPublished = TimeSpan.FromSeconds(-1);
    private readonly SemaphoreSlim _frameSignal = new(0, int.MaxValue);
    private int _converting;
    // 120 コマ/秒以上では、画面の変換 (GPU から読み出す) を同時にいくつか進め、取り込みの枠も多めに持つ
    private readonly int _maxConversions, _buffers;
    private TimeSpan _lastAccepted = TimeSpan.FromSeconds(-1);

    private volatile bool _stopping;
    private TimeSpan _stopAt = TimeSpan.MaxValue;
    private IRandomAccessStream? _stream;
    private Task _transcode = Task.CompletedTask;
    private string? _failure;
    private Exception? _transcodeError; // 書き出しが途中で失敗した (ファイルは壊れている)
    private readonly double _scale;     // 取り込んだ画面を録画の大きさにする倍率 (4K より大きい画面だけ 1 より小さい)

    /// <summary>録画が止まった理由 (ウィンドウが閉じられたなど)。無ければ null。</summary>
    public string? Failure => _failure;

    public static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); } catch { return false; }
        }
    }

    /// <summary>取り込みを禁止しているウィンドウか (主催者の「画面キャプチャの防止」・著作権保護の動画など。録画すると黒くなる)。</summary>
    public static bool IsCaptureProtected(IntPtr window) =>
        window != IntPtr.Zero && GetWindowDisplayAffinity(window, out uint affinity) && affinity != 0;

    private ScreenRecorder(GraphicsCaptureItem item, int processId, bool excludeProcess, string path, RecordOptions options)
    {
        Path = path;
        Options = options;
        _item = item;
        _device = CreateDevice();
        try
        {
            var size = item.Size;
            // H.264 は縦横が偶数。4K (3840×2160) より大きい画面 (縦長の 4K・横に長い画面など) は、縦横の比を保って縮める
            _scale = Math.Min(1.0, Math.Min(3840.0 / Math.Max(1, size.Width), 2160.0 / Math.Max(1, size.Height)));
            Width = Math.Max(64, (int)(size.Width * _scale) & ~1);
            Height = Math.Max(64, (int)(size.Height * _scale) & ~1);
            _minFrameGap = TimeSpan.FromSeconds(0.9 / Math.Max(1, options.Fps));
            _latest = new Canvas(new byte[Width * Height * 4]) { Refs = 1 }; // まだ画面が来ていない間は黒い画面
            _poolSize = size;

            _maxConversions = options.Fps > 60 ? 3 : 1;
            _buffers = options.Fps > 60 ? 4 : 2;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, _buffers, size);
            _session = _pool.CreateCaptureSession(item);
            try { _session.IsCursorCaptureEnabled = true; } catch { }
            HideYellowBorder(_session);
            SetMinUpdateInterval(_session, TimeSpan.FromSeconds(1.0 / Math.Max(1, options.Fps)));
            item.Closed += (_, _) => Fail("録画していたウィンドウが閉じられました");

            _startTicks = Stopwatch.GetTimestamp();
            StartedAt = DateTime.Now;
            _startRelative = TimeSpan.FromSeconds(_startTicks / (double)Stopwatch.Frequency);
            _pool.FrameArrived += OnFrameArrived;
            _session.StartCapture();

            if (options.WindowAudio && processId > 0)
            {
                _windowAudio = excludeProcess
                    ? AudioCapture.ForAllExcept(processId, AudioRate, AudioChannels)
                    : AudioCapture.ForProcess(processId, AudioRate, AudioChannels);
                _windowAudio.DataAvailable += _windowQueue.Enqueue;
                _windowAudio.Failed += ex => App.Log("RecordAudio", ex);
            }
            if (options.Microphone)
            {
                _micAudio = AudioCapture.ForMicrophone(options.MicrophoneDevice, AudioRate, AudioChannels);
                _micAudio.DataAvailable += _micQueue.Enqueue;
                _micAudio.Failed += ex => App.Log("RecordMic", ex);
            }
        }
        catch
        {
            // (途中で失敗したら、作った GPU の資源と取り込みを片付ける)
            try { _session?.Dispose(); } catch { }
            try { _pool?.Dispose(); } catch { }
            try { _device.Dispose(); } catch { }
            throw;
        }
    }

    private bool HasAudio => _windowAudio != null || _micAudio != null;

    private TimeSpan Elapsed => TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - _startTicks) / (double)Stopwatch.Frequency);

    /// <summary>録画した長さ。</summary>
    public TimeSpan Duration => _stopping ? _stopAt : Elapsed;

    /// <summary>実際に取り込めた画面の 1 秒あたりの数 (画面が変わらない間は増えないので、動きのある画面での目安)。</summary>
    public double CapturedFps => Duration.TotalSeconds > 0.5 ? Interlocked.Read(ref _capturedFrames) / Duration.TotalSeconds : 0;

    /// <summary>書いたファイルの大きさ (バイト)。</summary>
    public long FileSize
    {
        get
        {
            try { return new FileInfo(Path).Length; } catch { return 0; }
        }
    }

    /// <summary>ウィンドウを録画し始める。processId のアプリの音 (とマイク) も入れる。</summary>
    public static Task<ScreenRecorder> StartWindowAsync(IntPtr window, int processId, string path, RecordOptions options) =>
        StartAsync(CreateItemForWindow(window), processId, false, path, options);

    /// <summary>モニター (画面全体) を録画し始める。音は GetText 以外のすべての音 (とマイク)。</summary>
    public static Task<ScreenRecorder> StartMonitorAsync(IntPtr monitor, string path, RecordOptions options) =>
        StartAsync(CreateItemForMonitor(monitor), Environment.ProcessId, true, path, options);

    private static async Task<ScreenRecorder> StartAsync(GraphicsCaptureItem item, int processId, bool excludeProcess, string path, RecordOptions options)
    {
        var recorder = new ScreenRecorder(item, processId, excludeProcess, path, options);
        try
        {
            await recorder.StartWritingAsync();
            return recorder;
        }
        catch
        {
            try { await recorder.StopAsync(); } catch { } // (始められなかった理由の方を伝える)
            throw;
        }
    }

    private async Task StartWritingAsync()
    {
        var full = System.IO.Path.GetFullPath(Path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.Create(full).Dispose();
        var file = await StorageFile.GetFileFromPathAsync(full);
        _stream = await file.OpenAsync(FileAccessMode.ReadWrite);

        // H.264 の書き出しが受け付けるコマ数には上限がある (この PC では 144 まで)。受け付けない数なら下げて準備する。
        // 書き出しのコマ数は目安で、取り込んだ画面はすべて実際の時刻のまま入れる
        PrepareTranscodeResult? prepared = null;
        Exception? lastError = null;
        foreach (int rate in new[] { Options.Fps, 144, 120, 60, 30 }.Where(r => r <= Options.Fps).Distinct())
        {
            try
            {
                prepared = await PrepareAsync(rate);
                if (prepared.CanTranscode)
                {
                    EncoderFps = rate;
                    break;
                }
                lastError = new InvalidOperationException(prepared.FailureReason.ToString());
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                lastError = ex;
            }
            prepared = null;
            _stream.Seek(0);
            _stream.Size = 0;
        }
        if (prepared == null) throw new InvalidOperationException("録画の書き出しを準備できませんでした (" + lastError?.Message + ")", lastError);
        // 動画の 0 秒は取り込みを始めた時刻。書き出しの準備にかかった時間だけ音の頭を無音で埋めてから音を取り込み始める
        // (埋めないと、音が映像より先に進んだまま録画される)
        // (取り込みを始める処理は機器の準備を待つので、それぞれ始める直前の時刻で埋める)
        // (Start は機器の準備ができるまで待ち、その後に取り込みが動き出すので、戻った時刻までを頭に足す)
        if (_windowAudio != null)
        {
            _windowAudio.Start();
            _windowQueue.PushFront(new byte[(int)(Elapsed.TotalSeconds * AudioRate) * AudioBlock]);
        }
        if (_micAudio != null)
        {
            _micAudio.Start();
            _micQueue.PushFront(new byte[(int)(Elapsed.TotalSeconds * AudioRate) * AudioBlock]);
        }
        _transcode = Task.Run(async () =>
        {
            try
            {
                await prepared.TranscodeAsync();
            }
            catch (Exception ex)
            {
                App.Log("RecordTranscode", ex);
                _transcodeError = ex;
                Fail("録画を書き出せませんでした: " + ex.Message);
            }
        });
    }

    /// <summary>書き出しに使ったコマ数 (選んだ数を受け付けないときは下げた数)。</summary>
    public int EncoderFps { get; private set; }

    private async Task<PrepareTranscodeResult> PrepareAsync(int rate)
    {
        var video = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)Width, (uint)Height);
        video.FrameRate.Numerator = (uint)rate;
        video.FrameRate.Denominator = 1;
        var videoDescriptor = new VideoStreamDescriptor(video);
        MediaStreamSource source;
        if (HasAudio)
        {
            var audioDescriptor = new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(AudioRate, AudioChannels, 16));
            source = new MediaStreamSource(videoDescriptor, audioDescriptor);
        }
        else
        {
            source = new MediaStreamSource(videoDescriptor);
        }
        source.BufferTime = TimeSpan.Zero;
        source.CanSeek = false;
        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += OnSampleRequested;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Video.Width = (uint)Width;
        profile.Video.Height = (uint)Height;
        profile.Video.FrameRate.Numerator = (uint)rate;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        double bitsPerPixel = Options.HighQuality ? 0.16 : 0.08;
        // 画質の目安は選んだコマ数で決める (書き出しのコマ数を下げても、実際は取り込んだ画面がすべて入る)
        profile.Video.Bitrate = (uint)Math.Clamp(Width * Height * Options.Fps * bitsPerPixel, 1_000_000, 80_000_000);
        profile.Audio = HasAudio ? AudioEncodingProperties.CreateAac(AudioRate, AudioChannels, 192_000) : null;
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        return await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, _stream!, profile);
    }

    // ───────── 画面 ─────────

    private sealed class Canvas(byte[] data)
    {
        public readonly byte[] Data = data;
        public int Refs; // 今の画面として持っている分 + 書き出しに渡して処理が終わっていない分
    }

    private Canvas RentCanvas()
    {
        lock (_videoLock)
            if (_freeCanvases.Count > 0) return _freeCanvases.Pop();
        return new Canvas(new byte[Width * Height * 4]);
    }

    private void ReleaseCanvas(Canvas canvas)
    {
        lock (_videoLock)
            if (--canvas.Refs == 0) _freeCanvases.Push(canvas);
    }

    private async void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = pool.TryGetNextFrame();
            if (frame == null || _stopping) return;
            Interlocked.Increment(ref _arrivedFrames);
            var content = frame.ContentSize;
            var time = frame.SystemRelativeTime - _startRelative;
            bool resized = content.Width != _poolSize.Width || content.Height != _poolSize.Height;
            // 決めた間隔より早い画面・変換が追いつかないときの画面は飛ばす (負荷を抑える)
            if (time - _lastAccepted < _minFrameGap)
            {
                if (resized) Resize(content);
                return;
            }
            if (Interlocked.Increment(ref _converting) > _maxConversions)
            {
                Interlocked.Decrement(ref _converting);
                if (resized) Resize(content);
                return;
            }
            _lastAccepted = time;
            try
            {
                var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
                using (bitmap)
                {
                    var canvas = RentCanvas();
                    Compose(bitmap, content, canvas.Data);
                    Publish(canvas, time);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _converting);
            }
            if (resized) Resize(content);
        }
        catch (Exception ex) when (_stopping || ex is ObjectDisposedException)
        {
            // 止めている途中
        }
        catch (Exception ex)
        {
            App.Log("RecordFrame", ex);
        }
        finally
        {
            frame?.Dispose();
        }
    }

    // ウィンドウの大きさが変わったら取り込みの大きさを合わせる (録画の大きさは最初のまま。はみ出た分は切り、足りない分は黒)
    private void Resize(Windows.Graphics.SizeInt32 size)
    {
        if (size.Width <= 0 || size.Height <= 0 || _stopping) return;
        _poolSize = size;
        try { _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, _buffers, size); } catch (ObjectDisposedException) { }
    }

    private void Compose(SoftwareBitmap bitmap, Windows.Graphics.SizeInt32 content, byte[] canvas)
    {
        int srcW = bitmap.PixelWidth, srcH = bitmap.PixelHeight;
        int bytes = srcW * srcH * 4;
        // 取り込んだ画面を写す場所は使い回す (1080p・60 コマ/秒で毎秒 500MB の配列を作ると、片付けで止まる)
        var source = System.Buffers.ArrayPool<byte>.Shared.Rent(bytes);
        try
        {
            bitmap.CopyToBuffer(source.AsBuffer(0, bytes));
            int cw = Math.Min(content.Width, srcW), ch = Math.Min(content.Height, srcH);
            // 書き出し (Media Foundation) は非圧縮の BGRA を下の行から並んだものとして読むので、上下を入れ替えて置く
            if (_scale >= 1.0)
            {
                int w = Math.Min(cw, Width), h = Math.Min(ch, Height);
                if (w < Width || h < Height) Array.Clear(canvas);
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(source, y * srcW * 4, canvas, (Height - 1 - y) * Width * 4, w * 4);
                return;
            }
            // 4K より大きい画面: 縦横の比を保って縮める (近い画素を拾う。行ごとに並べて速くする)
            int dw = Math.Min(Width, (int)(cw * _scale)), dh = Math.Min(Height, (int)(ch * _scale));
            if (dw < Width || dh < Height) Array.Clear(canvas);
            double step = 1.0 / _scale;
            Parallel.For(0, dh, y =>
            {
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(source.AsSpan(Math.Min(srcH - 1, (int)(y * step)) * srcW * 4, srcW * 4));
                var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(canvas.AsSpan((Height - 1 - y) * Width * 4, Width * 4));
                for (int x = 0; x < dw; x++) dst[x] = src[Math.Min(srcW - 1, (int)(x * step))];
            });
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(source);
        }
    }

    private void Publish(Canvas canvas, TimeSpan time)
    {
        lock (_videoLock)
        {
            // 同時に変換していた画面が後から終わったら (新しい画面がもう入っている)、使わない
            if (_latestVersion > 0 && time <= _latestTime)
            {
                _freeCanvases.Push(canvas);
                return;
            }
            canvas.Refs++;
            var old = _latest;
            _latest = canvas;
            _latestTime = time;
            _latestVersion++;
            _capturedFrames++;
            _lastPublished = time;
            if (--old.Refs == 0) _freeCanvases.Push(old);
        }
        _frameSignal.Release();
    }

    // ───────── 書き出しへ渡す ─────────

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var request = args.Request;
        var deferral = request.GetDeferral();
        bool isVideo = request.StreamDescriptor is VideoStreamDescriptor;
        _ = Task.Run(async () =>
        {
            try
            {
                request.Sample = isVideo ? await NextVideoAsync() : await NextAudioAsync();
            }
            catch (Exception ex)
            {
                App.Log("RecordSample", ex);
                request.Sample = null;
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private async Task<MediaStreamSample?> NextVideoAsync()
    {
        // 新しい画面を待つ。画面が変わらない (止まっている) ときも 0.2 秒ごとに同じ画面を渡して、動画の時間を進める
        var waitUntil = Elapsed + TimeSpan.FromMilliseconds(200);
        while (!_stopping && Interlocked.Read(ref _latestVersion) == _servedVersion && Elapsed < waitUntil)
            await _frameSignal.WaitAsync(TimeSpan.FromMilliseconds(50));
        Canvas canvas;
        TimeSpan time;
        lock (_videoLock)
        {
            bool fresh = _latestVersion != _servedVersion;
            if (_stopping && (!fresh || _latestTime >= _stopAt)) return null; // 終わり
            canvas = _latest;
            canvas.Refs++;
            time = fresh ? _latestTime : Elapsed;
            _servedVersion = _latestVersion;
        }
        if (time <= _lastVideoTime) time = _lastVideoTime + TimeSpan.FromMilliseconds(1);
        _lastVideoTime = time;
        var sample = MediaStreamSample.CreateFromBuffer(canvas.Data.AsBuffer(), time);
        sample.Processed += (_, _) => ReleaseCanvas(canvas);
        return sample;
    }

    private async Task<MediaStreamSample?> NextAudioAsync()
    {
        var position = TimeSpan.FromSeconds(_audioFrames / (double)AudioRate);
        if (_stopping && position >= _stopAt) return null;
        var chunkEnd = TimeSpan.FromSeconds((_audioFrames + ChunkFrames) / (double)AudioRate);
        // 実際の時間より先の音は渡さない (届いていない音を無音で埋めて、後で音がずれないように)
        while (!_stopping && Elapsed < chunkEnd + AudioLatency)
            await Task.Delay(10);
        int bytes = ChunkFrames * AudioBlock;
        var mixed = new byte[bytes];
        bool any = false;
        foreach (var queue in new[] { _windowAudio != null ? _windowQueue : null, _micAudio != null ? _micQueue : null })
        {
            if (queue == null) continue;
            // 取り込みが先に進みすぎていたら (処理が遅れた・時計のずれ)、古い分を捨てて合わせる
            long expected = (long)((Elapsed - position).TotalSeconds * AudioRate) * AudioBlock;
            queue.DropExcess(expected + AudioRate / 4 * AudioBlock);
            var chunk = new byte[bytes];
            int read = queue.Read(chunk);
            if (read == 0) continue;
            if (!any)
            {
                Buffer.BlockCopy(chunk, 0, mixed, 0, bytes);
                any = true;
            }
            else
            {
                for (int i = 0; i < bytes; i += 2)
                {
                    int sum = (short)(mixed[i] | mixed[i + 1] << 8) + (short)(chunk[i] | chunk[i + 1] << 8);
                    sum = Math.Clamp(sum, short.MinValue, short.MaxValue);
                    mixed[i] = (byte)sum;
                    mixed[i + 1] = (byte)(sum >> 8);
                }
            }
        }
        var sample = MediaStreamSample.CreateFromBuffer(mixed.AsBuffer(), position);
        sample.Duration = TimeSpan.FromSeconds(ChunkFrames / (double)AudioRate);
        _audioFrames += ChunkFrames;
        return sample;
    }

    /// <summary>
    /// 録画中のプレビュー: いま録画している画面 (下の行から並んだ BGRA、Width×Height) を write に渡す。
    /// 録画と同じ画面を使うので、プレビューのために取り込み直さない。
    /// </summary>
    public void CopyPreview(Action<byte[], int, int> write)
    {
        Canvas canvas;
        lock (_videoLock)
        {
            canvas = _latest;
            canvas.Refs++;
        }
        try
        {
            write(canvas.Data, Width, Height);
        }
        finally
        {
            ReleaseCanvas(canvas);
        }
    }

    // ───────── 止める ─────────

    private void Fail(string message)
    {
        if (_stopping) return;
        _failure = message;
        Failed?.Invoke(message);
    }

    /// <summary>止めて、ファイルを書き終えるまで待つ。録画できていなければ例外。</summary>
    public async Task StopAsync()
    {
        if (!_stopping)
        {
            _stopAt = Elapsed;
            _stopping = true;
            _frameSignal.Release();
        }
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
        // 書き出しが終わるのを待つ (最後の音と画面を渡し終えると終わる)
        bool finished = await Task.WhenAny(_transcode, Task.Delay(TimeSpan.FromSeconds(30))) == _transcode;
        if (!finished) App.Log("RecordStop", new TimeoutException("録画の書き出しが 30 秒で終わりませんでした"));
        _windowAudio?.Dispose();
        _micAudio?.Dispose();
        _stream?.Dispose();
        _stream = null;
        try { _device.Dispose(); } catch { } // (GPU の資源。放っておくと録画のたびに残る)
        if (_failure != null && FileSize == 0) throw new InvalidOperationException(_failure);
        // 書き出しが途中で失敗した・終わらなかったファイルは、終わりの目次が無く再生できない
        if (_transcodeError != null) throw new InvalidOperationException("録画の書き出しに失敗しました: " + _transcodeError.Message, _transcodeError);
        if (!finished) throw new TimeoutException("録画の書き出しが終わりませんでした (ファイルが壊れている可能性があります)");
    }

    // ───────── 音のたまり場 ─────────

    private sealed class ByteQueue
    {
        private readonly object _lock = new();
        private readonly Queue<byte[]> _chunks = new();
        private int _offset; // 先頭の塊の読んだ位置
        private long _length;

        public void Enqueue(byte[] data)
        {
            if (data.Length == 0) return;
            lock (_lock)
            {
                _chunks.Enqueue(data);
                _length += data.Length;
            }
        }

        /// <summary>先頭に足す (読みかけの塊は、読んだ分を除いてから後ろに続ける)。</summary>
        public void PushFront(byte[] data)
        {
            if (data.Length == 0) return;
            lock (_lock)
            {
                var rest = _chunks.ToArray();
                _chunks.Clear();
                _chunks.Enqueue(data);
                for (int i = 0; i < rest.Length; i++)
                    _chunks.Enqueue(i == 0 && _offset > 0 ? rest[0][_offset..] : rest[i]);
                _offset = 0;
                _length += data.Length;
            }
        }

        public int Read(byte[] destination)
        {
            lock (_lock)
            {
                int written = 0;
                while (written < destination.Length && _chunks.Count > 0)
                {
                    var head = _chunks.Peek();
                    int n = Math.Min(head.Length - _offset, destination.Length - written);
                    Buffer.BlockCopy(head, _offset, destination, written, n);
                    written += n;
                    _offset += n;
                    _length -= n;
                    if (_offset == head.Length)
                    {
                        _chunks.Dequeue();
                        _offset = 0;
                    }
                }
                return written;
            }
        }

        /// <summary>たまった量が limit バイトを超えていたら、古い分を捨てる (4 バイト単位)。</summary>
        public void DropExcess(long limit)
        {
            lock (_lock)
            {
                long excess = _length - Math.Max(0, limit);
                excess -= excess % 4;
                if (excess <= 0) return;
                var trash = new byte[Math.Min(excess, int.MaxValue)];
                Monitor.Exit(_lock);
                try { Read(trash); }
                finally { Monitor.Enter(_lock); }
            }
        }
    }

    // ───────── Windows の部品 ─────────

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(long point, uint flags);

    /// <summary>メインの画面 (モニター)。</summary>
    public static IntPtr PrimaryMonitor() => MonitorFromPoint(0, 1 /* MONITOR_DEFAULTTOPRIMARY */);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    private static readonly Guid GraphicsCaptureItemIid = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");

    internal static IDirect3DDevice CreateDevice()
    {
        const uint BgraSupport = 0x20;
        int hr = D3D11CreateDevice(IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, 7, out var d3d, out _, out var context);
        if (hr < 0) hr = D3D11CreateDevice(IntPtr.Zero, 5 /* WARP (GPU が使えないとき) */, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, 7, out d3d, out _, out context);
        Marshal.ThrowExceptionForHR(hr);
        if (context != IntPtr.Zero) Marshal.Release(context);
        try
        {
            var dxgiIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in dxgiIid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try
                {
                    return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgi);
            }
        }
        finally
        {
            Marshal.Release(d3d);
        }
    }

    private static IGraphicsCaptureItemInterop ItemInterop()
    {
        var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var iid = typeof(IGraphicsCaptureItemInterop).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in iid, out var pointer));
        try
        {
            return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    internal static GraphicsCaptureItem CreateItemForWindow(IntPtr window)
    {
        var iid = GraphicsCaptureItemIid;
        Marshal.ThrowExceptionForHR(ItemInterop().CreateForWindow(window, ref iid, out var pointer));
        try { return GraphicsCaptureItem.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    internal static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        var iid = GraphicsCaptureItemIid;
        Marshal.ThrowExceptionForHR(ItemInterop().CreateForMonitor(monitor, ref iid, out var pointer));
        try { return GraphicsCaptureItem.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutBoolean(IntPtr self, byte value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutTimeSpan(IntPtr self, long ticks);

    /// <summary>
    /// 取り込む画面の間隔の下限を決める (Windows 11 24H2 以降。既定では 1 秒に 60 枚ほどに抑えられるので、
    /// 120・144・240 コマ/秒のときは短くする)。使えない Windows では何もしない。
    /// </summary>
    internal static void SetMinUpdateInterval(GraphicsCaptureSession session, TimeSpan interval)
    {
        try
        {
            var unknown = ((WinRT.IWinRTObject)session).NativeObject.ThisPtr;
            var iid = new Guid("67c0ea62-1f85-5061-925a-239be0ac09cb"); // IGraphicsCaptureSession5 (MinUpdateInterval)
            if (Marshal.QueryInterface(unknown, in iid, out var session5) < 0) return;
            try
            {
                var vtable = Marshal.ReadIntPtr(session5);
                var put = Marshal.GetDelegateForFunctionPointer<PutTimeSpan>(Marshal.ReadIntPtr(vtable, 7 * IntPtr.Size)); // put_MinUpdateInterval
                put(session5, interval.Ticks);
            }
            finally
            {
                Marshal.Release(session5);
            }
        }
        catch (Exception ex)
        {
            App.Log("RecordInterval", ex);
        }
    }

    /// <summary>
    /// 取り込んでいるウィンドウの周りの黄色い枠を出さない (Windows 11。この PC の画面に出るだけで、録画には入らない)。
    /// 使えない Windows では何もしない。
    /// </summary>
    internal static void HideYellowBorder(GraphicsCaptureSession session)
    {
        try
        {
            var unknown = ((WinRT.IWinRTObject)session).NativeObject.ThisPtr;
            var iid = new Guid("f2cdd966-22ae-5ea1-9596-3a289344c3be"); // IGraphicsCaptureSession3 (IsBorderRequired)
            if (Marshal.QueryInterface(unknown, in iid, out var session3) < 0) return;
            try
            {
                var vtable = Marshal.ReadIntPtr(session3);
                var put = Marshal.GetDelegateForFunctionPointer<PutBoolean>(Marshal.ReadIntPtr(vtable, 7 * IntPtr.Size)); // put_IsBorderRequired
                put(session3, 0);
            }
            finally
            {
                Marshal.Release(session3);
            }
        }
        catch (Exception ex)
        {
            App.Log("RecordBorder", ex);
        }
    }
}
