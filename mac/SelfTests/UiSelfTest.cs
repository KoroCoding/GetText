using System.IO;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GetText;

/// <summary>読み取りの画面の代わり (動作確認用)。</summary>
internal sealed class TestHost : IMinutesHost
{
    public bool Hidden { get; private set; }
    public void SetOcrHidden(bool hidden) => Hidden = hidden;
    public Translator Translator { get; } = new();
}

/// <summary>
/// 議事録の画面の操作の確認 (GetText --selftest-ui 結果.txt)。画面には出さずに (Skia で描いて) 見本の議事録を動かし、
/// 実際のキー・マウスの入力で、選択の解除・話者の名前の確定・絞り込み・保存と読み込みなどを確かめる。
/// </summary>
internal static class UiSelfTest
{
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    public static async Task<int> RunAsync(string reportPath)
    {
        var report = new StringBuilder();
        int failed = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine((ok ? "OK  " : "NG  ") + what);
            if (!ok) failed++;
        }

        App.ApplyTheme(AppTheme.Light);
        Dialogs.AutoAnswer = true; // 確認は「OK」と答えたことにする
        var host = new TestHost();
        var settings = new AppSettings { MinutesLanguages = ["ja", "en"], HideOcrDuringMinutes = true };
        var w = new MinutesWindow(host, settings) { Width = 1000, Height = 700 };
        w.LoadDemo("recording");
        w.Show();
        await Idle();
        Check(host.Hidden, "「読み取り画面を隠す」がオンなら、開いたときに読み取りの画面を隠す");

        var list = w.FindControl<ListBox>("EntryList")!;
        var doc = w.Document;
        Check(doc.Entries.Count >= 6, $"見本の発言が並ぶ ({doc.Entries.Count} 件)");
        Check(!w.VisibleEntries.Any(e => e.Text == "はい。"), "相づち (はい。) は「相づちを除く」で隠れる");

        // 1. 発言を選ぶ → 一覧の外 (話者の欄) を押すと外れる
        list.SelectedItem = w.VisibleEntries[0];
        await Idle();
        Check(list.SelectedItems?.Count == 1, $"発言を選んだ状態にする (選択 {list.SelectedItems?.Count} 件)");
        var speakerTitle = w.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "話者");
        w.MouseDown(Center(speakerTitle, w), MouseButton.Left);
        w.MouseUp(Center(speakerTitle, w), MouseButton.Left);
        await Idle();
        Check(list.SelectedItems?.Count == 0, $"一覧の外を押すと選択が外れる (選択 {list.SelectedItems?.Count} 件)");

        // 2. 選んで一覧で Enter → 外れる
        list.SelectedIndex = 1;
        list.Focus();
        await Idle();
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Idle();
        Check(list.SelectedItems?.Count == 0, $"一覧で Enter を押すと選択が外れる (選択 {list.SelectedItems?.Count} 件、フォーカス {w.FocusManager?.GetFocusedElement()?.GetType().Name}、Focusable {list.Focusable})");

        // 3. 話者の名前の欄で Enter → 名前が確定し、選んだ文字が残らない
        var speakerList = w.FindControl<ItemsControl>("SpeakerList")!;
        var box = speakerList.GetVisualDescendants().OfType<TextBox>().First(b => b.Text == "田中");
        box.Focus();
        await Idle();
        box.Text = "田中部長";
        box.SelectAll();
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Idle();
        Check(doc.Speakers.Any(s => s.Name == "田中部長"), "名前の欄で Enter を押すと名前が確定する");
        Check(box.SelectionStart == box.SelectionEnd && !box.IsFocused, $"Enter のあと、名前の文字が選ばれたまま残らない (選択 {Math.Abs(box.SelectionEnd - box.SelectionStart)} 文字)");
        Check(doc.Entries.Where(e => e.Speaker.Id == 1).All(e => e.Speaker.Name == "田中部長"), "名前はすべての発言に反映される");

        // 4. Esc で取り消し
        box.Focus();
        await Idle();
        box.Text = "まちがい";
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Idle();
        Check(box.Text == "田中部長" && doc.Speakers.All(s => s.Name != "まちがい"), $"Esc で書きかけを取り消す (「{box.Text}」)");

        // 5. 「自分」と同じ名前は付けられない
        box.Focus();
        await Idle();
        box.Text = "自分";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Idle();
        Check(doc.Speakers.Count(s => s.Name == "自分") == 1, $"「自分」と同じ名前は付けられない (「自分」の話者 {doc.Speakers.Count(s => s.Name == "自分")} 人)");

        // 6. 同じ名前にすると 1 人にまとまる
        var sarah = speakerList.GetVisualDescendants().OfType<TextBox>().First(b => b.Text == "Sarah");
        int before = doc.Speakers.Count;
        sarah.Focus();
        await Idle();
        sarah.Text = doc.Speakers.First(s => s.Id == 1).Name;
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Idle();
        Check(doc.Speakers.Count == before - 1, $"ほかの話者と同じ名前にすると 1 人にまとまる (話者 {before} → {doc.Speakers.Count} 人)");

        // 7. 検索・★ だけ
        var search = w.FindControl<TextBox>("SearchBox")!;
        search.Text = "金曜日";
        await Idle();
        Check(w.VisibleEntries.Count >= 1 && w.VisibleEntries.All(e => e.Text.Contains("金曜日") || (e.Translation ?? "").Contains("金曜日")),
            $"検索で絞り込む ({w.VisibleEntries.Count} 件)");
        search.Text = "";
        await Idle();
        var marked = w.FindControl<Avalonia.Controls.Primitives.ToggleButton>("MarkedOnlyToggle")!;
        marked.IsChecked = true;
        marked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle();
        Check(w.VisibleEntries.Count == doc.Entries.Count(e => e.IsMarked) && w.VisibleEntries.Count > 0, $"★ だけを表示する ({w.VisibleEntries.Count} 件)");
        marked.IsChecked = false;
        marked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle();

        // 8. ⌘B (Mac) / Ctrl+B で ★ を付ける
        var target = w.VisibleEntries.First(e => !e.IsMarked && !e.IsNote);
        list.SelectedItem = target;
        list.Focus();
        await Idle();
        // アプリと同じく、その環境の「コマンドキー」を使う (Mac の実際の画面では ⌘、画面に出さない動作確認では Ctrl のことがある)
        var command = w.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var cmd = command == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control;
        w.KeyPress(Key.B, cmd, PhysicalKey.B, "b");
        await Idle();
        Check(target.IsMarked, "⌘B で選んだ発言に ★ を付ける");

        // 9. Delete で削除、⌘Z で戻す
        int count = doc.Entries.Count;
        list.SelectedItem = target;
        list.Focus();
        await Idle();
        w.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        await Idle();
        bool deleted = doc.Entries.Count == count - 1;
        list.Focus();
        w.KeyPress(Key.Z, cmd, PhysicalKey.Z, "z");
        await Idle();
        Check(deleted && doc.Entries.Count == count, $"Delete で削除し、⌘Z で元に戻す ({count} → {(deleted ? count - 1 : count)} → {doc.Entries.Count})");

        // 話者の欄の「人数」: 調整を開くと、押せる幅で見えている (ボタンと並べていたときは細くなって押せなかった)
        w.FindControl<Expander>("SpeakerOptions")!.IsExpanded = true;
        await Idle();
        var countBox = w.FindControl<ComboBox>("SpeakerCountBox")!;
        Check(countBox.IsEffectivelyVisible && countBox.Bounds.Width >= 70, $"話者の欄の「人数」が押せる幅 ({countBox.Bounds.Width:0} px)");
        w.FindControl<Expander>("SpeakerOptions")!.IsExpanded = false;

        // 発言と話者の欄の境目: 変えた幅を使い、窓の半分までに抑える
        var speakerColumn = w.FindControl<Grid>("BodyGrid")!.ColumnDefinitions[2];
        settings.MinutesSpeakerWidth = 340;
        typeof(MinutesWindow).GetMethod("FitSpeakerPanel", Private)!.Invoke(w, null);
        await Idle();
        double wide = speakerColumn.ActualWidth;
        settings.MinutesSpeakerWidth = 5000;
        typeof(MinutesWindow).GetMethod("FitSpeakerPanel", Private)!.Invoke(w, null);
        await Idle();
        Check(Math.Abs(wide - 340) < 1 && speakerColumn.ActualWidth <= w.Bounds.Width * 0.5 + 1,
            $"話者の欄の幅を変えられる (340 → {wide:0}、大きすぎる幅 → {speakerColumn.ActualWidth:0})");
        settings.MinutesSpeakerWidth = AppSettings.DefaultSpeakerWidth;
        typeof(MinutesWindow).GetMethod("FitSpeakerPanel", Private)!.Invoke(w, null);

        // 10. 右クリックのメニュー
        var menu = (ContextMenu)typeof(MinutesWindow).GetMethod("BuildEntryMenu", Private)!.Invoke(w, [new List<MinutesEntry> { target }, null])!;
        var headers = menu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
        Check(headers.Contains("この発言を削除") && headers.Contains("話者を変更") && headers.Contains("発言をコピー"), $"右クリックのメニュー ({string.Join(" / ", headers)})");

        // 11. 保存と開き直し (Markdown・議事録データ・字幕)
        var dir = Path.Combine(Path.GetTempPath(), "gettext_ui_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var ext in new[] { "md", "json", "srt", "txt" }) await w.SaveToAsync(Path.Combine(dir, "議事録." + ext));
            Check(new[] { "md", "json", "srt", "txt" }.All(e => new FileInfo(Path.Combine(dir, "議事録." + e)).Length > 50), "Markdown・議事録データ・字幕・テキストで保存する");
            int speakers = doc.Speakers.Count, entries = doc.Entries.Count;
            await w.OpenPathAsync(Path.Combine(dir, "議事録.json"));
            await Idle();
            Check(doc.Entries.Count == entries && doc.Speakers.Count == speakers && doc.Entries.Any(e => e.IsNote) && doc.Entries.Any(e => e.IsMarked),
                $"保存した議事録データを開き直す (発言 {doc.Entries.Count}/{entries} 件、話者 {doc.Speakers.Count}/{speakers} 人)");

            // 音声も入れたプロジェクト (.gettext): 元の音声を消しても、開き直すと中の音声を使う
            var audioFile = Path.Combine(dir, "議事録_音声1.m4a");
            File.WriteAllBytes(audioFile, [0, 0, 0, 24, 102, 116, 121, 112]);
            doc.AudioParts.Add(new AudioPart(audioFile, doc.StartedAt ?? DateTime.Now, 60));
            var project = Path.Combine(dir, "会議.gettext");
            await w.SaveToAsync(project);
            File.Delete(audioFile);
            w.ResetDocument();
            await w.OpenPathAsync(project);
            await Idle();
            var reopened = doc.AudioParts.FirstOrDefault();
            Check(doc.Entries.Count == entries && reopened != null && File.Exists(reopened.Path) && !reopened.Path.StartsWith(dir),
                $"音声も入れたプロジェクトに保存して開き直す (発言 {doc.Entries.Count}/{entries} 件、音声 {(reopened != null && File.Exists(reopened.Path) ? "あり" : "なし")})");
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        // 12. 新規にすると検索が消え、遅れて届いた発言は入れない
        search.Text = "金曜日";
        await Idle();
        w.ResetDocument();
        await Idle();
        Check((search.Text ?? "").Length == 0, "新規にすると検索が消える");
        typeof(MinutesWindow).GetMethod("OnSegment", Private)!.Invoke(w, [new TranscriptSegment("win", 1, 2, 1, "遅れて届いた発言", 99)]);
        await Idle();
        Check(doc.Entries.Count == 0, $"新規の後に遅れて届いた発言は入れない (発言 {doc.Entries.Count} 件)");

        // 13. ファイルの文字起こしの前のメモは追加しない
        doc.FromFile = true;
        w.FindControl<TextBox>("MemoBox")!.Text = "早すぎるメモ";
        w.AddMemo();
        Check(doc.Entries.Count == 0, "ファイルの文字起こしの前のメモは追加しない");
        doc.FromFile = false;
        w.AddMemo();
        Check(doc.Entries.Count == 1 && doc.Entries[0].IsNote, "メモを追加する");

        // 14. 要約の ✎ で書き直し、もう一度押すと終える
        w.LoadDemo("summary");
        await Idle();
        var edit = w.FindControl<Button>("SummaryEditButton")!;
        var summaryBox = w.FindControl<TextBox>("SummaryBox")!;
        edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle();
        bool editing = summaryBox.IsVisible;
        edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle();
        Check(editing && !summaryBox.IsVisible, $"要約の ✎ で書き直し、✓ で終える (書き直し中 {editing}、終えた後 {summaryBox.IsVisible})");
        var summaryView = w.FindControl<StackPanel>("SummaryView")!;
        Check(summaryView.Children.Count >= 8, $"要約を見出しと箇条書きに整えて表示する ({summaryView.Children.Count} 行)");

        // 15. 話者の名前の色がテーマに合う
        var nameBlock = list.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "田中");
        var light = ((ISolidColorBrush)nameBlock.Foreground!).Color;
        App.ApplyTheme(AppTheme.Dark);
        await Idle();
        var dark = ((ISolidColorBrush)nameBlock.Foreground!).Color;
        App.ApplyTheme(AppTheme.Light);
        await Idle();
        Check(SpeakerColors.ContrastOnTheme(light, false) >= 4.5 && SpeakerColors.ContrastOnTheme(dark, true) >= 4.5 && light != dark,
            $"話者の名前の色がテーマに合う (ライト {light} 比 {SpeakerColors.ContrastOnTheme(light, false):0.0} ・ ダーク {dark} 比 {SpeakerColors.ContrastOnTheme(dark, true):0.0})");

        // 16. 文字の大きさ (⌘+)
        double size = settings.MinutesFontSize;
        list.Focus();
        w.KeyPress(Key.OemPlus, cmd, PhysicalKey.Equal, "=");
        await Idle();
        Check(settings.MinutesFontSize == size + 1 && (double)w.Resources["EntryFontSize"]! == size + 1, $"⌘+ で文字を大きくする ({size} → {settings.MinutesFontSize})");

        // 17. 「詳細」で設定の欄が開き、速さを選ぶと保存される・状態の印・「表示」で表示の切り替えが開く
        w.LoadDemo("recording");
        await Idle();
        var options = w.FindControl<Button>("OptionsButton")!;
        w.MouseDown(Center(options, w), MouseButton.Left); // 実際に押す (押したときにだけ Flyout が開く)
        w.MouseUp(Center(options, w), MouseButton.Left);
        await Idle();
        bool optionsOpen = options.Flyout?.IsOpen == true;
        var speedBox = w.FindControl<ComboBox>("SpeedBox")!;
        speedBox.SelectedItem = MinutesWindow.SpeedOptions.First(o => o.Value == "light");
        await Idle();
        Check(optionsOpen && settings.MinutesSpeed == "light", $"「詳細」で設定の欄が開き、速さを選ぶと保存される (開いた {optionsOpen}、速さ {settings.MinutesSpeed})");
        options.Flyout?.Hide();
        var stateText = w.FindControl<TextBlock>("StateText")!.Text;
        Check(stateText == "記録中", $"状態の印が記録中になる (「{stateText}」)");
        var view = w.FindControl<Button>("ViewButton")!;
        w.MouseDown(Center(view, w), MouseButton.Left);
        w.MouseUp(Center(view, w), MouseButton.Left);
        await Idle();
        Check(view.Flyout?.IsOpen == true && w.FindControl<CheckBox>("TimeCheck")!.IsVisible, "「表示」で時刻・日本語訳・相づちの切り替えが開く");
        view.Flyout?.Hide();

        // 18. 機能を選ぶ画面と画面の録画の画面
        var capture2 = new CaptureWindow(settings);
        var text2 = new TextWindow(capture2, settings) { Width = 560, Height = 480 };
        var home = new HomeWindow(text2, settings);
        home.Show();
        await Idle();
        var tiles = home.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tile")).ToList();
        Check(tiles.Count == 3 && tiles.All(b => b.IsEffectivelyVisible && b.Bounds.Width >= 120),
            $"ホームに 3 つの機能のタイルが出る ({tiles.Count} 枚、幅 {string.Join("/", tiles.Select(b => b.Bounds.Width.ToString("0")))})");
        home.Hide(); // (閉じると GetText の終了になるので隠すだけ)

        // コマンドの一覧 (⌘K): 日本語でも英語でも見つかる
        var palette = new CommandPalette(AppCommands.Registry, CommandContext.Home);
        palette.Show();
        palette.SetQuery("録画");
        await Idle();
        Check(palette.Items.Count > 0 && palette.Items[0].Command.Id.StartsWith("feature.record"), $"コマンドの一覧で「録画」を探すと、画面の録画が先頭に出る ({palette.Items.FirstOrDefault()?.Title})");
        palette.SetQuery("search");
        await Idle();
        Check(palette.Items.Any(i => i.Command.Id == "ocr.search"), "コマンドの一覧は英語の名前でも見つかる (search → 読み取った文字を検索)");
        palette.Close();

        // 読み取りの画面: 検索の件数と枠の上の印、読み取りが更新されても今の一致を選び続ける、枠・まとまりごと
        capture2.Show();
        text2.Show();
        await Idle();
        OcrLineData L(string s, double x, double y) => new([s], x, y, x + s.Length * 9, y + 18);
        text2.ShowDemoLines([L("alpha beta", 10, 10), L("gamma alpha", 10, 40), L("delta", 10, 70)], null, 300, 120, null);
        text2.ShowDemoSearch("ALPHA");
        await Idle();
        Check(text2.SearchCountText == "1/2" && capture2.HighlightCount >= 2, $"検索の件数と枠の上の印 (「{text2.SearchCountText}」、印 {capture2.HighlightCount})");
        text2.SearchNextForTest();
        text2.ShowDemoLines([L("intro", 10, 0), L("alpha beta", 10, 20), L("gamma alpha", 10, 44), L("delta", 10, 70)], null, 300, 120, null);
        await Idle();
        Check(text2.SearchCountText == "2/2", $"読み取りが更新されても、検索の位置は先頭に戻らない (「{text2.SearchCountText}」)");
        text2.CloseDemoSearch();
        Check(capture2.HighlightCount == 0, "検索を閉じると枠の上の印も消える");
        var grid = new byte[300 * 120 * 4];
        Array.Fill(grid, (byte)255);
        void Box(int l, int t, int r, int b)
        {
            for (int x = l; x <= r; x++) foreach (int y in new[] { t, t + 1, b - 1, b }) { int i = (y * 300 + x) * 4; grid[i] = grid[i + 1] = grid[i + 2] = 60; }
            for (int y = t; y <= b; y++) foreach (int x in new[] { l, l + 1, r - 1, r }) { int i = (y * 300 + x) * 4; grid[i] = grid[i + 1] = grid[i + 2] = 60; }
        }
        Box(2, 2, 145, 117);
        Box(152, 2, 297, 117);
        settings.OcrView = OcrView.Groups;
        text2.ShowDemoLines([L("A-1 山田", 12, 12), L("B-1 佐藤", 162, 12), L("A-2 鈴木", 12, 44), L("B-2 高橋", 162, 44)], grid, 300, 120, null);
        var grouped = text2.ResultText.Replace("\r\n", "\n");
        Check(grouped.StartsWith("【1】\nA-1 山田\nA-2 鈴木") && grouped.Contains("【2】\nB-1 佐藤\nB-2 高橋"), $"枠・まとまりごとに分ける (「{grouped.Replace("\n", " / ")}」)");
        settings.OcrView = OcrView.Text;
        text2.Hide();
        capture2.Hide();

        // 設定: 検索で別のページの項目も見つかる
        var sw = new SettingsWindow(text2, settings);
        sw.Show();
        await Idle();
        var hits = sw.FindSettings("テーマ");
        Check(hits.Count >= 1 && hits[0].Page == SettingsPage.Appearance, $"設定を検索できる (「テーマ」→ {string.Join(", ", hits.Select(h => h.Where + "/" + h.Label))})");
        Check(sw.FindSettings("間隔").Any(h => h.Page == SettingsPage.ScreenOcr), "詳細設定にたたんだ項目も検索できる (間隔)");
        sw.Close();
        var recorderWindow = new RecorderWindow(settings);
        recorderWindow.LoadDemo();
        recorderWindow.Show();
        await Idle();
        var targetBox = recorderWindow.FindControl<ComboBox>("TargetBox")!;
        var recordButton = recorderWindow.FindControl<Button>("RecordButton")!;
        Check(targetBox.ItemCount == 3 && recordButton.IsEffectivelyVisible && recordButton.Bounds.Width >= 120,
            $"画面の録画の画面 (録画するもの {targetBox.ItemCount} 件、録画のボタン {recordButton.Bounds.Width:0} px)");
        recorderWindow.Close();

        w.Close();
        report.Insert(0, $"議事録の画面の操作の確認 (Mac 版) ・ {(failed == 0 ? "すべて合格" : $"不合格 {failed} 件")}\n");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        Console.WriteLine(report);
        return failed == 0 ? 0 : 2;
    }

    internal static Point Center(Visual v, Visual relativeTo) =>
        v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), relativeTo) ?? default;

    internal static async Task Idle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(30);
        }
    }
}
