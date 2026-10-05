using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GetText;

/// <summary>
/// 操作手順の動画用に、見本のデータで各画面を描いて PNG に保存する (GetText.exe --doc-snapshots フォルダ)。
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
            TextWidth = 520,
            TextHeight = 520,
            MinutesLanguages = ["ja", "en"],
        };

        // 読み取り枠 (中は透明) と、読み取った文字の画面
        var capture = new CaptureWindow(settings);
        await RenderAsync(capture, Path.Combine(outDir, "capture.png"));
        var text = new TextWindow(capture, settings);
        text.ShowDemo(
            "GetText reads the text inside the frame in real time.\nYou can copy it, and it is translated into Japanese automatically.\nTranslation runs on your PC by default.",
            "GetText は、枠の中の文字をリアルタイムで読み取ります。\nコピーでき、日本語にも自動で翻訳されます。\n翻訳は、最初の設定では PC の中で行われます。",
            "AI OCR ・ GPU", "ローカル (GPU ・ 48 ms)", "読み取り 0.09 秒 ・ 3 行");
        await RenderAsync(text, Path.Combine(outDir, "text.png"));

        // 設定 (タブごと)
        var settingsWindow = new SettingsWindow(text, settings);
        for (int i = 0; i < settingsWindow.Tabs.Items.Count; i++)
        {
            settingsWindow.Tabs.SelectedIndex = i;
            await RenderAsync(settingsWindow, Path.Combine(outDir, $"settings_{i}.png"), keepOpen: i < settingsWindow.Tabs.Items.Count - 1);
        }

        // 機能を選ぶ画面 (起動したとき / 機能を開いているとき)
        var home = new HomeWindow(text, settings);
        home.ShowDemo(false, null, null);
        await RenderAsync(home, Path.Combine(outDir, "home.png"), keepOpen: true);
        home.ShowDemo(true, "記録中", "録画中");
        await RenderAsync(home, Path.Combine(outDir, "home_open.png"));

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
                // 文字を大きくしたとき (設定 → 表示 → 議事録の文字の大きさ)
                settings.MinutesFontSize = 18;
                minutes.ApplyFontSize();
                await RenderAsync(minutes, Path.Combine(outDir, "minutes_large.png"));
                settings.MinutesFontSize = 14;
            }
        }
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
            dc.DrawText(new FormattedText("定例ミーティング", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 15,
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
                var initial = new FormattedText(names[i][..1], System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 38,
                    Brushes.White, 1.0);
                dc.DrawText(initial, new Point(x + tw / 2 - initial.Width / 2, y + th / 2 - 8 - initial.Height / 2));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), null, new Rect(x + 8, y + th - 34, 86, 26), 6, 6);
                dc.DrawText(new FormattedText(names[i], System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 14,
                    Brushes.White, 1.0), new Point(x + 16, y + th - 31));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dv);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height);
    }

    private static async Task RenderAsync(Window window, string path, bool keepOpen = false)
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

        // 余白 (Margin) も含めて描くため、ウィンドウの中身の一番外側 (テンプレートの根元) を描く
        var visual = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        int w = (int)Math.Ceiling(visual.ActualWidth * Scale), h = (int)Math.Ceiling(visual.ActualHeight * Scale);
        var bitmap = new RenderTargetBitmap(w, h, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        // 背景 (Fluent テーマではウィンドウの背景が透明になるので、窓の背景色を先に塗る)
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = window.AllowsTransparency ? Brushes.Transparent
                : (window.Background is SolidColorBrush { Color.A: > 0 } b ? b
                    : Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White);
            dc.DrawRectangle(bg, null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        }
        bitmap.Render(dv);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using (var file = File.Create(path))
            encoder.Save(file);
        Console.WriteLine($"{path} {w}x{h}");
        if (!keepOpen) window.Hide();
    }
}
