namespace GetText;

/// <summary>
/// 16kHz・モノラル・16bit PCM の音の取り込み元 (Windows は WASAPI の <see cref="AudioCapture"/>、Mac は ScreenCaptureKit など)。
/// 音が無い間も「受け取ったサンプル数 = 経過時間」になるよう無音を補って渡す。
/// </summary>
public interface IAudioSource : IDisposable
{
    /// <summary>取り込んだ PCM データ (取り込み用のスレッドから呼ばれる)。</summary>
    event Action<byte[]>? DataAvailable;

    /// <summary>取り込みが止まってしまったときのエラー。</summary>
    event Action<Exception>? Failed;

    void Start();
}

/// <summary>OS ごとの取り込み元の作り方。各 OS の実装が起動時に登録する。</summary>
public static class AudioSources
{
    public const int SampleRate = 16000;

    /// <summary>アプリ (プロセス ID) の音。exclude が true なら、そのアプリ以外のすべての音。</summary>
    public static Func<int, bool, IAudioSource> Window { get; set; } =
        (_, _) => throw new PlatformNotSupportedException("この OS ではアプリの音を取り込めません");

    /// <summary>マイク (<see cref="MicrophoneDevice"/>、空なら既定の録音デバイス)。</summary>
    public static Func<IAudioSource> Microphone { get; set; } =
        () => throw new PlatformNotSupportedException("この OS ではマイクの音を取り込めません");

    /// <summary>使うマイクの端末 ID (空なら既定。会議アプリが別のマイクを使っているときに選ぶ)。</summary>
    public static string MicrophoneDevice { get; set; } = "";

    /// <summary>選べるマイク (端末 ID, 名前)。既定のマイクの名前も返す。選べない OS では空。</summary>
    public static Func<(IReadOnlyList<(string Id, string Name)> Devices, string? DefaultName)> ListMicrophones { get; set; } =
        () => ([], null);
}
