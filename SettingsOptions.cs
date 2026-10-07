namespace GetText;

/// <summary>コンボボックスの選択肢 (表示名と値)。</summary>
public sealed record Option<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>画面の録画の選べるもの (Windows 版・Mac 版で共通)。</summary>
public static class RecordOptionsList
{
    /// <summary>1 秒あたりのコマ数。画面の更新 (リフレッシュレート) より多くはならない。</summary>
    public static readonly Option<int>[] Fps =
    [
        new("15 コマ/秒", 15), new("30 コマ/秒", 30), new("60 コマ/秒", 60),
        new("120 コマ/秒", 120), new("144 コマ/秒", 144), new("240 コマ/秒", 240),
    ];

    public static readonly Option<bool>[] Quality = [new("標準 (小さいファイル)", false), new("高画質", true)];
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>読み取った文字の並べ方。</summary>
public enum OcrView
{
    /// <summary>上から順 (画面どおりの改行)。</summary>
    Text,
    /// <summary>枠・離れたかたまりごとに分けて、見出しを付ける (まとまりが無ければ上から順)。</summary>
    Groups,
}

/// <summary>設定のページ (一般 / 機能 / 拡張 / 詳細 の順)。番号ではなくこの名前で開く。</summary>
public enum SettingsPage
{
    Appearance,
    Shortcuts,
    Privacy,
    ScreenOcr,
    Translation,
    Meetings,
    Recording,
    Models,
    Extensions,
    Diagnostics,
}

public enum PaneLayout
{
    /// <summary>日本語訳を原文の下に表示する。</summary>
    Below,
    /// <summary>日本語訳を原文の右に表示する。</summary>
    Right,
}

/// <summary>設定画面とメイン画面で共有する選択肢。</summary>
public static class SettingsOptions
{
    public const string AutoLanguage = "auto";

    public static readonly Option<int>[] Intervals =
    [
        new("0.3 秒", 300), new("0.5 秒", 500), new("1 秒", 1000),
        new("2 秒", 2000), new("3 秒", 3000), new("5 秒", 5000),
    ];

    public static readonly Option<double>[] Scales =
    [
        new("自動", 0), new("1 倍", 1), new("2 倍", 2), new("3 倍", 3), new("4 倍", 4),
    ];

    public static readonly Option<OcrEngineKind>[] OcrEngines =
    [
        new("AI (PP-OCRv6) — 高精度・記号にも強い", OcrEngineKind.Ai),
        new("Windows 標準 — 軽量・追加インストール不要", OcrEngineKind.Windows),
    ];

    public static readonly Option<TranslationEngine>[] TranslationEngines =
    [
        new("ローカル — オフライン・回数無制限", TranslationEngine.Local),
        new("Google — キー不要 (オンライン)", TranslationEngine.Google),
        new("DeepL — API キーが必要 (オンライン)", TranslationEngine.DeepL),
    ];

    public static readonly Option<ConvertMode>[] ConvertModes =
        TextConverter.Modes.Select(m => new Option<ConvertMode>(m.Label, m.Mode)).ToArray();

    public static readonly Option<AppTheme>[] Themes =
    [
        new("Windows の設定に合わせる", AppTheme.System), new("ライト", AppTheme.Light), new("ダーク", AppTheme.Dark),
    ];

    public static readonly Option<PaneLayout>[] Layouts =
    [
        new("原文の下", PaneLayout.Below), new("原文の右", PaneLayout.Right),
    ];

    public static readonly Option<OcrView>[] OcrViews =
    [
        new("上から順", OcrView.Text), new("枠・まとまりごと", OcrView.Groups),
    ];

    public static Option<T> Find<T>(IEnumerable<Option<T>> options, T value) =>
        options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, value)) ?? options.First();
}
