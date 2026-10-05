using System.Runtime.InteropServices;

namespace GetText;

/// <summary>
/// WASAPI で音声を 16kHz・モノラル・16bit PCM として取り込む。
/// ・<see cref="ForProcess"/>: 指定したアプリ (プロセスとその子プロセス) が再生している音だけ (Windows 10 2004 以降)
/// ・<see cref="ForMicrophone"/>: 既定の録音デバイス (マイク)
/// </summary>
public sealed class AudioCapture : IAudioSource
{
    public const int SampleRate = AudioSources.SampleRate;

    // 議事録の取り込み元として登録する (Mac 版は別の実装を登録する)
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register()
    {
        AudioSources.Window = (pid, exclude) => exclude ? ForAllExcept(pid) : ForProcess(pid);
        AudioSources.Microphone = () => ForMicrophone(AudioSources.MicrophoneDevice);
        AudioSources.ListMicrophones = ListMicrophones;
    }

    /// <summary>取り込んだ PCM データ (取り込み用のスレッドから呼ばれる)。</summary>
    public event Action<byte[]>? DataAvailable;

    /// <summary>取り込みが止まってしまったときのエラー。</summary>
    public event Action<Exception>? Failed;

    private readonly Func<IAudioClient> _activate;
    private readonly bool _isLoopback;
    private readonly int _rate;
    private readonly int _channels;
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>取り込む形式 (既定は議事録用の 16kHz・モノラル。画面の録画では 48kHz・ステレオ)。</summary>
    private AudioCapture(Func<IAudioClient> activate, bool isLoopback, int rate = SampleRate, int channels = 1)
    {
        _activate = activate;
        _isLoopback = isLoopback;
        _rate = rate;
        _channels = channels;
    }

    public int Rate => _rate;
    public int Channels => _channels;

    public static AudioCapture ForProcess(int processId, int rate = SampleRate, int channels = 1) =>
        new(() => ActivateProcessLoopback(processId, exclude: false), isLoopback: true, rate, channels);

    /// <summary>指定したプロセス (とその子プロセス) 以外が再生しているすべての音。</summary>
    public static AudioCapture ForAllExcept(int processId, int rate = SampleRate, int channels = 1) =>
        new(() => ActivateProcessLoopback(processId, exclude: true), isLoopback: true, rate, channels);

    /// <summary>マイク。deviceId が空なら「既定の通信デバイス」(通話アプリが使うマイク)。</summary>
    public static AudioCapture ForMicrophone(string? deviceId = null, int rate = SampleRate, int channels = 1) =>
        new(() => ActivateMicrophone(deviceId), isLoopback: false, rate, channels);

    /// <summary>使えるマイクの一覧 (端末 ID, 名前) と、既定の通信デバイスの名前。</summary>
    public static (IReadOnlyList<(string Id, string Name)> Devices, string? DefaultName) ListMicrophones()
    {
        var list = new List<(string, string)>();
        string? defaultName = null;
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            if (enumerator.EnumAudioEndpoints(1 /* eCapture */, 1 /* DEVICE_STATE_ACTIVE */, out var collection) >= 0)
            {
                collection.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) < 0) continue;
                    if (device.GetId(out var id) >= 0) list.Add((id, FriendlyName(device) ?? id));
                    Marshal.ReleaseComObject(device);
                }
                Marshal.ReleaseComObject(collection);
            }
            if (enumerator.GetDefaultAudioEndpoint(1, 2 /* eCommunications */, out var def) >= 0)
            {
                defaultName = FriendlyName(def);
                Marshal.ReleaseComObject(def);
            }
        }
        catch (Exception)
        {
            // 一覧を取れなくても、既定のマイクで記録できる
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        return (list, defaultName);
    }

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) < 0) return null;
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 }; // PKEY_Device_FriendlyName
            if (store.GetValue(ref key, out var value) < 0) return null;
            try
            {
                return value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        var ready = new ManualResetEventSlim();
        Exception? startError = null;
        // WASAPI の COM オブジェクトはすべて MTA の専用スレッドで作って使う
        _thread = new Thread(() => Run(ready, e => startError = e)) { IsBackground = true, Name = "AudioCapture" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        ready.Wait();
        if (startError != null)
        {
            _running = false;
            throw startError;
        }
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(2000);
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Run(ManualResetEventSlim ready, Action<Exception> reportStartError)
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        using var dataEvent = new AutoResetEvent(false);
        try
        {
            try
            {
                client = _activate();
                var format = new WAVEFORMATEX
                {
                    wFormatTag = 1, // PCM
                    nChannels = (ushort)_channels,
                    nSamplesPerSec = _rate,
                    wBitsPerSample = 16,
                    nBlockAlign = (ushort)(2 * _channels),
                    nAvgBytesPerSec = _rate * 2 * _channels,
                };
                // AUTOCONVERTPCM: 端末の形式から指定した形式 (16kHz モノラルなど) へ Windows が変換する
                uint flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                if (_isLoopback) flags |= AUDCLNT_STREAMFLAGS_LOOPBACK;
                // バッファは 1 秒 (PC が忙しくて取り込みが少し遅れても、音が途切れないように)
                Check(client.Initialize(0 /* SHARED */, flags, 10_000_000 /* 1 秒 */, 0, ref format, IntPtr.Zero));
                Check(client.SetEventHandle(dataEvent.SafeWaitHandle.DangerousGetHandle()));
                var iid = typeof(IAudioCaptureClient).GUID;
                Check(client.GetService(ref iid, out var service));
                capture = (IAudioCaptureClient)service;
                Check(client.Start());
            }
            catch (Exception ex)
            {
                reportStartError(ex);
                return;
            }
            finally
            {
                ready.Set();
            }

            // アプリが音を出していない間は Windows からデータが来ないので、経過時間の分だけ無音を補って
            // 「受け取ったサンプル数 = 経過時間」を保つ (発言の時刻がずれないようにするため)
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long delivered = 0;
            while (_running)
            {
                dataEvent.WaitOne(100);
                while (true)
                {
                    Check(capture.GetNextPacketSize(out uint packet));
                    if (packet == 0) break;
                    Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint bufferFlags, out _, out _));
                    var bytes = new byte[frames * 2 * _channels];
                    if ((bufferFlags & AUDCLNT_BUFFERFLAGS_SILENT) == 0) Marshal.Copy(data, bytes, 0, bytes.Length);
                    Check(capture.ReleaseBuffer(frames));
                    if (bytes.Length == 0) continue;
                    delivered += frames;
                    DataAvailable?.Invoke(bytes);
                }
                long expected = (long)(clock.Elapsed.TotalSeconds * _rate);
                long missing = expected - delivered - _rate / 5; // 0.2 秒以上遅れていたら補う
                // スリープからの復帰などで大きく空いたときも、1 秒ずつに分けて送る (一度に数百 MB を確保しないように)
                while (missing > 0 && _running)
                {
                    long n = Math.Min(missing, _rate);
                    delivered += n;
                    missing -= n;
                    DataAvailable?.Invoke(new byte[n * 2 * _channels]);
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            if (_running) Failed?.Invoke(ex);
        }
        finally
        {
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    // ───────── 取り込み元の作成 ─────────

    private static IAudioClient ActivateMicrophone(string? deviceId)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            // 選んだマイク → 通話アプリと同じ「既定の通信デバイス」→ 既定の録音デバイス (選んだマイクが外されていたら既定に戻す)
            IMMDevice? device = null;
            if (!string.IsNullOrEmpty(deviceId) && enumerator.GetDevice(deviceId, out var chosen) >= 0)
            {
                // 抜いたマイクも GetDevice では返るので、使える状態 (DEVICE_STATE_ACTIVE) かを確かめる
                if (chosen.GetState(out int state) >= 0 && state == 1) device = chosen;
                else Marshal.ReleaseComObject(chosen);
            }
            if (device == null && enumerator.GetDefaultAudioEndpoint(1 /* eCapture */, 2 /* eCommunications */, out var comm) >= 0) device = comm;
            if (device == null) Check(enumerator.GetDefaultAudioEndpoint(1 /* eCapture */, 0 /* eConsole */, out device));
            var iid = typeof(IAudioClient).GUID;
            Check(device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var client));
            Marshal.ReleaseComObject(device);
            return (IAudioClient)client;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static IAudioClient ActivateProcessLoopback(int processId, bool exclude)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = PROCESS_LOOPBACK, TargetProcessId,
        //   INCLUDE_TARGET_PROCESS_TREE (0) / EXCLUDE_TARGET_PROCESS_TREE (1) }
        var paramsPtr = Marshal.AllocHGlobal(12);
        var propPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.WriteInt32(paramsPtr, 0, 1);
            Marshal.WriteInt32(paramsPtr, 4, processId);
            Marshal.WriteInt32(paramsPtr, 8, exclude ? 1 : 0);
            Marshal.StructureToPtr(new PROPVARIANT_BLOB { vt = 65 /* VT_BLOB */, cbSize = 12, pBlobData = paramsPtr }, propPtr, false);

            var handler = new ActivationHandler();
            var iid = typeof(IAudioClient).GUID;
            Check(ActivateAudioInterfaceAsync(VirtualAudioDeviceProcessLoopback, ref iid, propPtr, handler, out var operation));
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("音声の取り込みを開始できませんでした (タイムアウト)");
            Check(operation.GetActivateResult(out int hr, out var activated));
            Check(hr);
            return (IAudioClient)activated;
        }
        finally
        {
            Marshal.FreeHGlobal(propPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    // ───────── COM 定義 ─────────

    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT_BLOB
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public int cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT value);

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WAVEFORMATEX format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    /// <summary>完了通知は別スレッドから直接呼ばれるので、マーシャリング不要 (IAgileObject) を示す。</summary>
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject;

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Done { get; } = new();
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Done.Set();
    }
}
