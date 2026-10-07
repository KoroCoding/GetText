using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace GetText;

/// <summary>
/// デザイントークン (DesignTokens) を WPF の資源に入れる。色は「Gt.名前」のブラシ (DynamicResource で使う。ライト / ダークで入れ替わる)、
/// 文字の大きさは「Gt.Font.名前」、角の丸みは「Gt.Radius.名前」(CornerRadius)。
/// Windows の設定に合わせるときは、Windows のライト / ダークを読んで、変わったら入れ替える。
/// </summary>
public static class Theme
{
    private static bool _subscribed;
    private static AppTheme _requested = AppTheme.System;

    /// <summary>今ダークで表示しているか。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>ライト / ダークが変わった (画面の上に直接描いている色を描き直す)。</summary>
    public static event Action? Changed;

    public static Palette Colors => DesignTokens.Colors(mac: false, IsDark);

    /// <summary>起動したとき、窓を作る前に呼ぶ (文字の大きさ・角の丸み・字体を入れる)。</summary>
    public static void Initialize(ResourceDictionary resources)
    {
        var type = DesignTokens.WindowsType;
        resources["UiFont"] = new FontFamily(type.UiFont);
        resources["Gt.Font.Ui"] = new FontFamily(type.UiFont);
        resources["Gt.Font.Display"] = new FontFamily(type.DisplayFont);
        resources["Gt.Font.Mono"] = new FontFamily(type.MonospaceFont);
        resources["Gt.Font.Caption"] = type.Caption;
        resources["Gt.Font.Body"] = type.Body;
        resources["Gt.Font.BodyStrong"] = type.BodyStrong;
        resources["Gt.Font.Subtitle"] = type.Subtitle;
        resources["Gt.Font.Title"] = type.Title;
        resources["Gt.Font.LargeTitle"] = type.LargeTitle;
        resources["Gt.Font.Dense"] = type.Dense;
        var r = DesignTokens.WindowsRadii;
        resources["Gt.Radius.Small"] = new CornerRadius(r.Small);
        resources["Gt.Radius.Button"] = new CornerRadius(r.Button);
        resources["Gt.Radius.Input"] = new CornerRadius(r.Input);
        resources["Gt.Radius.Card"] = new CornerRadius(r.Card);
        resources["Gt.Radius.Dialog"] = new CornerRadius(r.Dialog);
        ApplyColors(resources, dark: false);
    }

    /// <summary>ライト / ダーク / Windows の設定に合わせる。</summary>
    public static void Apply(AppTheme theme)
    {
        _requested = theme;
        if (!_subscribed)
        {
            _subscribed = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle && _requested == AppTheme.System)
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(_requested));
            };
        }
        bool dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemUsesDark(),
        };
        if (Application.Current is { } app) ApplyColors(app.Resources, dark);
    }

    private static void ApplyColors(ResourceDictionary resources, bool dark)
    {
        bool changed = IsDark != dark;
        IsDark = dark;
        foreach (var (name, argb) in DesignTokens.Entries(DesignTokens.Colors(mac: false, dark)))
        {
            var brush = new SolidColorBrush(ToColor(argb));
            brush.Freeze();
            resources["Gt." + name] = brush;
            resources["Gt." + name + "Color"] = ToColor(argb);
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Windows の「アプリ モード」がダークか。</summary>
    public static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    public static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static Brush Brush(Func<Palette, uint> pick)
    {
        var brush = new SolidColorBrush(ToColor(pick(Colors)));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Segoe Fluent Icons (Windows 10 では Segoe MDL2 Assets) の文字。</summary>
public static class WindowsIcons
{
    public static string Glyph(AppIcon icon) => icon switch
    {
        AppIcon.ScreenOcr => "",
        AppIcon.Meeting => "",
        AppIcon.Recording => "",
        AppIcon.Presentation => "",
        AppIcon.Search => "",
        AppIcon.Pin => "",
        AppIcon.Extensions => "",
        AppIcon.Settings => "",
        AppIcon.Refresh => "",
        AppIcon.Pause => "",
        AppIcon.Play => "",
        AppIcon.Stop => "",
        AppIcon.Copy => "",
        AppIcon.Save => "",
        AppIcon.History => "",
        AppIcon.Layout => "",
        AppIcon.Groups => "",
        AppIcon.Warning => "",
        AppIcon.Error => "",
        AppIcon.Info => "",
        AppIcon.Success => "",
        AppIcon.Close => "",
        AppIcon.More => "",
        AppIcon.ChevronRight => "",
        AppIcon.ChevronDown => "",
        AppIcon.ArrowUp => "",
        AppIcon.ArrowDown => "",
        AppIcon.Add => "",
        AppIcon.Download => "",
        AppIcon.Folder => "",
        AppIcon.Document => "",
        AppIcon.Translate => "",
        AppIcon.Accumulate => "",
        AppIcon.Clear => "",
        AppIcon.Command => "",
        AppIcon.Home => "",
        AppIcon.Exit => "",
        AppIcon.Local => "",
        AppIcon.Cloud => "",
        AppIcon.Privacy => "",
        AppIcon.Keyboard => "",
        AppIcon.Appearance => "",
        AppIcon.Model => "",
        AppIcon.Diagnostics => "",
        AppIcon.Microphone => "",
        AppIcon.Video => "",
        AppIcon.Volume => "",
        AppIcon.Monitor => "",
        _ => "",
    };
}

/// <summary>XAML でアイコンを名前で使う: Text="{local:Icon Search}"。</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class IconExtension(AppIcon icon) : MarkupExtension
{
    public IconExtension() : this(AppIcon.None) { }

    [ConstructorArgument("icon")]
    public AppIcon Icon { get; set; } = icon;

    public override object ProvideValue(IServiceProvider serviceProvider) => WindowsIcons.Glyph(Icon);
}
