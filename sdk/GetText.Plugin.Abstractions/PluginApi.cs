namespace GetText.Plugins;

/// <summary>Plugin API の版。マニフェストの apiVersion と同じ数字。</summary>
public static class PluginApi
{
    public const int Version = 1;
}

/// <summary>
/// 拡張機能の入口。GetText は起動したときに 1 回だけ Initialize を呼び、終わるときに Shutdown を呼ぶ。
/// Initialize ではコマンド・機能・設定・提供元を登録するだけにして、重い処理 (モデルの読み込みなど) はしない。
/// 同じプロセスで動くので、GetText と同じ権限を持つ (マニフェストの permissions は「使うと宣言したもの」で、OS が制限するものではない)。
/// </summary>
public interface IGetTextPlugin
{
    void Initialize(IPluginContext context);

    /// <summary>終わるときに呼ばれる (タイマー・ファイル・プロセスを片付ける)。例外を投げても GetText は終わる。</summary>
    void Shutdown();
}

/// <summary>GetText が拡張機能に渡すもの。</summary>
public interface IPluginContext
{
    /// <summary>この拡張機能の id (マニフェストの id)。</summary>
    string PluginId { get; }

    /// <summary>この拡張機能が自由に読み書きできるフォルダ (版をまたいで残る。アンインストールで消すかは利用者が選ぶ)。</summary>
    string DataDirectory { get; }

    HostInfo Host { get; }

    IPluginLogger Log { get; }

    /// <summary>この拡張機能の設定の値 (GetText が保存する)。</summary>
    IPluginSettings Settings { get; }

    void AddCommand(PluginCommand command);

    void AddFeature(PluginFeature feature);

    /// <summary>設定のページ (GetText の設定の「拡張機能」に、GetText の部品で表示する)。</summary>
    void AddSettings(PluginSettingsPage page);

    /// <summary>検索の提供元 (履歴など。GetText の検索の画面で表示する)。</summary>
    void AddSearchProvider(IPluginSearchProvider provider);

    void AddOcrProvider(IOcrProvider provider);

    void AddExportProvider(IExportProvider provider);

    /// <summary>GetText の画面の右下に短い知らせを出す (OS の通知ではない)。</summary>
    void Notify(PluginNotification notification);

    /// <summary>読み取りの画面が新しく読み取るたび (画面が変わったときだけ) に呼ばれる。UI のスレッドとは別のスレッドで呼ぶ。</summary>
    event EventHandler<OcrFrameEventArgs> OcrFrameRead;

    /// <summary>GetText が持っている機能 (capability) を使う。無ければ null (例: 画面の取り込み・OCR)。</summary>
    T? GetService<T>() where T : class;

    /// <summary>ほかの拡張機能・GetText が、その capability を持っているか (例: "meeting.transcript")。</summary>
    bool HasCapability(string capability);
}

/// <summary>GetText の版と動いている環境。</summary>
public sealed record HostInfo(Version HostVersion, int ApiVersion, string Platform, string UiLanguage);

public interface IPluginLogger
{
    /// <summary>ログに書く (OCR の文字・訳・議事録の文・キーは書かないこと)。</summary>
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}

public interface IPluginSettings
{
    string? Get(string key);

    void Set(string key, string? value);

    bool GetBool(string key, bool fallback);

    double GetNumber(string key, double fallback);

    /// <summary>値が変わった (設定の画面で変えた・Set した)。引数は key。</summary>
    event Action<string>? Changed;
}

/// <summary>言語ごとの文字 (今は ja / en)。足りない言語は ja → en → 最初の値の順で使う。</summary>
public sealed class LocalizedText
{
    public LocalizedText(string text) => Values = new Dictionary<string, string> { ["ja"] = text };

    public LocalizedText(IReadOnlyDictionary<string, string> values) => Values = values;

    public IReadOnlyDictionary<string, string> Values { get; }

    public string For(string language) =>
        Values.TryGetValue(language, out var v) ? v
        : Values.TryGetValue("ja", out var ja) ? ja
        : Values.TryGetValue("en", out var en) ? en
        : Values.Values.FirstOrDefault() ?? "";

    public static implicit operator LocalizedText(string text) => new(text);

    public override string ToString() => For("ja");
}

/// <summary>
/// アイコン: GetText の意味の名前 (Search・Recording・History など。docs/design/DESIGN.md) か、24×24 の SVG の線 (path の d)。
/// 絵文字は使えない。
/// </summary>
public sealed record PluginIcon(string? Name, string? SvgPath = null)
{
    public static implicit operator PluginIcon(string name) => new(name);
}

/// <summary>コマンドの一覧 (Ctrl+K / ⌘K) に出す操作。</summary>
public sealed class PluginCommand
{
    public required string Id { get; init; }
    public required LocalizedText Title { get; init; }
    public LocalizedText? Subtitle { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public PluginIcon Icon { get; init; } = "Extensions";
    public Func<bool> IsAvailable { get; init; } = () => true;
    /// <summary>実行 (例外は GetText が受け止めてログに書き、知らせを出す)。</summary>
    public required Func<CancellationToken, Task> Execute { get; init; }
}

public enum PluginFeatureState
{
    Closed,
    Open,
    Active,
}

public sealed record PluginFeatureStatus(PluginFeatureState State, LocalizedText? Label = null);

/// <summary>機能を出す場所。</summary>
public enum PluginFeaturePlacement
{
    /// <summary>ホームのタイル (ひとりで使える機能)。</summary>
    Home,
    /// <summary>
    /// 読み取りの画面の「表示」メニューの切り替え (読み取りの画面が読んだ文字を使う機能。読み取りの画面を開いていないと動かないので、
    /// ホームには出さない)。Close が要る (無ければホームに出す)。
    /// </summary>
    ReadingWindow,
}

/// <summary>ホームのタイル (または読み取りの画面の切り替え) に出す機能。</summary>
public sealed class PluginFeature
{
    public required string Id { get; init; }
    /// <summary>出す場所 (既定はホーム)。</summary>
    public PluginFeaturePlacement Placement { get; init; } = PluginFeaturePlacement.Home;
    public required LocalizedText Name { get; init; }
    public required LocalizedText Description { get; init; }
    public PluginIcon Icon { get; init; } = "Extensions";
    public Func<PluginFeatureStatus> Status { get; init; } = () => new(PluginFeatureState.Closed);
    /// <summary>タイルを押したとき (多くはコマンドを実行するか、設定を開く)。</summary>
    public required Func<CancellationToken, Task> Open { get; init; }
    public Func<CancellationToken, Task>? Close { get; init; }
}

public enum PluginSettingKind
{
    Toggle,
    Text,
    MultilineText,
    Number,
    Choice,
}

/// <summary>設定の 1 行 (名前・短い説明・操作の部品は GetText が描く)。</summary>
public sealed class PluginSetting
{
    public required string Key { get; init; }
    public required LocalizedText Label { get; init; }
    public LocalizedText? Description { get; init; }
    public PluginSettingKind Kind { get; init; } = PluginSettingKind.Toggle;
    public string? Default { get; init; }
    /// <summary>Choice の選択肢 (値, 表示名)。</summary>
    public IReadOnlyList<(string Value, LocalizedText Label)> Choices { get; init; } = [];
    public double Minimum { get; init; }
    public double Maximum { get; init; } = 100;
    /// <summary>詳細設定にたたむ (技術的な項目)。</summary>
    public bool Advanced { get; init; }
}

public sealed class PluginSettingsPage
{
    public required LocalizedText Title { get; init; }
    public IReadOnlyList<PluginSetting> Items { get; init; } = [];
}

public enum PluginNotificationKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>GetText の画面の右下に出す短い知らせ。ActionLabel と Action があればボタンを出す。</summary>
public sealed record PluginNotification(LocalizedText Title, LocalizedText? Message = null, PluginNotificationKind Kind = PluginNotificationKind.Info,
    LocalizedText? ActionLabel = null, Action? Action = null, bool PlaySound = false);

/// <summary>読み取った 1 行 (座標は画面の物理ピクセル)。</summary>
public sealed record OcrTextLine(string Text, double Left, double Top, double Right, double Bottom);

/// <summary>読み取りの画面が読み取った 1 回分。</summary>
public sealed class OcrFrameEventArgs : EventArgs
{
    public required DateTimeOffset Time { get; init; }
    public required IReadOnlyList<OcrTextLine> Lines { get; init; }
    /// <summary>表示している文字 (改行区切り)。</summary>
    public required string Text { get; init; }
    public string? Translation { get; init; }
    /// <summary>読み取った範囲 (画面の物理ピクセル)。</summary>
    public required (int X, int Y, int Width, int Height) Region { get; init; }
    /// <summary>手前のアプリの名前 (分からなければ null)。</summary>
    public string? SourceApplication { get; init; }
    public string? SourceWindowTitle { get; init; }
    /// <summary>読み取った画像を取り出す (BGRA・上から。必要なときだけ呼ぶ。null なら取り出せない)。</summary>
    public Func<(byte[] Bgra, int Width, int Height)?>? GetImage { get; init; }
}

/// <summary>検索の提供元 (履歴など)。</summary>
public interface IPluginSearchProvider
{
    string Id { get; }
    LocalizedText Title { get; }
    LocalizedText? Placeholder { get; }
    Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken cancellationToken);
}

public sealed record PluginSearchResult(LocalizedText Title, LocalizedText? Subtitle, DateTimeOffset? Time, string CopyText,
    IReadOnlyList<PluginResultAction>? Actions = null);

public sealed record PluginResultAction(LocalizedText Label, Func<CancellationToken, Task> Execute);
