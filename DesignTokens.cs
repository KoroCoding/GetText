namespace GetText;

/// <summary>
/// 画面の見た目の決まり (デザイントークン)。色・文字の大きさ・余白・角の丸みを、画面ごとに直接書かずにここから使う。
/// Windows 版 (WPF・Fluent 2 に合わせる) と Mac 版 (Avalonia・Apple の HIG に合わせる) で値は違うが、名前と意味は同じ。
/// 色は意味で名前を付ける (Accent は操作の主役・選択中だけ、Success・Warning・Critical は状態を伝えるときだけに使う)。
/// 文字と背景のコントラストは単体テスト (DesignTokenTests) で確かめる (普通の文字 4.5:1 以上、アイコン・大きな文字 3:1 以上)。
/// </summary>
public static class DesignTokens
{
    /// <summary>余白の段階 (px)。これ以外の値は使わない。</summary>
    public static readonly double[] SpacingRamp = [2, 4, 8, 12, 16, 24, 32, 48];

    public const double Space2 = 2, Space4 = 4, Space8 = 8, Space12 = 12, Space16 = 16, Space24 = 24, Space32 = 32, Space48 = 48;

    /// <summary>キーボードで選んでいる所を示す枠の太さ。</summary>
    public const double FocusStroke = 2;

    public static Palette Colors(bool mac, bool dark) => (mac, dark) switch
    {
        (false, false) => WindowsLight,
        (false, true) => WindowsDark,
        (true, false) => MacLight,
        (true, true) => MacDark,
    };

    public static TypeRamp Type(bool mac) => mac ? MacType : WindowsType;

    public static Radii Corners(bool mac) => mac ? MacRadii : WindowsRadii;

    // ───────── Windows (Fluent 2) ─────────

    public static readonly Palette WindowsLight = new(
        Background: 0xFFF3F3F3, Surface: 0xFFFFFFFF, SurfaceSecondary: 0xFFF7F7F7, SurfaceHover: 0xFFF0F0F0, SurfacePressed: 0xFFE8E8E8,
        Border: 0xFFE0E0E0, BorderStrong: 0xFF8A8A8A, Divider: 0xFFE5E5E5,
        TextPrimary: 0xFF1A1A1A, TextSecondary: 0xFF5C5C5C, TextTertiary: 0xFF6B6B6B, TextDisabled: 0xFFA0A0A0,
        Accent: 0xFF005FB8, AccentHover: 0xFF0A67C2, AccentPressed: 0xFF004E99, OnAccent: 0xFFFFFFFF, AccentText: 0xFF005FB8,
        AccentSubtle: 0xFFE5F0FA,
        Success: 0xFF0F7B0F, SuccessSubtle: 0xFFDFF6DD, Warning: 0xFF9D5D00, WarningSubtle: 0xFFFFF4CE,
        Critical: 0xFFC42B1C, CriticalSubtle: 0xFFFDE7E9,
        SearchHighlight: 0xFFFFE680, SearchHighlightActive: 0xFFFF9F1A, SearchHighlightBorder: 0xFF8A6100, SearchHighlightActiveBorder: 0xFF8F3F00,
        FocusRing: 0xFF1A1A1A, Overlay: 0x66000000, SubtleHover: 0x0A000000, SubtlePressed: 0x06000000);

    public static readonly Palette WindowsDark = new(
        Background: 0xFF202020, Surface: 0xFF2B2B2B, SurfaceSecondary: 0xFF323232, SurfaceHover: 0xFF383838, SurfacePressed: 0xFF2F2F2F,
        Border: 0xFF3D3D3D, BorderStrong: 0xFF9A9A9A, Divider: 0xFF3A3A3A,
        TextPrimary: 0xFFFFFFFF, TextSecondary: 0xFFC8C8C8, TextTertiary: 0xFF9E9E9E, TextDisabled: 0xFF6E6E6E,
        Accent: 0xFF60CDFF, AccentHover: 0xFF5BBDEB, AccentPressed: 0xFF4FA8D3, OnAccent: 0xFF000000, AccentText: 0xFF99EBFF,
        AccentSubtle: 0xFF1D3A4D,
        Success: 0xFF6CCB5F, SuccessSubtle: 0xFF2A3A27, Warning: 0xFFFCE100, WarningSubtle: 0xFF433519,
        Critical: 0xFFFF99A4, CriticalSubtle: 0xFF442726,
        SearchHighlight: 0xFF6B5A00, SearchHighlightActive: 0xFFA65500, SearchHighlightBorder: 0xFFE8CC4A, SearchHighlightActiveBorder: 0xFFFFC27A,
        FocusRing: 0xFFFFFFFF, Overlay: 0x99000000, SubtleHover: 0x0FFFFFFF, SubtlePressed: 0x0AFFFFFF);

    public static readonly TypeRamp WindowsType = new(
        Caption: 12, Body: 14, BodyStrong: 14, Subtitle: 20, Title: 28, LargeTitle: 40, Dense: 13,
        UiFont: "Segoe UI Variable Text, Segoe UI, Yu Gothic UI, Meiryo UI",
        DisplayFont: "Segoe UI Variable Display, Segoe UI, Yu Gothic UI, Meiryo UI",
        MonospaceFont: "Cascadia Mono, Consolas, Yu Gothic UI");

    public static readonly Radii WindowsRadii = new(Small: 2, Button: 4, Input: 4, Card: 8, Dialog: 8);

    // ───────── macOS (Human Interface Guidelines) ─────────

    public static readonly Palette MacLight = new(
        Background: 0xFFECECEC, Surface: 0xFFFFFFFF, SurfaceSecondary: 0xFFF5F5F5, SurfaceHover: 0xFFEDEDED, SurfacePressed: 0xFFE3E3E3,
        Border: 0xFFD9D9D9, BorderStrong: 0xFF808080, Divider: 0xFFE2E2E2,
        TextPrimary: 0xFF1D1D1F, TextSecondary: 0xFF4D4D52, TextTertiary: 0xFF636366, TextDisabled: 0xFFA1A1A6,
        Accent: 0xFF0066CC, AccentHover: 0xFF0A6FD6, AccentPressed: 0xFF0055AA, OnAccent: 0xFFFFFFFF, AccentText: 0xFF0066CC,
        AccentSubtle: 0xFFE3EEFB,
        Success: 0xFF19722E, SuccessSubtle: 0xFFE1F3E5, Warning: 0xFF935600, WarningSubtle: 0xFFFFF1D6,
        Critical: 0xFFC9251C, CriticalSubtle: 0xFFFCE6E4,
        SearchHighlight: 0xFFFFE680, SearchHighlightActive: 0xFFFF9F1A, SearchHighlightBorder: 0xFF8A6100, SearchHighlightActiveBorder: 0xFF8F3F00,
        FocusRing: 0xFF0066CC, Overlay: 0x59000000, SubtleHover: 0x0D000000, SubtlePressed: 0x08000000);

    public static readonly Palette MacDark = new(
        Background: 0xFF1E1E1E, Surface: 0xFF2A2A2A, SurfaceSecondary: 0xFF323232, SurfaceHover: 0xFF383838, SurfacePressed: 0xFF2E2E2E,
        Border: 0xFF3E3E3E, BorderStrong: 0xFF98989D, Divider: 0xFF3A3A3A,
        TextPrimary: 0xFFF5F5F7, TextSecondary: 0xFFC7C7CC, TextTertiary: 0xFF9C9CA1, TextDisabled: 0xFF6C6C70,
        Accent: 0xFF0B6FD8, AccentHover: 0xFF0A64C4, AccentPressed: 0xFF0A62BF, OnAccent: 0xFFFFFFFF, AccentText: 0xFF6CB4FF,
        AccentSubtle: 0xFF1C3350,
        Success: 0xFF5FD27A, SuccessSubtle: 0xFF213A28, Warning: 0xFFFFC74D, WarningSubtle: 0xFF40331A,
        Critical: 0xFFFF8A80, CriticalSubtle: 0xFF452523,
        SearchHighlight: 0xFF6B5A00, SearchHighlightActive: 0xFFA65500, SearchHighlightBorder: 0xFFE8CC4A, SearchHighlightActiveBorder: 0xFFFFC27A,
        FocusRing: 0xFF6CB4FF, Overlay: 0x80000000, SubtleHover: 0x12FFFFFF, SubtlePressed: 0x0BFFFFFF);

    public static readonly TypeRamp MacType = new(
        Caption: 11, Body: 13, BodyStrong: 13, Subtitle: 15, Title: 22, LargeTitle: 26, Dense: 13,
        UiFont: ".AppleSystemUIFont, Hiragino Sans, Hiragino Kaku Gothic ProN, Segoe UI, Yu Gothic UI",
        DisplayFont: ".AppleSystemUIFont, Hiragino Sans, Hiragino Kaku Gothic ProN, Segoe UI, Yu Gothic UI",
        MonospaceFont: "SF Mono, Menlo, Consolas");

    public static readonly Radii MacRadii = new(Small: 4, Button: 6, Input: 6, Card: 10, Dialog: 12);

    /// <summary>色の名前と値 (テストと、Windows 版・Mac 版の資源に入れるときに使う)。</summary>
    public static IEnumerable<(string Name, uint Argb)> Entries(Palette p) =>
        typeof(Palette).GetProperties()
            .Where(x => x.PropertyType == typeof(uint))
            .Select(x => (x.Name, (uint)x.GetValue(p)!));

    // ───────── コントラスト (WCAG 2.x) ─────────

    /// <summary>2 つの色のコントラスト比 (1〜21)。前景が半透明なら背景に重ねた色で測る。</summary>
    public static double Contrast(uint foreground, uint background)
    {
        uint fg = Over(foreground, background);
        double a = Luminance(fg), b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>半透明の色を不透明な背景に重ねた色。</summary>
    public static uint Over(uint color, uint background)
    {
        double alpha = (color >> 24) / 255.0;
        if (alpha >= 1) return color;
        byte Mix(int shift) => (byte)Math.Round(((color >> shift) & 0xFF) * alpha + ((background >> shift) & 0xFF) * (1 - alpha));
        return 0xFF000000u | ((uint)Mix(16) << 16) | ((uint)Mix(8) << 8) | Mix(0);
    }

    public static double Luminance(uint argb)
    {
        static double Channel(uint v)
        {
            double c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel((argb >> 16) & 0xFF) + 0.7152 * Channel((argb >> 8) & 0xFF) + 0.0722 * Channel(argb & 0xFF);
    }

    /// <summary>「#AARRGGBB」の文字 (XAML の色)。</summary>
    public static string Hex(uint argb) => $"#{argb:X8}";
}

/// <summary>意味で名前を付けた色 (ARGB)。</summary>
public sealed record Palette(
    uint Background, uint Surface, uint SurfaceSecondary, uint SurfaceHover, uint SurfacePressed,
    uint Border, uint BorderStrong, uint Divider,
    uint TextPrimary, uint TextSecondary, uint TextTertiary, uint TextDisabled,
    uint Accent, uint AccentHover, uint AccentPressed, uint OnAccent, uint AccentText, uint AccentSubtle,
    uint Success, uint SuccessSubtle, uint Warning, uint WarningSubtle, uint Critical, uint CriticalSubtle,
    uint SearchHighlight, uint SearchHighlightActive, uint SearchHighlightBorder, uint SearchHighlightActiveBorder,
    uint FocusRing, uint Overlay,
    /// <summary>背景に重ねる薄い色 (操作バーのボタンにマウスを乗せた・押したとき)。半透明。</summary>
    uint SubtleHover, uint SubtlePressed);

/// <summary>文字の大きさの段階 (pt)。Dense は情報の多い一覧 (議事録の画面など) の本文。</summary>
public sealed record TypeRamp(
    double Caption, double Body, double BodyStrong, double Subtitle, double Title, double LargeTitle, double Dense,
    string UiFont, string DisplayFont, string MonospaceFont);

/// <summary>角の丸み (px)。</summary>
public sealed record Radii(double Small, double Button, double Input, double Card, double Dialog);
