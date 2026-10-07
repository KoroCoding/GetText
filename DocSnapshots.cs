using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GetText;

/// <summary>
/// 見本のデータで各画面を描いて PNG に保存する (GetText.exe --doc-snapshots フォルダ [dark])。
/// 操作手順の動画の素材と、デザインの確認 (CI で Windows のライト / ダークを描いて残す) に使う。
/// 状態 (空・通常・読み込み中・エラー・長い文・検索・まとまり・多い件数)、拡大率 (100〜200%)、最小の大きさも描く。
/// ウィンドウは画面の外に出して描くので、利用者の画面には映らない。設定や議事録は保存しない (App.DemoMode)。
/// </summary>
internal static class DocSnapshots
{
    private const double Scale = 1.5; // 動画で文字がつぶれないよう、1.5 倍の解像度で描く (1920x1080 に縮小して使う)

    public static async Task RunAsync(string outDir, bool dark = false)
    {
        Directory.CreateDirectory(outDir);
        App.ApplyTheme(dark ? AppTheme.Dark : AppTheme.Light); // dark: ダークテーマの見え方の確認用
        var settings = new AppSettings
        {
            OcrEngine = OcrEngineKind.Ai, // 見本では AI は起動しない (App.DemoMode)
            Translate = true,
            Follow = false,
            Topmost = false,
            CaptureWidth = 560,
            CaptureHeight = 300,
            TextWidth = 560,
            TextHeight = 540,
            MinutesLanguages = ["ja", "en"],
        };

        // 読み取り枠 (中は透明) と、読み取った文字の画面
        var capture = new CaptureWindow(settings);
        await RenderAsync(capture, Path.Combine(outDir, "capture.png"));
        var text = new TextWindow(capture, settings);
        var home = new HomeWindow(text, settings); // (コマンドと機能の登録)
        text.ShowDemo(
            "GetText reads the text inside the frame in real time.\nYou can copy it, and it is translated into Japanese automatically.\nTranslation runs on your PC by default.",
            "GetText は、枠の中の文字をリアルタイムで読み取ります。\nコピーでき、日本語にも自動で翻訳されます。\n翻訳は、最初の設定では PC の中で行われます。",
            "AI OCR · 48 ms", "翻訳: PC 内", "3 行");
        await RenderAsync(text, Path.Combine(outDir, "text.png"), keepOpen: true);
        foreach (var s in new[] { 1.0, 1.25, 2.0 })
            await RenderAsync(text, Path.Combine(outDir, $"text_scale{s * 100:0}.png"), keepOpen: true, scale: s);

        // 状態: 読み込み中・AI OCR を使えない・空・長い文
        text.ShowDemoState("loading");
        await RenderAsync(text, Path.Combine(outDir, "text_loading.png"), keepOpen: true);
        text.ShowDemoState("fallback");
        await RenderAsync(text, Path.Combine(outDir, "text_error.png"), keepOpen: true);
        text.ShowDemoState("empty");
        await RenderAsync(text, Path.Combine(outDir, "text_empty.png"), keepOpen: true);
        text.ShowDemoState("");
        text.ShowDemo(string.Join("\n", Enumerable.Range(1, 40).Select(i => $"{i}. Long line of recognized text that keeps going to check wrapping and scrolling in the original pane.")),
            string.Join("\n", Enumerable.Range(1, 40).Select(i => $"{i}. 原文の欄の折り返しとスクロールを確かめるための、長く続く読み取った文の訳です。")),
            "Windows OCR · 210 ms", "翻訳: Google (オンライン)", "40 行");
        await RenderAsync(text, Path.Combine(outDir, "text_long.png"), keepOpen: true);

        // 検索 (Ctrl+F): 原文の欄と、読み取り枠の上の印
        var (sampleLines, sampleShown) = SampleDocument();
        text.ShowDemoLines(sampleLines, null, 0, 0,
            "GetText は、枠の中の文字をリアルタイムで読み取ります。\n見つけた言葉は、画面の上にも印が付きます。\n翻訳は PC の中で行われます (PC 内)。");
        text.ShowDemoSearch("PC");
        await RenderAsync(text, Path.Combine(outDir, "text_search.png"), keepOpen: true);
        await RenderCaptureOverAsync(capture, sampleShown, Path.Combine(outDir, "capture_search.png"));
        text.CloseDemoSearch();

        // 枠・まとまりごと (名簿のような枠で区切られた画面)
        var (roster, rosterLines, rosterPixels, rw, rh) = Roster();
        settings.OcrView = OcrView.Groups;
        settings.Translate = false; // (名前だけの名簿なので、原文だけを出す)
        text.OnSettingChanged(nameof(AppSettings.Translate));
        text.ShowDemoLines(rosterLines, rosterPixels, rw, rh, null);
        await RenderAsync(text, Path.Combine(outDir, "text_groups.png"), keepOpen: true);
        await RenderCaptureOverAsync(capture, roster, Path.Combine(outDir, "capture_roster.png"));
        settings.OcrView = OcrView.Text;
        text.ShowDemoLines(rosterLines, rosterPixels, rw, rh, null);
        await RenderAsync(text, Path.Combine(outDir, "text_groups_off.png"), keepOpen: true);
        settings.Translate = true;
        text.OnSettingChanged(nameof(AppSettings.Translate));

        // 最小の大きさ (操作バーはアイコンだけになる)
        text.ShowDemo("GetText reads the text inside the frame.", "GetText は枠の中の文字を読み取ります。", "AI OCR · 48 ms", "翻訳: PC 内", "1 行");
        text.Width = text.MinWidth;
        text.Height = text.MinHeight + 140;
        await RenderAsync(text, Path.Combine(outDir, "text_min.png"));

        // 設定 (ページごと・検索)
        var settingsWindow = new SettingsWindow(text, settings);
        foreach (var page in Enum.GetValues<SettingsPage>())
        {
            settingsWindow.SelectPage(page);
            await RenderAsync(settingsWindow, Path.Combine(outDir, $"settings_{page}.png".ToLowerInvariant()), keepOpen: true);
            // (操作手順の動画が使う昔の名前)
            if (page == SettingsPage.ScreenOcr) File.Copy(Path.Combine(outDir, "settings_screenocr.png"), Path.Combine(outDir, "settings_0.png"), true);
            if (page == SettingsPage.Models) File.Copy(Path.Combine(outDir, "settings_models.png"), Path.Combine(outDir, "settings_5.png"), true);
        }
        settingsWindow.ShowDemoSearch("翻訳");
        await RenderAsync(settingsWindow, Path.Combine(outDir, "settings_search.png"), keepOpen: true);
        settingsWindow.ShowDemoSearch("zzz");
        await RenderAsync(settingsWindow, Path.Combine(outDir, "settings_search_empty.png"), keepOpen: true);
        settingsWindow.ShowDemoSearch("");
        settingsWindow.Width = settingsWindow.MinWidth;
        settingsWindow.Height = settingsWindow.MinHeight;
        settingsWindow.SelectPage(SettingsPage.ScreenOcr);
        await RenderAsync(settingsWindow, Path.Combine(outDir, "settings_min.png"));

        // ホーム (起動したとき・機能を開いているとき・議事録を入れていないとき・最近の議事録が多いとき)
        home.ShowDemo(false, null, null);
        await RenderAsync(home, Path.Combine(outDir, "home.png"), keepOpen: true);
        foreach (var s in new[] { 1.0, 1.25, 2.0 })
            await RenderAsync(home, Path.Combine(outDir, $"home_scale{s * 100:0}.png"), keepOpen: true, scale: s);
        home.ShowDemo(true, "記録中", "録画中", ["10月6日 (月) 14:00 ・ 定例ミーティング", "10月3日 (金) 10:30 ・ 記録", "10月1日 (水) 16:00 ・ 顧客との打ち合わせ"]);
        await RenderAsync(home, Path.Combine(outDir, "home_open.png"), keepOpen: true);
        home.ShowDemo(false, null, null, minutesInstalled: false);
        await RenderAsync(home, Path.Combine(outDir, "home_not_installed.png"), keepOpen: true);
        home.ShowDemo(false, "開いています", null, Enumerable.Range(1, 4).Select(i => $"10月{i}日 14:00 ・ とても長い名前の会議の議事録で、表示しきれない部分は省略されることを確かめる {i}").ToList());
        await RenderAsync(home, Path.Combine(outDir, "home_many.png"));

        // コマンドの一覧 (Ctrl+K)
        foreach (var (name, query) in new[] { ("palette", ""), ("palette_search", "録画"), ("palette_empty", "zzzz") })
        {
            var palette = new CommandPalette(AppCommands.Registry, CommandContext.Home) { Width = 580 };
            palette.SetQuery(query);
            await RenderAsync(palette, Path.Combine(outDir, name + ".png"));
            palette.CloseIfOpen();
        }

        // 画面の録画 (録画する前 / 録画中)
        var preview = MeetingMock(1000, 500);
        var recorder = new RecorderWindow(settings);
        recorder.LoadDemo(false, preview);
        await RenderAsync(recorder, Path.Combine(outDir, "recorder.png"), keepOpen: true);
        recorder.LoadDemo(true, preview);
        await RenderAsync(recorder, Path.Combine(outDir, "recorder_recording.png"));

        // 議事録の「⚙ 詳細」の中 (会議の画面の録画・字幕つきの動画を選んだところ)
        settings.MinutesRecordVideo = true;
        var withOptions = new MinutesWindow(text, settings);
        withOptions.LoadDemo("empty");
        await RenderAsync(withOptions, Path.Combine(outDir, "minutes_empty_options.png"), keepOpen: true);
        await RenderPopupAsync(withOptions, "OptionsButton", "OptionsPopup", Path.Combine(outDir, "minutes_options.png"));
        withOptions.Hide();
        settings.MinutesRecordVideo = false;

        // 議事録
        foreach (var scene in new[] { "empty", "loading", "recording", "file", "many", "summary" })
        {
            var minutes = new MinutesWindow(text, settings);
            minutes.LoadDemo(scene);
            await RenderAsync(minutes, Path.Combine(outDir, $"minutes_{scene}.png"), keepOpen: scene == "recording");
            if (scene == "recording")
            {
                // 文字を大きくしたとき (設定 → 外観 → 議事録の文字の大きさ)
                settings.MinutesFontSize = 18;
                minutes.ApplyFontSize();
                await RenderAsync(minutes, Path.Combine(outDir, "minutes_large.png"));
                settings.MinutesFontSize = 14;
            }
        }
    }

    // 検索の見本: 読み取った行 (枠の中の位置つき) と、その画面の絵
    private static (List<OcrLineData> Lines, DrawingVisual Screen) SampleDocument()
    {
        string[] rows =
        [
            "GetText reads the text inside the frame in real time.",
            "Matches are highlighted on screen, too.",
            "Translation runs on your PC (on-device).",
        ];
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dv = new DrawingVisual();
        var lines = new List<OcrLineData>();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 560, 276));
            double y = 28;
            foreach (var row in rows)
            {
                var ft = new FormattedText(row, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 18, Brushes.Black, 1.0);
                dc.DrawText(ft, new Point(20, y));
                lines.Add(new OcrLineData([row], 20, y, 20 + ft.WidthIncludingTrailingWhitespace, y + ft.Height));
                y += 44;
            }
        }
        return (lines, dv);
    }

    // 名簿の見本 (2 行 × 3 列の枠に名前)。行は OCR と同じく上から順 (枠をまたいで並ぶ)
    private static (DrawingVisual Screen, List<OcrLineData> Lines, byte[] Pixels, int Width, int Height) Roster()
    {
        const int width = 560, height = 276;
        string[] last = ["山田", "佐藤", "鈴木", "高橋", "田中", "伊藤", "渡辺", "中村", "小林", "加藤", "吉田", "山本"];
        var face = new Typeface("Yu Gothic UI");
        var dv = new DrawingVisual();
        var lines = new List<(int Row, int Col, int I, OcrLineData Line)>();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(70, 90, 120)), 2);
            for (int row = 0; row < 2; row++)
            for (int col = 0; col < 3; col++)
            {
                double x = 12 + col * 182, y = 12 + row * 130;
                dc.DrawRectangle(null, pen, new Rect(x, y, 170, 118));
                char group = (char)('A' + row * 3 + col);
                for (int i = 0; i < 3; i++)
                {
                    string label = $"{group}-{i + 1} {last[(row * 3 + col + i * 5) % last.Length]}";
                    var ft = new FormattedText(label, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, face, 16, Brushes.Black, 1.0);
                    double ty = y + 14 + i * 32;
                    dc.DrawText(ft, new Point(x + 14, ty));
                    lines.Add((row, col, i, new OcrLineData([label], x + 14, ty, x + 14 + ft.Width, ty + ft.Height)));
                }
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dv);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var ordered = lines.OrderBy(l => l.Row).ThenBy(l => l.I).ThenBy(l => l.Col).Select(l => l.Line).ToList();
        return (dv, ordered, pixels, width, height);
    }

    /// <summary>
    /// 紹介動画用: ファイルから作った議事録 (.json) を、動画の時間に合わせて描く
    /// (GetText.exe --doc-snapshots-minutes 議事録.json フォルダ 秒,秒,... [final])。
    /// 各秒の時点で話し終わっていた発言を並べた画面を minutes_t{秒}.png に、final なら要約も入れた完成形を minutes_final.png に保存する。
    /// </summary>
    public static async Task RunMinutesAsync(string jsonPath, string outDir, string times, bool final)
    {
        Directory.CreateDirectory(outDir);
        App.ApplyTheme(AppTheme.Light);
        var settings = new AppSettings { OcrEngine = OcrEngineKind.Ai, Translate = true, Follow = false, MinutesLanguages = ["ja", "en"] };
        var file = MinutesFile.FromJson(File.ReadAllText(jsonPath));
        var capture = new CaptureWindow(settings);
        var text = new TextWindow(capture, settings);
        double total = file.DurationSeconds ?? file.Entries.Max(e => e.OffsetSeconds + e.DurationSeconds);
        var minutes = new MinutesWindow(text, settings);
        foreach (var t in times.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(double.Parse))
        {
            minutes.LoadDemoFile(file, t, total, finished: false);
            await RenderAsync(minutes, Path.Combine(outDir, $"minutes_t{t:0}.png"), keepOpen: true);
        }
        if (final)
        {
            minutes.LoadDemoFile(file, total, total, finished: true);
            await RenderAsync(minutes, Path.Combine(outDir, "minutes_final.png"), keepOpen: true);
        }
        minutes.Hide();
    }

    /// <summary>ボタンで開く欄 (Popup) の中身を描く (Popup は別の窓なので、ウィンドウの絵には入らない)。</summary>
    private static async Task RenderPopupAsync(Window window, string buttonName, string popupName, string path)
    {
        ((System.Windows.Controls.Primitives.ToggleButton)window.FindName(buttonName)).IsChecked = true;
        for (int i = 0; i < 5; i++)
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(300);
        var popup = (System.Windows.Controls.Primitives.Popup)window.FindName(popupName);
        var child = (FrameworkElement)popup.Child;
        child.UpdateLayout();
        int w = (int)Math.Ceiling(child.ActualWidth * Scale), h = (int)Math.Ceiling(child.ActualHeight * Scale);
        var bitmap = new RenderTargetBitmap(w, h, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(child);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using (var file = File.Create(path))
            encoder.Save(file);
        Console.WriteLine($"{path} {w}x{h}");
        popup.IsOpen = false;
    }

    /// <summary>録画のプレビューの見本: 会議アプリの画面に見立てた絵 (参加者の枠と名前)。BGRA・上から。</summary>
    private static (byte[] Pixels, int Width, int Height) MeetingMock(int width, int height)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 33, 36)), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(48, 49, 54)), null, new Rect(0, 0, width, 34));
            var face = new Typeface("Yu Gothic UI");
            dc.DrawText(new FormattedText("定例ミーティング", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 15,
                Brushes.White, 1.0), new Point(14, 8));
            string[] names = ["田中", "佐藤", "Sarah", "鈴木"];
            Color[] colors = [Color.FromRgb(70, 110, 160), Color.FromRgb(90, 140, 110), Color.FromRgb(150, 100, 140), Color.FromRgb(160, 120, 80)];
            double gap = 12, top = 46, tw = (width - gap * 3) / 2, th = (height - top - gap * 2) / 2;
            for (int i = 0; i < 4; i++)
            {
                double x = gap + (i % 2) * (tw + gap), y = top + (i / 2) * (th + gap);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(60, 64, 72)), i == 0 ? new Pen(new SolidColorBrush(Color.FromRgb(80, 200, 120)), 3) : null,
                    new Rect(x, y, tw, th), 10, 10);
                dc.DrawEllipse(new SolidColorBrush(colors[i]), null, new Point(x + tw / 2, y + th / 2 - 8), 46, 46);
                var initial = new FormattedText(names[i][..1], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 38,
                    Brushes.White, 1.0);
                dc.DrawText(initial, new Point(x + tw / 2 - initial.Width / 2, y + th / 2 - 8 - initial.Height / 2));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), null, new Rect(x + 8, y + th - 34, 86, 26), 6, 6);
                dc.DrawText(new FormattedText(names[i], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 14,
                    Brushes.White, 1.0), new Point(x + 16, y + th - 31));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dv);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height);
    }

    /// <summary>読み取り枠を、下の画面 (見本の絵) に重ねて描く (枠の中は透明なので、下の画面と検索の印が見える)。</summary>
    private static async Task RenderCaptureOverAsync(CaptureWindow capture, DrawingVisual screen, string path)
    {
        await PrepareAsync(capture);
        var visual = (FrameworkElement)VisualTreeHelper.GetChild(capture, 0);
        var area = (FrameworkElement)capture.FindName("CaptureArea");
        var origin = area.TranslatePoint(new Point(0, 0), visual);
        int w = (int)Math.Ceiling(visual.ActualWidth * Scale), h = (int)Math.Ceiling(visual.ActualHeight * Scale);
        var bitmap = new RenderTargetBitmap(w, h, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
            dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
            dc.DrawDrawing(screen.Drawing);
            dc.Pop();
        }
        bitmap.Render(dv);
        bitmap.Render(visual);
        await SaveAsync(bitmap, path);
        capture.Hide();
    }

    private static async Task PrepareAsync(Window window)
    {
        if (!window.IsVisible)
        {
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000; // 画面の外に出して描く
            window.Top = -32000;
            window.Show();
        }
        // 並べ替え・データの表示が終わるまで待つ
        for (int i = 0; i < 5; i++)
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(300);
        window.UpdateLayout();
    }

    private static async Task RenderAsync(Window window, string path, bool keepOpen = false, double scale = Scale)
    {
        await PrepareAsync(window);

        // 余白 (Margin) も含めて描くため、ウィンドウの中身の一番外側 (テンプレートの根元) を描く
        var visual = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        int w = (int)Math.Ceiling(visual.ActualWidth * scale), h = (int)Math.Ceiling(visual.ActualHeight * scale);
        var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        // 背景 (Fluent テーマではウィンドウの背景が透明になるので、窓の背景色を先に塗る)
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = window.AllowsTransparency ? Brushes.Transparent
                : (window.Background is SolidColorBrush { Color.A: > 0 } b ? b
                    : Application.Current.TryFindResource("Gt.Background") as Brush ?? Brushes.White);
            dc.DrawRectangle(bg, null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        }
        bitmap.Render(dv);
        bitmap.Render(visual);
        await SaveAsync(bitmap, path);
        if (!keepOpen) window.Hide();
    }

    private static async Task SaveAsync(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using (var file = File.Create(path))
            encoder.Save(file);
        Console.WriteLine($"{path} {bitmap.PixelWidth}x{bitmap.PixelHeight}");
    }
}
