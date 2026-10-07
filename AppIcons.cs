using System.Text.RegularExpressions;

namespace GetText;

/// <summary>
/// 意味で名前を付けたアイコン。画面では文字コードや絵を直接書かず、この名前を使う
/// (Windows は Segoe Fluent Icons、Mac は線で描いた記号に置き換える: WindowsIcons・MacIcons)。
/// 拡張機能 (プラグイン) もこの名前か、確かめた SVG の線 (IconSource.Svg) でアイコンを指定する (絵文字は使えない)。
/// </summary>
public enum AppIcon
{
    None,
    ScreenOcr,
    Meeting,
    Recording,
    Presentation,
    Search,
    Pin,
    Extensions,
    Settings,
    Refresh,
    Pause,
    Play,
    Stop,
    Copy,
    Save,
    History,
    Layout,
    Groups,
    Warning,
    Error,
    Info,
    Success,
    Close,
    More,
    ChevronRight,
    ChevronDown,
    ArrowUp,
    ArrowDown,
    Add,
    Download,
    Folder,
    Document,
    Translate,
    Accumulate,
    Clear,
    Command,
    Home,
    Exit,
    Local,
    Cloud,
    Privacy,
    Keyboard,
    Appearance,
    Model,
    Diagnostics,
    Microphone,
    Video,
    Volume,
    Monitor,
}

/// <summary>アイコンの指定 (決まった名前、または拡張機能が渡す SVG の線)。</summary>
public sealed record IconSource(AppIcon Semantic, string? SvgPath = null)
{
    public static implicit operator IconSource(AppIcon icon) => new(icon);

    /// <summary>拡張機能のアイコン: 24×24 の SVG の path の d (線・曲線の命令と数だけ)。確かめられなければ null。</summary>
    public static IconSource? Svg(string pathData, AppIcon fallback = AppIcon.Extensions) =>
        IconValidator.IsValidSvgPath(pathData) ? new IconSource(fallback, pathData) : null;
}

/// <summary>拡張機能が渡すアイコンを確かめる (絵文字・画像・スクリプトは受け付けない)。</summary>
public static class IconValidator
{
    private static readonly Regex PathData = new(@"^[MmLlHhVvCcSsQqTtAaZz0-9eE\.,\-\+\s]+$", RegexOptions.CultureInvariant);

    public static bool IsValidSvgPath(string? d) =>
        !string.IsNullOrWhiteSpace(d) && d.Length <= 4096 && PathData.IsMatch(d) && d.TrimStart()[0] is 'M' or 'm';

    /// <summary>
    /// 一覧の名前やアイコンとして使えない文字 (絵文字・記号の絵) が入っているか
    /// (拡張機能が名前の頭に絵文字を付けて、アイコンの代わりにするのを防ぐ)。
    /// </summary>
    public static bool HasPictograph(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            int cp = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i) : text[i];
            if (char.IsSurrogatePair(text, i)) i++;
            if (cp is >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or >= 0x2B00 and <= 0x2BFF or 0xFE0F) return true;
        }
        return false;
    }
}
