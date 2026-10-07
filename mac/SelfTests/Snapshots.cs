using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

namespace GetText;

/// <summary>
/// 画面の画像を作る (GetText --snapshots フォルダ [dark])。画面には出さずに (Skia で描いて) 見本のデータの画面を PNG にする。
/// Mac の見た目の確認 (デザインの確認・説明書の画像) に使う。状態 (空・通常・読み込み中・エラー・検索・まとまり・多い件数) と最小の大きさも描く。
/// </summary>
internal static class Snapshots
{
    public static async Task<int> RunAsync(string dir, bool dark)
    {
        Directory.CreateDirectory(dir);
        App.ApplyTheme(dark ? AppTheme.Dark : AppTheme.Light);
        Dialogs.AutoAnswer = true;
        var settings = new AppSettings { MinutesLanguages = ["ja", "en"], Translate = true };
        int saved = 0;

        async Task Save(Window w, string name)
        {
            await UiSelfTest.Idle();
            var frame = w.CaptureRenderedFrame();
            if (frame == null) return;
            frame.Save(Path.Combine(dir, name + ".png"));
            Console.WriteLine($"{name}.png {frame.PixelSize.Width}x{frame.PixelSize.Height}");
            saved++;
        }

        var minutes = new MinutesWindow(new TestHost(), settings) { Width = 1040, Height = 720 };
        minutes.Show();
        foreach (var scene in new[] { "empty", "loading", "recording", "file", "many", "summary" })
        {
            minutes.LoadDemo(scene);
            await Save(minutes, "minutes_" + scene);
        }
        // 小さな画面 (ノート PC など) で、要約と発言の欄が重ならないか
        minutes.Width = 940;
        minutes.Height = 656;
        minutes.LoadDemo("summary");
        await Save(minutes, "minutes_summary_small");
        minutes.Close();

        var capture = new CaptureWindow(settings);
        var text = new TextWindow(capture, settings) { Width = 560, Height = 540 };
        var home = new HomeWindow(text, settings); // (コマンドと機能の登録)
        capture.Show();
        text.Show();
        text.ShowDemo(
            "Quarterly Report\nRevenue grew 12% compared with last year.\n\n売上は前年より 12% 伸びました。",
            "四半期報告\n売上高は前年比で 12% 増加しました。\n\n売上は前年より 12% 伸びました。",
            "AI OCR · 180 ms", "翻訳: Mac 内", "4 行");
        await Save(text, "text");
        await Save(capture, "capture");

        text.ShowDemoState("loading");
        await Save(text, "text_loading");
        text.ShowDemoState("fallback");
        await Save(text, "text_error");
        text.ShowDemoState("empty");
        await Save(text, "text_empty");
        text.ShowDemoState("");

        // 検索 (⌘F)
        OcrLineData L(string s, double x, double y) => new([s], x, y, x + s.Length * 9, y + 18);
        text.ShowDemoLines([L("GetText reads the text inside the frame.", 10, 10), L("Matches are highlighted on screen, too.", 10, 40),
            L("Translation runs on your Mac.", 10, 70)], null, 0, 0, "GetText は枠の中の文字を読み取ります。\n見つけた言葉は画面の上にも印が付きます。\n翻訳は Mac の中で行われます。");
        text.ShowDemoSearch("the");
        await Save(text, "text_search");
        await Save(capture, "capture_search");
        text.CloseDemoSearch();

        // 枠・まとまりごと
        var grid = new byte[400 * 160 * 4];
        Array.Fill(grid, (byte)255);
        void Box(int l, int t, int r, int b)
        {
            for (int x = l; x <= r; x++) foreach (int y in new[] { t, t + 1, b - 1, b }) { int i = (y * 400 + x) * 4; grid[i] = grid[i + 1] = grid[i + 2] = 60; }
            for (int y = t; y <= b; y++) foreach (int x in new[] { l, l + 1, r - 1, r }) { int i = (y * 400 + x) * 4; grid[i] = grid[i + 1] = grid[i + 2] = 60; }
        }
        Box(2, 2, 195, 157);
        Box(204, 2, 397, 157);
        settings.OcrView = OcrView.Groups;
        text.ShowDemoLines([L("A-1 山田", 12, 12), L("B-1 佐藤", 214, 12), L("A-2 鈴木", 12, 44), L("B-2 高橋", 214, 44), L("A-3 田中", 12, 76), L("B-3 伊藤", 214, 76)],
            grid, 400, 160, null);
        await Save(text, "text_groups");
        settings.OcrView = OcrView.Text;

        // 最小の大きさ (操作バーはアイコンだけになる)
        text.ShowDemo("GetText reads the text inside the frame.", "GetText は枠の中の文字を読み取ります。", "AI OCR · 48 ms", "翻訳: Mac 内", "1 行");
        text.Width = text.MinWidth;
        text.Height = text.MinHeight + 140;
        await Save(text, "text_min");

        // 設定 (ページごと・検索)
        var settingsWindow = new SettingsWindow(text, settings);
        settingsWindow.ShowDemoModels(ModelCatalog.Demo());
        settingsWindow.Show();
        foreach (var page in Enum.GetValues<SettingsPage>())
        {
            settingsWindow.SelectPage(page);
            await Save(settingsWindow, $"settings_{page}".ToLowerInvariant());
        }
        settingsWindow.ShowDemoSearch("翻訳");
        await Save(settingsWindow, "settings_search");
        settingsWindow.ShowDemoSearch("");

        // 設定 → 拡張機能 (空・入れたもの・見つける・更新・一覧を取れない・入れている途中)
        var pluginDemo = Path.Combine(Path.GetTempPath(), "gettext-plugin-demo-" + Environment.ProcessId);
        foreach (var (name, scenario, tab, error, progress) in new (string, string, string, string?, string?)[]
        {
            ("empty", "empty", "Installed", null, null),
            ("installed", "installed", "Installed", null, null),
            ("discover", "discover", "Discover", null, null),
            ("updates", "updates", "Updates", null, null),
            ("offline", "discover", "Discover", "オンラインの一覧を取れませんでした (時間切れ)。前に保存した一覧を出しています。", null),
            ("progress", "discover", "Discover", null, "「読み取りの履歴」を入れています"),
        })
        {
            settingsWindow.ShowDemoExtensions(PluginDemo.Create(pluginDemo, scenario), tab, error, progress);
            await Save(settingsWindow, $"settings_extensions_{name}");
        }
        settingsWindow.Width = settingsWindow.MinWidth;
        settingsWindow.Height = settingsWindow.MinHeight;
        settingsWindow.SelectPage(SettingsPage.ScreenOcr);
        await Save(settingsWindow, "settings_min");
        settingsWindow.Close();

        // ホーム
        home.Show();
        home.ShowDemo(false, null, null);
        await Save(home, "home");
        home.ShowDemo(true, "記録中", "録画中", ["10月6日 (月) 14:00 ・ 定例ミーティング", "10月3日 (金) 10:30 ・ 記録", "10月1日 (水) 16:00 ・ 顧客との打ち合わせ"]);
        await Save(home, "home_open");
        home.ShowDemo(false, null, null, minutesInstalled: false);
        await Save(home, "home_not_installed");

        // コマンドの一覧 (⌘K)
        foreach (var (name, query) in new[] { ("palette", ""), ("palette_search", "録画"), ("palette_empty", "zzzz") })
        {
            var palette = new CommandPalette(AppCommands.Registry, CommandContext.Home);
            palette.Show();
            palette.SetQuery(query);
            await Save(palette, name);
            palette.Close();
        }
        return saved > 0 ? 0 : 1;
    }
}
