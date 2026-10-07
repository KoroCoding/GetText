using System.IO;

namespace GetText.Tests;

/// <summary>デザイントークン (色のコントラスト・余白の段階)・アイコン・コマンドの検索。</summary>
public class DesignSystemTests
{
    public static IEnumerable<object[]> Palettes() =>
    [
        ["Windows ライト", DesignTokens.WindowsLight],
        ["Windows ダーク", DesignTokens.WindowsDark],
        ["Mac ライト", DesignTokens.MacLight],
        ["Mac ダーク", DesignTokens.MacDark],
    ];

    [Theory]
    [MemberData(nameof(Palettes))]
    public void TextHasEnoughContrast(string name, Palette p)
    {
        // 普通の文字は 4.5:1 以上 (背景・カード・薄い背景・押せる行の上でも)
        foreach (var bg in new[] { p.Background, p.Surface, p.SurfaceSecondary, p.SurfaceHover })
        {
            foreach (var (label, fg) in new[] { ("TextPrimary", p.TextPrimary), ("TextSecondary", p.TextSecondary), ("AccentText", p.AccentText) })
                Assert.True(DesignTokens.Contrast(fg, bg) >= 4.5, $"{name}: {label} on {DesignTokens.Hex(bg)} = {DesignTokens.Contrast(fg, bg):0.00}");
        }
        foreach (var bg in new[] { p.Background, p.Surface, p.SurfaceSecondary })
            Assert.True(DesignTokens.Contrast(p.TextTertiary, bg) >= 4.5, $"{name}: TextTertiary on {DesignTokens.Hex(bg)} = {DesignTokens.Contrast(p.TextTertiary, bg):0.00}");
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void AccentAndStatusHaveEnoughContrast(string name, Palette p)
    {
        foreach (var fill in new[] { p.Accent, p.AccentHover, p.AccentPressed })
            Assert.True(DesignTokens.Contrast(p.OnAccent, fill) >= 4.5, $"{name}: OnAccent on {DesignTokens.Hex(fill)} = {DesignTokens.Contrast(p.OnAccent, fill):0.00}");
        // 状態の色は文字にも使う (「録画中」など)。薄い背景の帯の上でも読める
        foreach (var (label, fg, subtle) in new[] { ("Success", p.Success, p.SuccessSubtle), ("Warning", p.Warning, p.WarningSubtle), ("Critical", p.Critical, p.CriticalSubtle) })
        {
            foreach (var bg in new[] { p.Background, p.Surface, subtle })
                Assert.True(DesignTokens.Contrast(fg, bg) >= 4.5, $"{name}: {label} on {DesignTokens.Hex(bg)} = {DesignTokens.Contrast(fg, bg):0.00}");
            Assert.True(DesignTokens.Contrast(p.TextPrimary, subtle) >= 4.5, $"{name}: TextPrimary on {label}Subtle");
        }
        Assert.True(DesignTokens.Contrast(p.TextPrimary, p.AccentSubtle) >= 4.5, $"{name}: TextPrimary on AccentSubtle");
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void ControlsAndHighlightsAreVisible(string name, Palette p)
    {
        // 操作の部品・アイコン・キーボードの枠は 3:1 以上
        foreach (var bg in new[] { p.Background, p.Surface })
        {
            Assert.True(DesignTokens.Contrast(p.BorderStrong, bg) >= 3, $"{name}: BorderStrong on {DesignTokens.Hex(bg)} = {DesignTokens.Contrast(p.BorderStrong, bg):0.00}");
            Assert.True(DesignTokens.Contrast(p.FocusRing, bg) >= 3, $"{name}: FocusRing on {DesignTokens.Hex(bg)}");
            Assert.True(DesignTokens.Contrast(p.SearchHighlightBorder, bg) >= 3, $"{name}: SearchHighlightBorder on {DesignTokens.Hex(bg)}");
            Assert.True(DesignTokens.Contrast(p.SearchHighlightActiveBorder, bg) >= 3, $"{name}: SearchHighlightActiveBorder on {DesignTokens.Hex(bg)}");
        }
        // 検索で見つかった所の上の文字も読める。今の一致とほかの一致は色が違う (枠の太さでも区別する)
        Assert.True(DesignTokens.Contrast(p.TextPrimary, p.SearchHighlight) >= 4.5, $"{name}: text on SearchHighlight");
        Assert.True(DesignTokens.Contrast(p.TextPrimary, p.SearchHighlightActive) >= 4.5, $"{name}: text on SearchHighlightActive");
        Assert.NotEqual(p.SearchHighlight, p.SearchHighlightActive);
    }

    [Fact]
    public void PalettesDefineEveryColorOpaqueExceptOverlay()
    {
        foreach (var p in new[] { DesignTokens.WindowsLight, DesignTokens.WindowsDark, DesignTokens.MacLight, DesignTokens.MacDark })
        foreach (var (name, argb) in DesignTokens.Entries(p))
            Assert.True(name is "Overlay" or "SubtleHover" or "SubtlePressed" || argb >> 24 == 0xFF, $"{name} は不透明にする ({DesignTokens.Hex(argb)})");
        Assert.Equal(32, DesignTokens.Entries(DesignTokens.WindowsLight).Count());
    }

    [Fact]
    public void SpacingAndRadiiUseTheRamp()
    {
        Assert.Equal([2, 4, 8, 12, 16, 24, 32, 48], DesignTokens.SpacingRamp);
        foreach (var r in new[] { DesignTokens.WindowsRadii, DesignTokens.MacRadii })
            Assert.True(r.Small <= r.Button && r.Button <= r.Card && r.Card <= r.Dialog);
        foreach (var t in new[] { DesignTokens.WindowsType, DesignTokens.MacType })
        {
            Assert.True(t.Caption < t.Body && t.Body < t.Subtitle && t.Subtitle < t.Title && t.Title < t.LargeTitle);
            Assert.DoesNotContain("Yu Gothic", t.UiFont.Split(',')[0]); // 日本語の字体に固定しない (OS の UI の字体を先に)
        }
    }

    [Fact]
    public void AppXamlMatchesTokens()
    {
        // App.xaml の文字の大きさ・角の丸み (XAML を読むときに要る) が DesignTokens と同じ値か
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GetText.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "App.xaml"));
        var t = DesignTokens.WindowsType;
        var r = DesignTokens.WindowsRadii;
        foreach (var (key, value) in new (string, double)[]
                 {
                     ("Gt.Font.Caption", t.Caption), ("Gt.Font.Body", t.Body), ("Gt.Font.BodyStrong", t.BodyStrong), ("Gt.Font.Subtitle", t.Subtitle),
                     ("Gt.Font.Title", t.Title), ("Gt.Font.LargeTitle", t.LargeTitle), ("Gt.Font.Dense", t.Dense),
                     ("Gt.Radius.Small", r.Small), ("Gt.Radius.Button", r.Button), ("Gt.Radius.Input", r.Input), ("Gt.Radius.Card", r.Card), ("Gt.Radius.Dialog", r.Dialog),
                 })
            Assert.Contains($"x:Key=\"{key}\">{value}<", xaml);
        // Mac 版 (Avalonia) の App.axaml も同じく DesignTokens.MacType・MacRadii と同じ値
        var axaml = File.ReadAllText(Path.Combine(dir.FullName, "mac", "App.axaml"));
        var mt = DesignTokens.MacType;
        var mr = DesignTokens.MacRadii;
        foreach (var (key, value) in new (string, double)[]
                 {
                     ("Gt.Font.Caption", mt.Caption), ("Gt.Font.Body", mt.Body), ("Gt.Font.BodyStrong", mt.BodyStrong), ("Gt.Font.Subtitle", mt.Subtitle),
                     ("Gt.Font.Title", mt.Title), ("Gt.Font.LargeTitle", mt.LargeTitle),
                     ("Gt.Radius.Small", mr.Small), ("Gt.Radius.Button", mr.Button), ("Gt.Radius.Input", mr.Input), ("Gt.Radius.Card", mr.Card), ("Gt.Radius.Dialog", mr.Dialog),
                 })
            Assert.Contains($"x:Key=\"{key}\">{value}<", axaml);
        foreach (var file in new[] { "HomeWindow.axaml", "TextWindow.axaml", "SettingsWindow.axaml", "CommandPalette.axaml" })
        {
            var text = File.ReadAllText(Path.Combine(dir.FullName, "mac", file));
            Assert.DoesNotMatch(@"(Foreground|Background|BorderBrush|Fill|Stroke)=""#[0-9A-Fa-f]{6,8}""", text);
            Assert.DoesNotMatch(@"Content=""[⚙🎙●❚⟳⇣💾⧉✕▶]", text); // 記号・絵文字のアイコンは使わない (GtIcon を使う)
        }
        // 画面の XAML に色を直接書かない (トークンを使う)。議事録の話者の色などの例外は除く
        foreach (var file in new[] { "HomeWindow.xaml", "TextWindow.xaml", "SettingsWindow.xaml", "CommandPalette.xaml" })
        {
            var text = File.ReadAllText(Path.Combine(dir.FullName, file));
            Assert.DoesNotMatch(@"(Foreground|Background|BorderBrush|Fill|Stroke)=""#[0-9A-Fa-f]{6,8}""", text);
            Assert.DoesNotContain("Yu Gothic", text);
        }
    }

    [Fact]
    public void ContrastMatchesKnownValues()
    {
        Assert.Equal(21, DesignTokens.Contrast(0xFF000000, 0xFFFFFFFF), 1);
        Assert.Equal(1, DesignTokens.Contrast(0xFF777777, 0xFF777777), 3);
        Assert.Equal(4.48, DesignTokens.Contrast(0xFF777777, 0xFFFFFFFF), 2);
        Assert.Equal(0xFF7F7F7Fu, DesignTokens.Over(0x80000000, 0xFFFFFFFF));
    }

    [Fact]
    public void ExtensionIconsAreValidated()
    {
        Assert.NotNull(IconSource.Svg("M4 4h16v16H4z"));
        Assert.Null(IconSource.Svg("<script>alert(1)</script>"));
        Assert.Null(IconSource.Svg("url(http://example.com/a.png)"));
        Assert.Null(IconSource.Svg(""));
        Assert.True(IconValidator.HasPictograph("🎙 議事録"));
        Assert.True(IconValidator.HasPictograph("☀ テーマ"));
        Assert.False(IconValidator.HasPictograph("議事録 (Meeting)"));
        Assert.False(IconValidator.HasPictograph("【1】 A-1"));
    }

    private static AppCommand Cmd(string id, string title, string[]? keywords = null, CommandCategory category = CommandCategory.Action,
        CommandContext[]? contexts = null) =>
        new() { Id = id, Title = title, Keywords = keywords ?? [], Category = category, Contexts = contexts ?? [], Execute = () => { } };

    private static CommandRegistry Sample()
    {
        var r = new CommandRegistry();
        r.Register(Cmd("ocr.open", "文字の読み取りを開く", ["Screen OCR", "よみとり"], CommandCategory.Feature, [CommandContext.Home]));
        r.Register(Cmd("ocr.search", "読み取った文字を検索", ["Search", "find", "けんさく"], contexts: [CommandContext.ScreenOcr]));
        r.Register(Cmd("minutes.start", "会議の記録を開始", ["Start Meeting", "議事録"], contexts: [CommandContext.Meeting]));
        r.Register(Cmd("record.open", "画面の録画を開く", ["Record screen", "録画"], CommandCategory.Feature));
        r.Register(Cmd("settings.open", "設定を開く", ["Settings", "preferences"], CommandCategory.Settings));
        r.Register(Cmd("settings.theme", "テーマ (ライト・ダーク)", ["Appearance", "dark mode"], CommandCategory.Settings));
        return r;
    }

    [Fact]
    public void CommandSearchFindsByJapaneseEnglishAndKana()
    {
        var r = Sample();
        Assert.Equal("ocr.open", r.Search("screen ocr", CommandContext.Any)[0].Id);
        Assert.Equal("ocr.open", r.Search("ＳＣＲＥＥＮ", CommandContext.Any)[0].Id); // 全角でも
        Assert.Equal("ocr.open", r.Search("ヨミトリ", CommandContext.Any)[0].Id);     // カタカナでも
        Assert.Equal("minutes.start", r.Search("議事録", CommandContext.Any)[0].Id);
        Assert.Equal("settings.theme", r.Search("dark", CommandContext.Any)[0].Id);
        Assert.Equal("ocr.search", r.Search("find", CommandContext.Any)[0].Id);
        Assert.Empty(r.Search("zzzz", CommandContext.Any));
        // 語をすべて含むものだけ
        Assert.Equal(["record.open"], r.Search("録画 開く", CommandContext.Any).Select(c => c.Id));
    }

    [Fact]
    public void CommandSearchFuzzyAndOrdering()
    {
        var r = Sample();
        // 飛び飛びの文字でも見つかる (Settings)
        Assert.Contains(r.Search("sttngs", CommandContext.Any), c => c.Id == "settings.open");
        // 前方一致は途中の一致より上
        var hits = r.Search("set", CommandContext.Any).Select(c => c.Id).ToList();
        Assert.Equal("settings.open", hits[0]);
    }

    [Fact]
    public void CommandSearchPrefersContextAndRecent()
    {
        var r = Sample();
        var now = new DateTime(2026, 10, 7, 12, 0, 0);
        // 何も入れていないとき: 今の画面に関係するものが先頭
        Assert.Equal("minutes.start", r.Search("", CommandContext.Meeting, now: now)[0].Id);
        Assert.Equal("ocr.search", r.Search("", CommandContext.ScreenOcr, now: now)[0].Id);
        // 最近使ったものも上に
        r.MarkUsed("record.open", now.AddMinutes(-5));
        Assert.Equal("record.open", r.Search("", CommandContext.Any, now: now)[0].Id);
    }

    [Fact]
    public void CommandRegistryReplacesAndRejectsEmoji()
    {
        var r = Sample();
        int count = r.All.Count;
        r.Register(Cmd("ocr.open", "文字の読み取りを開く (新)"));
        Assert.Equal(count, r.All.Count);
        Assert.Throws<ArgumentException>(() => r.Register(Cmd("ext.x", "🚀 速くする")));
        bool ran = false;
        r.Register(new AppCommand { Id = "x.unavailable", Title = "使えない", IsAvailable = () => false, Execute = () => ran = true });
        Assert.False(r.TryRun("x.unavailable"));
        Assert.False(ran);
        Assert.DoesNotContain(r.Search("使えない", CommandContext.Any), c => c.Id == "x.unavailable");
        r.Unregister("x.unavailable");
        Assert.DoesNotContain(r.All, c => c.Id == "x.unavailable");
    }
}
