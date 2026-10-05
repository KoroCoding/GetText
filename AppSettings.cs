using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GetText;

public enum OcrEngineKind
{
    Ai,
    Windows,
}

public sealed class AppSettings
{
    public double? CaptureLeft { get; set; }
    public double? CaptureTop { get; set; }
    public double CaptureWidth { get; set; } = 520;
    public double CaptureHeight { get; set; } = 320;

    public double? TextLeft { get; set; }
    public double? TextTop { get; set; }
    public double TextWidth { get; set; } = 480;
    public double TextHeight { get; set; } = 600;

    /// <summary>
    /// 枠・文字の画面・議事録の画面の左上 (物理ピクセル [x, y])。拡大率の違うモニターでも同じ場所に戻すため
    /// (CaptureLeft などの WPF の座標は、窓のいるモニターの拡大率で読み替えられてずれる。古い版との互換用に残す)。
    /// </summary>
    public int[]? CapturePixel { get; set; }
    public int[]? TextPixel { get; set; }
    public int[]? MinutesPixel { get; set; }

    public string? Language { get; set; }
    public int IntervalMs { get; set; } = 1000;
    public double Scale { get; set; } = 0; // 0 = 自動
    public bool HighAccuracy { get; set; } = true;
    public OcrEngineKind OcrEngine { get; set; } = OcrEngineKind.Ai;
    public double FontSize { get; set; } = 15;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public PaneLayout Layout { get; set; } = PaneLayout.Below;
    /// <summary>議事録を開いている間、読み取り枠とテキストの画面を隠す。</summary>
    public bool HideOcrDuringMinutes { get; set; }
    /// <summary>議事録で発言の時刻を表示・書き出しする。</summary>
    public bool MinutesShowTime { get; set; } = true;
    /// <summary>議事録で相づち・言いよどみだけの発言を画面と書き出しから除く。</summary>
    public bool MinutesHideFillers { get; set; } = true;
    /// <summary>
    /// 議事録で文字にする言語 (Whisper の言語コード)。1 つならその言語、複数なら発言ごとにその中から判定する。
    /// </summary>
    public List<string> MinutesLanguages { get; set; } = ["ja", "en"];
    /// <summary>前回、議事録の音声認識の準備にかかった秒数 (準備中の表示で目安として出す)。</summary>
    public double? MinutesLoadSeconds { get; set; }
    /// <summary>議事録で、記録した会議の音声も保存する (発言の時刻をクリックして聞ける)。</summary>
    public bool MinutesSaveAudio { get; set; } = true;
    /// <summary>議事録の用語の登録 (人名・社名・専門用語。「九条(くじょう)」「誤り→正しい」も書ける)。</summary>
    public List<string> MinutesTerms { get; set; } = [];
    /// <summary>名前を付けた話者の声を覚え、次の会議で似た声の話者に名前を付ける。</summary>
    public bool MinutesRememberVoices { get; set; } = true;
    /// <summary>議事録の日本語訳を AI (文脈補正と同じ AI) で、前後の発言と用語を使って作る (遅いが流れに合った訳になる)。</summary>
    public bool MinutesAiTranslate { get; set; }
    /// <summary>
    /// 議事録の速さ: "auto" (GPU があれば正確さ優先、無ければ軽さ優先)・"light" (速い日本語の音声認識)・"accurate" (kotoba-whisper)。
    /// </summary>
    public string MinutesSpeed { get; set; } = "auto";

    /// <summary>議事録で使うマイクの端末 ID (空なら既定の通信デバイス)。</summary>
    public string MinutesMicDevice { get; set; } = "";

    /// <summary>どのアプリを使っていても効くキー (操作の名前 → 「Ctrl+Alt+C」など)。無いものは既定のキー。</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = [];

    /// <summary>AI の機能のセットアップの案内を閉じた (次からは出さない)。</summary>
    public bool SetupHintDismissed { get; set; }

    /// <summary>議事録の「保存…」で最後に選んだ形式 (1 = Word、2 = テキスト、3 = Markdown、4 = 議事録データ、5 = SRT、6 = WebVTT)。</summary>
    public int MinutesSaveFilter { get; set; } = 1;
    /// <summary>文脈による補正。null なら速さに合わせる (正確さ優先ならオン、軽さ優先ならオフ)。</summary>
    public bool? MinutesCorrect { get; set; }
    /// <summary>議事録で日本語以外の発言に日本語訳を原文とセットで付ける。</summary>
    public bool MinutesTranslate { get; set; } = true;
    /// <summary>議事録で新しい発言が加わるたびに一番下までスクロールする。</summary>
    public bool MinutesAutoScroll { get; set; } = true;
    /// <summary>議事録の発言の文字の大きさ (pt)。</summary>
    public double MinutesFontSize { get; set; } = 14;
    /// <summary>議事録の画面の位置と大きさ (前回閉じたとき)。</summary>
    public double? MinutesLeft { get; set; }
    public double? MinutesTop { get; set; }
    public double MinutesWidth { get; set; } = 860;
    public double MinutesHeight { get; set; } = 620;
    /// <summary>議事録の話者の欄の幅 (発言との境目をドラッグして変えた幅)。</summary>
    public double MinutesSpeakerWidth { get; set; } = DefaultSpeakerWidth;
    public const double DefaultSpeakerWidth = 220;

    /// <summary>画面の録画の保存先 (空なら「ビデオ\GetText」)。</summary>
    public string RecordFolder { get; set; } = "";
    /// <summary>録画の 1 秒あたりのコマ数。</summary>
    public int RecordFps { get; set; } = 30;
    /// <summary>録画を高画質 (ファイルが大きい) にする。</summary>
    public bool RecordHighQuality { get; set; }
    /// <summary>録画にウィンドウ (アプリ) の音を入れる。</summary>
    public bool RecordAudio { get; set; } = true;
    /// <summary>録画にマイクの音も入れる。</summary>
    public bool RecordMic { get; set; }
    /// <summary>議事録を取りながら、選んだアプリの画面も録画する。</summary>
    public bool MinutesRecordVideo { get; set; }
    /// <summary>議事録の録画に、発言の字幕を焼き込んだ動画も作る。</summary>
    public bool MinutesSubtitledVideo { get; set; } = true;
    /// <summary>録画の画面にプレビューを出す。</summary>
    public bool RecordPreview { get; set; } = true;
    /// <summary>機能を選ぶ画面で機能を開いたら、その画面を最小化する。</summary>
    public bool HomeMinimizeOnOpen { get; set; } = true;
    public bool Follow { get; set; } = true;
    public bool Topmost { get; set; } = true;
    public bool Wrap { get; set; } = true;
    public bool JoinCjk { get; set; } = true;
    public ConvertMode Convert { get; set; } = ConvertMode.None;

    public bool Translate { get; set; } = true;
    public TranslationEngine TranslationEngine { get; set; } = TranslationEngine.Local;
    /// <summary>DPAPI (現在の Windows ユーザーだけが復号できる) で暗号化した DeepL キー。</summary>
    public string? DeepLKeyProtected { get; set; }

    // Windows は %APPDATA%\GetText、Mac は ~/Library/Application Support/GetText
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(OperatingSystem.IsWindows() ? Environment.SpecialFolder.ApplicationData : Environment.SpecialFolder.LocalApplicationData),
        "GetText", "settings.json");

    /// <summary>設定ファイルの場所。</summary>
    public static string SettingsPath => FilePath;

    /// <summary>
    /// DeepL キーの保管場所 (Mac 版はキーチェーン)。null なら Windows の DPAPI で暗号化して設定ファイルに置く。
    /// </summary>
    public static ISecretStore? SecretStore { get; set; }

    /// <summary>キーチェーンなどに置いたことを示す、設定ファイルに書く印。</summary>
    private const string StoredElsewhere = "secret-store";

    public string? GetDeepLKey()
    {
        if (string.IsNullOrEmpty(DeepLKeyProtected)) return null;
        if (SecretStore is { } store) return DeepLKeyProtected == StoredElsewhere ? store.Get("DeepL") : null;
        try
        {
            var bytes = ProtectedData.Unprotect(System.Convert.FromBase64String(DeepLKeyProtected), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    public void SetDeepLKey(string? key)
    {
        if (SecretStore is { } store)
        {
            if (string.IsNullOrWhiteSpace(key)) store.Delete("DeepL");
            else store.Set("DeepL", key.Trim());
            DeepLKeyProtected = string.IsNullOrWhiteSpace(key) ? null : StoredElsewhere;
            return;
        }
        DeepLKeyProtected = string.IsNullOrWhiteSpace(key)
            ? null
            : System.Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
    }

    public static AppSettings Load()
    {
        // 保存の途中で落ちて壊れていたら、ひとつ前に保存した設定 (.bak) を使う
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
                    loaded.Normalize();
                    return loaded;
                }
            }
            catch
            {
                // 壊れた設定は飛ばす
            }
        }
        return new AppSettings();
    }

    /// <summary>
    /// 手で書き換えた設定ファイルなどのおかしな値を、使える範囲に直す (負の大きさで窓を作れない・null の一覧で落ちる・
    /// 知らない翻訳の方式で外部に送る、などを防ぐ。1 つの値のせいで設定がすべて既定に戻らないように)。
    /// </summary>
    public void Normalize()
    {
        static double Size(double v, double min, double def) => double.IsFinite(v) && v >= min ? Math.Min(v, 20000) : def;
        static T Defined<T>(T v, T def) where T : struct, Enum => Enum.IsDefined(v) ? v : def;
        CaptureWidth = Size(CaptureWidth, 140, 520);
        CaptureHeight = Size(CaptureHeight, 90, 320);
        TextWidth = Size(TextWidth, 360, 480);
        TextHeight = Size(TextHeight, 260, 600);
        MinutesWidth = Size(MinutesWidth, 560, 860);
        MinutesHeight = Size(MinutesHeight, 380, 620);
        MinutesSpeakerWidth = Size(MinutesSpeakerWidth, 170, DefaultSpeakerWidth);
        IntervalMs = Math.Clamp(IntervalMs, 300, 60000);
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, 0, 4) : 0;
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 8, 48) : 15;
        MinutesFontSize = double.IsFinite(MinutesFontSize) ? Math.Clamp(MinutesFontSize, 8, 48) : 14;
        OcrEngine = Defined(OcrEngine, OcrEngineKind.Ai);
        Theme = Defined(Theme, AppTheme.System);
        Layout = Defined(Layout, PaneLayout.Below);
        Convert = Defined(Convert, ConvertMode.None);
        TranslationEngine = Defined(TranslationEngine, TranslationEngine.Local);
        Hotkeys ??= [];
        MinutesLanguages ??= ["ja", "en"];
        MinutesTerms ??= [];
        MinutesSpeed ??= "auto";
        MinutesMicDevice ??= "";
        RecordFolder ??= "";
        RecordFps = RecordOptionsList.Fps.Any(o => o.Value == RecordFps) ? RecordFps : 30;
    }

    public void Save()
    {
        if (App.DemoMode) return; // 見本の画面を作るときは利用者の設定を書き換えない
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // 一時ファイルに書いてから入れ替える (書いている途中で落ちても、設定や DeepL のキーが消えないように)
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, FilePath);
        }
        catch
        {
            // 保存失敗は致命的ではない
        }
    }
}

/// <summary>キーなどの秘密の値の保管場所 (Mac 版のキーチェーンなど)。</summary>
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
}
