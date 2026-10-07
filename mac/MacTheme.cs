using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;

namespace GetText;

/// <summary>
/// デザイントークン (DesignTokens の Mac の値) を Avalonia の資源に入れる。色は「Gt.名前」(ライト / ダークの辞書に入れるので、
/// DynamicResource で使えば切り替わる)。前からある名前 (Text1・CardBg など) も同じ値にそろえる。
/// </summary>
public static class MacTheme
{
    public static void Apply(Application app)
    {
        if (app.Resources is not ResourceDictionary root) return;
        foreach (var (variant, dark) in new[] { (ThemeVariant.Light, false), (ThemeVariant.Dark, true) })
        {
            if (!root.ThemeDictionaries.TryGetValue(variant, out var provider) || provider is not ResourceDictionary dict)
            {
                dict = new ResourceDictionary();
                root.ThemeDictionaries[variant] = dict;
            }
            var p = DesignTokens.Colors(mac: true, dark);
            foreach (var (name, argb) in DesignTokens.Entries(p))
                dict["Gt." + name] = new SolidColorBrush(Color.FromUInt32(argb));
            // 前からある名前 (各画面の XAML が使っている)
            dict["Text1"] = Brush(p.TextPrimary);
            dict["Text2"] = Brush(p.TextSecondary);
            dict["Text3"] = Brush(p.TextTertiary);
            dict["AppBg"] = Brush(p.Background);
            dict["CardBg"] = Brush(p.Surface);
            dict["CardStroke"] = Brush(p.Border);
            dict["Subtle"] = Brush(p.SubtleHover);
            dict["Subtle2"] = Brush(p.SubtlePressed);
            dict["Divider"] = Brush(p.Divider);
            dict["Accent"] = Brush(p.Accent);
            dict["AccentText"] = Brush(p.AccentText);
            dict["OnAccent"] = Brush(p.OnAccent);
            dict["Success"] = Brush(p.Success);
            dict["Critical"] = Brush(p.Critical);
            dict["PopupBg"] = Brush(p.Surface);
        }
        var type = DesignTokens.MacType;
        root["UiFont"] = new FontFamily(type.UiFont);
        root["Gt.Font.Mono"] = new FontFamily(type.MonospaceFont);
    }

    private static SolidColorBrush Brush(uint argb) => new(Color.FromUInt32(argb));

    /// <summary>今ダークで表示しているか。</summary>
    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public static Palette Colors => DesignTokens.Colors(mac: true, IsDark);

    public static IBrush BrushOf(Func<Palette, uint> pick) => new SolidColorBrush(Color.FromUInt32(pick(Colors)));
}

/// <summary>Mac の線のアイコン (24×24 の線。太さ 1.6、端は丸い。SF Symbols に近い見た目)。</summary>
public static class MacIcons
{
    public static string PathData(AppIcon icon) => icon switch
    {
        AppIcon.ScreenOcr => "M3 8V3h5 M16 3h5v5 M21 16v5h-5 M8 21H3v-5 M8 10h8 M8 14h5",
        AppIcon.Meeting or AppIcon.Microphone => "M9 6a3 3 0 0 1 6 0v6a3 3 0 0 1-6 0z M5 11a7 7 0 0 0 14 0 M12 18v3 M9 21h6",
        AppIcon.Recording or AppIcon.Video => "M3 7h12v10H3z M15 10l6-3v10l-6-3",
        AppIcon.Presentation => "M3 4h18 M4 4v11h16V4 M12 15v5 M8 20h8",
        AppIcon.Search => "M4 11a7 7 0 1 0 14 0a7 7 0 1 0-14 0 M16 16l5 5",
        AppIcon.Pin => "M9 4h6 M10 4v6l-3 3h10l-3-3V4 M12 13v7",
        AppIcon.Extensions => "M4 8h4a2 2 0 1 1 4 0h4v4a2 2 0 1 1 0 4v4H4z",
        AppIcon.Settings => "M4 6h9 M17 6h3 M4 12h3 M11 12h9 M4 18h11 M19 18h1 M15 4v4 M9 10v4 M17 16v4",
        AppIcon.Refresh => "M20 12a8 8 0 1 1-2.3-5.6 M20 4v5h-5",
        AppIcon.Pause => "M8 5v14 M16 5v14",
        AppIcon.Play => "M7 4l12 8-12 8z",
        AppIcon.Stop => "M6 6h12v12H6z",
        AppIcon.Copy => "M8 8h12v12H8z M4 16V4h12",
        AppIcon.Save => "M5 4h11l3 3v13H5z M8 4v5h7V4 M8 20v-6h8v6",
        AppIcon.Download => "M12 4v11 M7 10l5 5 5-5 M4 20h16",
        AppIcon.History => "M4 12a8 8 0 1 0 2.3-5.6 M4 4v5h5 M12 8v4l3 2",
        AppIcon.Layout => "M4 4h16v16H4z M4 10h16 M10 10v10",
        AppIcon.Groups => "M4 4h7v7H4z M13 4h7v7h-7z M4 13h7v7H4z M13 13h7v7h-7z",
        AppIcon.Warning => "M12 3l10 18H2z M12 10v5 M12 18v0.5",
        AppIcon.Error => "M3 12a9 9 0 1 0 18 0a9 9 0 1 0-18 0 M9 9l6 6 M15 9l-6 6",
        AppIcon.Info => "M3 12a9 9 0 1 0 18 0a9 9 0 1 0-18 0 M12 11v6 M12 7.5v0.5",
        AppIcon.Success => "M3 12a9 9 0 1 0 18 0a9 9 0 1 0-18 0 M8 12l3 3 5-6",
        AppIcon.Close => "M6 6l12 12 M18 6L6 18",
        AppIcon.More => "M5 12h0.5 M12 12h0.5 M19 12h0.5",
        AppIcon.ChevronRight => "M9 5l7 7-7 7",
        AppIcon.ChevronDown => "M5 9l7 7 7-7",
        AppIcon.ArrowUp => "M12 19V5 M6 11l6-6 6 6",
        AppIcon.ArrowDown => "M12 5v14 M6 13l6 6 6-6",
        AppIcon.Add => "M12 5v14 M5 12h14",
        AppIcon.Folder => "M3 6h6l2 2h10v11H3z",
        AppIcon.Document => "M6 3h8l4 4v14H6z M14 3v4h4 M9 12h6 M9 16h6",
        AppIcon.Translate => "M4 5h9 M8.5 3v2 M6 5c1 4 4 7 7 8 M11 5c-1 4-4 7-7 8 M13 20l4-9 4 9 M14.5 17h5",
        AppIcon.Accumulate => "M4 6h16 M4 11h16 M4 16h9 M17 14v6 M14 17l3 3 3-3",
        AppIcon.Clear => "M4 7h16 M10 11v6 M14 11v6 M6 7l1 13h10l1-13 M9 7V4h6v3",
        AppIcon.Command => "M9 9h6v6H9z M9 9V7a2 2 0 1 0-2 2h2 M15 9V7a2 2 0 1 1 2 2h-2 M9 15v2a2 2 0 1 1-2-2h2 M15 15v2a2 2 0 1 0 2-2h-2",
        AppIcon.Home => "M3 11l9-7 9 7 M5 10v10h14V10",
        AppIcon.Exit => "M12 3v9 M6.3 6.3a8 8 0 1 0 11.4 0",
        AppIcon.Local => "M3 5h18v11H3z M8 20h8 M12 16v4",
        AppIcon.Cloud => "M7 18h10a4 4 0 0 0 0.5-8A6 6 0 0 0 6 9a4.5 4.5 0 0 0 1 9z",
        AppIcon.Privacy => "M6 11h12v9H6z M8 11V8a4 4 0 1 1 8 0v3",
        AppIcon.Keyboard => "M3 6h18v12H3z M7 10h0.5 M11 10h0.5 M15 10h0.5 M7 14h10",
        AppIcon.Appearance => "M3 12a9 9 0 1 0 18 0a9 9 0 1 0-18 0 M12 3v18",
        AppIcon.Model => "M7 7h10v10H7z M10 3v4 M14 3v4 M10 17v4 M14 17v4 M3 10h4 M3 14h4 M17 10h4 M17 14h4",
        AppIcon.Volume => "M4 9h4l5-4v14l-5-4H4z M16 9a4 4 0 0 1 0 6 M18.5 6.5a8 8 0 0 1 0 11",
        AppIcon.Monitor => "M3 5h18v11H3z M8 20h8 M12 16v4",
        AppIcon.Diagnostics => "M3 12h4l3-7 4 14 3-7h4",
        _ => "",
    };

    private static readonly Dictionary<AppIcon, Geometry> Cache = [];

    public static Geometry? Geometry(AppIcon icon)
    {
        if (Cache.TryGetValue(icon, out var g)) return g;
        var data = PathData(icon);
        if (data.Length == 0) return null;
        return Cache[icon] = StreamGeometry.Parse(data);
    }
}

/// <summary>
/// 意味の名前のアイコンを線で描く部品 (&lt;local:GtIcon Icon="Search"/&gt;)。色は文字の色 (Foreground) を受け継ぐ。
/// 拡張機能のアイコン (確かめた SVG の線) は Svg に入れる。
/// </summary>
public sealed class GtIcon : Control
{
    public static readonly StyledProperty<AppIcon> IconProperty = AvaloniaProperty.Register<GtIcon, AppIcon>(nameof(Icon));
    public static readonly StyledProperty<string?> SvgProperty = AvaloniaProperty.Register<GtIcon, string?>(nameof(Svg));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<GtIcon>();
    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<GtIcon, double>(nameof(StrokeWidth), 1.6);

    static GtIcon()
    {
        AffectsRender<GtIcon>(IconProperty, SvgProperty, ForegroundProperty, StrokeWidthProperty);
    }

    public GtIcon()
    {
        Width = 16;
        Height = 16;
        IsHitTestVisible = false;
    }

    public AppIcon Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Svg { get => GetValue(SvgProperty); set => SetValue(SvgProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    public override void Render(DrawingContext context)
    {
        Geometry? geometry = Svg is { } svg && IconValidator.IsValidSvgPath(svg) ? StreamGeometry.Parse(svg) : MacIcons.Geometry(Icon);
        if (geometry == null || Foreground == null) return;
        double scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        if (scale <= 0) return;
        var pen = new Pen(Foreground, StrokeWidth / scale * Math.Max(1, scale * 0.75), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - 24 * scale) / 2, (Bounds.Height - 24 * scale) / 2)))
            context.DrawGeometry(null, pen, geometry);
    }
}
