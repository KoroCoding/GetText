using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GetText;

/// <summary>
/// 議事録の画面の操作の確認 (GetText.exe --selftest-ui 結果.txt)。画面の外に出した見本の議事録で、
/// 発言を選ぶ → 一覧の外を押す / Enter で選択が外れるか、話者の名前を Enter で確定したとき全選択のまま残らないかを確かめる。
/// 窓は前面に出さない (ほかのアプリの操作を邪魔しない)。
/// </summary>
internal static class UiSelfTest
{
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
        var settings = new AppSettings { OcrEngine = OcrEngineKind.Ai, Translate = true, Follow = false, MinutesLanguages = ["ja", "en"] };
        var text = new TextWindow(new CaptureWindow(settings), settings);
        var w = new MinutesWindow(text, settings)
        {
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        w.LoadDemo("recording");
        w.Show();
        await Idle(w);

        var list = (ListBox)w.FindName("EntryList");
        // 1. 発言の行 (名前の部分) を押すと選ばれる → 一覧の外 (話者の欄) を押すと外れる
        var firstItem = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        // (作った押下では行が選ばれないので、押したときと同じように選んでおく)
        list.SelectedItem = firstItem.DataContext;
        await Idle(w);
        Check(list.SelectedItems.Count == 1, $"発言を選んだ状態にする (選択 {list.SelectedItems.Count} 件)");
        var speakerPanel = (FrameworkElement)((FrameworkElement)w.FindName("SpeakerList")).Parent;
        Click(speakerPanel);
        await Idle(w);
        Check(list.SelectedItems.Count == 0, $"一覧の外を押すと選択が外れる (選択 {list.SelectedItems.Count} 件)");

        // 2. 選んだあと、一覧で Enter を押すと外れる
        list.SelectedIndex = 1;
        Press(list, Key.Enter);
        await Idle(w);
        Check(list.SelectedItems.Count == 0, $"一覧で Enter を押すと選択が外れる (選択 {list.SelectedItems.Count} 件)");

        // 3. 話者の名前の欄で Enter → 名前が確定し、全選択 (薄いグレー) のまま残らない
        var speakerList = (ItemsControl)w.FindName("SpeakerList");
        var box = Descendants<TextBox>(speakerList).First(b => b.Text == "田中");
        box.Text = "田中部長";
        box.SelectAll();
        Press(box, Key.Enter);
        await Idle(w);
        var doc = (MinutesDocument)typeof(MinutesWindow).GetField("_doc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(w)!;
        Check(doc.Speakers.Any(s => s.Name == "田中部長"), "名前の欄で Enter を押すと名前が確定する");
        Check(box.SelectionLength == 0, $"Enter のあと、名前の文字が選ばれたまま残らない (選択 {box.SelectionLength} 文字)");

        // 4. Esc で取り消し、選ばれたまま残らない
        box.Text = "まちがい";
        box.SelectAll();
        Press(box, Key.Escape);
        await Idle(w);
        Check(box.Text == "田中部長" && box.SelectionLength == 0, $"Esc で書きかけを取り消し、選択も残らない (「{box.Text}」、選択 {box.SelectionLength} 文字)");

        // 5. 「自分」と同じ名前は付けられず、元の名前に戻る
        box.Text = "自分";
        Press(box, Key.Enter);
        await Idle(w);
        Check(doc.Speakers.Count(s => s.Name == "自分") == 1, $"「自分」と同じ名前は付けられない (「自分」の話者 {doc.Speakers.Count(s => s.Name == "自分")} 人)");

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        void Call(string method, params object[] args) => typeof(MinutesWindow).GetMethod(method, flags)!.Invoke(w, args);

        // 6. 新規にすると検索が消える (前の検索が残って、開いた議事録が空に見えない)
        var search = (TextBox)w.FindName("SearchBox");
        search.Text = "金曜日";
        await Idle(w);
        Call("ResetDocument");
        await Idle(w);
        Check(search.Text.Length == 0, $"新規にすると検索が消える (検索「{search.Text}」)");

        // 7. 新規の後に、前の記録の結果が遅れて届いても入れない
        typeof(MinutesWindow).GetMethod("OnSegment", flags)!.Invoke(w, [new TranscriptSegment("win", 1, 2, 1, "遅れて届いた発言", 99)]);
        await Idle(w);
        Check(doc.Entries.Count == 0, $"新規の後に遅れて届いた発言は入れない (発言 {doc.Entries.Count} 件)");

        // 8. ファイルの文字起こしが始まる前のメモは追加しない (時刻がずれるため)
        doc.FromFile = true;
        ((TextBox)w.FindName("MemoBox")).Text = "早すぎるメモ";
        Call("AddMemo");
        Check(doc.Entries.Count == 0, $"ファイルの文字起こしの前のメモは追加しない (発言 {doc.Entries.Count} 件)");
        doc.FromFile = false;

        // 9. 要約の ✎ で書き直し、もう一度押すと書き直しが終わる (✓ が効く)
        w.LoadDemo("summary");
        await Idle(w);
        var edit = (Button)w.FindName("SummaryEditButton");
        var summaryBox = (TextBox)w.FindName("SummaryBox");
        Click(edit);
        edit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Idle(w);
        bool editing = summaryBox.Visibility == Visibility.Visible;
        Click(edit);
        edit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Idle(w);
        Check(editing && summaryBox.Visibility != Visibility.Visible, $"要約の ✎ で書き直し、✓ で終える (書き直し中 {editing}、終えた後 {summaryBox.Visibility})");

        // 11. 「詳細」を押すと設定の欄が開き、速さを選ぶと保存される・状態の印が記録中になる
        w.LoadDemo("recording");
        await Idle(w);
        var options = (System.Windows.Controls.Primitives.ToggleButton)w.FindName("OptionsButton");
        options.IsChecked = true;
        await Idle(w);
        var popup = (System.Windows.Controls.Primitives.Popup)w.FindName("OptionsPopup");
        var speedBox = (ComboBox)w.FindName("SpeedBox");
        speedBox.SelectedItem = MinutesWindow.SpeedOptions.First(o => o.Value == "light");
        await Idle(w);
        Check(popup.IsOpen && settings.MinutesSpeed == "light", $"「詳細」で設定の欄が開き、速さを選ぶと保存される (開いた {popup.IsOpen}、速さ {settings.MinutesSpeed})");
        var micBox = (ComboBox)w.FindName("MicBox");
        var micItems = micBox.Items.Cast<Option<string>>().ToList();
        Check(micItems.Count >= 1 && micItems[0].Value == "" && micItems[0].Label.StartsWith("既定の通信デバイス"),
            $"「詳細」でマイクを選べる (候補 {micItems.Count} 件、先頭「{micItems.FirstOrDefault()?.Label}」)");
        var videoCheck = (CheckBox)w.FindName("RecordVideoCheck");
        var subtitleCheck = (CheckBox)w.FindName("SubtitleVideoCheck");
        bool subtitleOffAtFirst = !subtitleCheck.IsEnabled;
        videoCheck.IsChecked = true;
        videoCheck.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(subtitleOffAtFirst && settings.MinutesRecordVideo && subtitleCheck.IsEnabled && subtitleCheck.IsChecked == true,
            $"「会議の画面も録画する」を入れると「字幕つきの動画も作る」を選べる (はじめ {(subtitleOffAtFirst ? "選べない" : "選べる")}、録画 {settings.MinutesRecordVideo}、字幕 {subtitleCheck.IsEnabled}/{subtitleCheck.IsChecked})");
        videoCheck.IsChecked = false;
        videoCheck.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        options.IsChecked = false;
        Check(((TextBlock)w.FindName("StateText")).Text == "記録中", $"状態の印が記録中になる (「{((TextBlock)w.FindName("StateText")).Text}」)");
        var view = (System.Windows.Controls.Primitives.ToggleButton)w.FindName("ViewButton");
        view.IsChecked = true;
        await Idle(w);
        Check(((System.Windows.Controls.Primitives.Popup)w.FindName("ViewPopup")).IsOpen && ((CheckBox)w.FindName("TimeCheck")).IsVisible,
            "「表示」で時刻・日本語訳・相づちの切り替えが開く");
        view.IsChecked = false;

        // 10. 話者の名前の文字の色が、ライト・ダークどちらの背景でも読める明るさになり、テーマを変えると選び直される
        w.LoadDemo("recording");
        await Idle(w);
        var nameBlock = Descendants<TextBlock>(list).First(t => t.Text == "田中");
        var light = ((SolidColorBrush)nameBlock.Foreground).Color;
        App.ApplyTheme(AppTheme.Dark);
        await Idle(w);
        var dark = ((SolidColorBrush)nameBlock.Foreground).Color;
        App.ApplyTheme(AppTheme.Light);
        await Idle(w);
        Check(SpeakerColors.ContrastOnTheme(light, dark: false) >= 4.5 && SpeakerColors.ContrastOnTheme(dark, dark: true) >= 4.5 && light != dark,
            $"話者の名前の色がテーマに合う (ライト {light} 比 {SpeakerColors.ContrastOnTheme(light, false):0.0} ・ ダーク {dark} 比 {SpeakerColors.ContrastOnTheme(dark, true):0.0})");

        // 13. 検索: Enter で一致した発言に移り、一致した文字を選択の色で示す
        w.LoadDemo("recording");
        await Idle(w);
        var searchBox = (TextBox)w.FindName("SearchBox");
        searchBox.Text = "読み取";
        await Idle(w);
        Press(searchBox, Key.Enter);
        await Idle(w);
        var count = ((TextBlock)w.FindName("SearchCount")).Text;
        var selected = Descendants<TextBox>(list).FirstOrDefault(b => b.SelectionLength > 0 && b.DataContext is MinutesEntry);
        Check(count.StartsWith("1/") && selected?.SelectedText == "読み取", $"検索の Enter で一致した所に移る (「{count}」、選択「{selected?.SelectedText}」)");
        searchBox.Text = "";
        await Idle(w);

        // 14. 狭い窓では話者の欄をたたみ、「話者」で開ける
        double width = w.Width;
        w.Width = 700;
        await Idle(w);
        var speakerCard = (FrameworkElement)w.FindName("SpeakerCard");
        var speakersToggle = (System.Windows.Controls.Primitives.ToggleButton)w.FindName("SpeakersToggle");
        bool folded = speakerCard.Visibility != Visibility.Visible && speakersToggle.IsVisible;
        speakersToggle.IsChecked = true;
        speakersToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Idle(w);
        Check(folded && speakerCard.Visibility == Visibility.Visible, $"狭い窓では話者の欄をたたみ、「話者」で開ける (たたむ {folded}、開く {speakerCard.Visibility})");
        speakersToggle.IsChecked = false;
        w.Width = width;
        await Idle(w);

        // 15. 話者の欄の「人数」: 調整を開くと、欄が押せる大きさで見えていて、押した所が人数の欄に当たる
        var speakerOptions = (Expander)w.FindName("SpeakerOptions");
        speakerOptions.IsExpanded = true;
        await Idle(w);
        var countBox = (ComboBox)w.FindName("SpeakerCountBox");
        var visible = LayoutInformation.GetLayoutClip(countBox)?.Bounds ?? new Rect(countBox.RenderSize);
        visible.Intersect(new Rect(countBox.RenderSize));
        var center = countBox.TranslatePoint(new Point(visible.Left + visible.Width / 2, visible.Top + visible.Height / 2), w);
        var hit = w.InputHitTest(center) as DependencyObject;
        bool onBox = false;
        for (var n = hit; n != null; n = VisualTreeHelper.GetParent(n)) onBox |= n == countBox;
        Check(!visible.IsEmpty && visible.Width >= 70 && onBox,
            $"話者の欄の「人数」が押せる (見えている幅 {(visible.IsEmpty ? 0 : visible.Width):0} px、押した所 {hit?.GetType().Name})");
        SavePng((FrameworkElement)w.FindName("SpeakerCard"), Path.ChangeExtension(reportPath, ".speakers.png"));
        speakerOptions.IsExpanded = false;

        // 16. 発言と話者の欄の境目: 変えた幅を使い、窓の半分までに抑え、ダブルクリックで元の幅に戻る
        w.Width = 1200;
        await Idle(w);
        var speakerColumn = (ColumnDefinition)w.FindName("SpeakerColumn");
        settings.MinutesSpeakerWidth = 340;
        Call("FitSpeakerPanel");
        await Idle(w);
        double wide = speakerColumn.ActualWidth;
        settings.MinutesSpeakerWidth = 5000;
        Call("FitSpeakerPanel");
        await Idle(w);
        double capped = speakerColumn.ActualWidth;
        var splitter = (GridSplitter)w.FindName("SpeakerSplitter");
        splitter.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
        await Idle(w);
        Check(Math.Abs(wide - 340) < 1 && capped <= w.ActualWidth * 0.5 + 1 && Math.Abs(speakerColumn.ActualWidth - AppSettings.DefaultSpeakerWidth) < 1 && splitter.IsVisible,
            $"話者の欄の幅を変えられる (340 → {wide:0}、大きすぎる幅 → {capped:0}、ダブルクリック → {speakerColumn.ActualWidth:0})");
        w.Width = width;
        await Idle(w);

        // 17. 音声も入れたプロジェクト (.gettext) に保存し、開き直すと発言・話者・音声がそろう
        w.LoadDemo("recording");
        await Idle(w);
        doc = (MinutesDocument)typeof(MinutesWindow).GetField("_doc", flags)!.GetValue(w)!;
        var tempDir = Path.Combine(Path.GetTempPath(), "gettext_uitest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var audioFile = Path.Combine(tempDir, "議事録_音声1.m4a");
            File.WriteAllBytes(audioFile, [0, 0, 0, 24, 102, 116, 121, 112]);
            doc.AudioParts.Add(new AudioPart(audioFile, doc.StartedAt ?? DateTime.Now, 60));
            int entries = doc.Entries.Count, speakers = doc.Speakers.Count;
            var projectPath = Path.Combine(tempDir, "会議.gettext");
            bool Saving() => (bool)typeof(MinutesWindow).GetField("_savingProject", flags)!.GetValue(w)!;
            Call("SaveProject", projectPath);
            for (int i = 0; i < 100 && Saving(); i++) await Idle(w);
            File.Delete(audioFile); // 別の PC で開くのと同じ (元の音声が無い)
            Call("ResetDocument");
            Call("OpenPath", projectPath);
            for (int i = 0; i < 100 && (Saving() || doc.Entries.Count == 0); i++) await Idle(w);
            var reopened = doc.AudioParts.FirstOrDefault();
            Check(File.Exists(projectPath) && doc.Entries.Count == entries && doc.Speakers.Count == speakers && reopened != null && File.Exists(reopened.Path),
                $"音声も入れたプロジェクトに保存して開き直せる (発言 {doc.Entries.Count}/{entries}、話者 {doc.Speakers.Count}/{speakers}、音声 {(reopened != null && File.Exists(reopened.Path) ? "あり" : "なし")})");
        }
        finally
        {
            Call("ResetDocument");
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        // 12. 設定: チェックの行の説明の文字を押してもチェックが切り替わる
        var sw = new SettingsWindow(text, settings)
        {
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        sw.Show();
        sw.SelectPage(SettingsPage.ScreenOcr);
        ((Expander)sw.FindName("WindowsOcrDetails")).IsExpanded = true; // (和文の空白は「詳細設定」の中)
        await Idle(sw);
        var join = (CheckBox)sw.FindName("JoinCheck");
        bool before = join.IsChecked == true;
        var label = Descendants<TextBlock>((DependencyObject)join.Parent).First(t => t.Text == "和文の文字間の空白を詰める");
        label.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
        label.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
        await Idle(sw);
        Check(join.IsChecked == !before && settings.JoinCjk == !before, $"設定の行の説明を押すとチェックが切り替わる ({before} → {join.IsChecked})");
        // 設定の検索: 別のページの項目も見つかり、押すとそのページが開く
        var hits = sw.FindSettings("テーマ");
        Check(hits.Count >= 1 && hits[0].Page == SettingsPage.Appearance, $"設定を検索できる (「テーマ」→ {string.Join(", ", hits.Select(h => h.Where + "/" + h.Label))})");
        var deepHits = sw.FindSettings("拡大");
        Check(deepHits.Any(h => h.Page == SettingsPage.ScreenOcr), $"詳細設定にたたんだ項目も検索できる (「拡大」→ {deepHits.Count} 件)");
        sw.SelectPage(SettingsPage.Privacy);
        await Idle(sw);
        Check(sw.CurrentPage == SettingsPage.Privacy && ((ItemsControl)sw.FindName("PrivacyList")).Items.Count >= 5,
            $"プライバシーのページに、送る・送らないの一覧が出る ({((ItemsControl)sw.FindName("PrivacyList")).Items.Count} 件)");
        sw.Close();

        // 確かめた画面を画像に残す (記録中: 確定前の文字の欄と最新の発言が重ならないか)
        typeof(MinutesWindow).GetMethod("LeaveTextBox", flags)!.Invoke(w, [null]); // 発言の書き直し中は自動で動かさないので、抜けておく
        ((TextBox)w.FindName("MemoBox")).Text = "";
        w.LoadDemo("recording");
        Call("ScrollToLatest"); // 発言が届いたときと同じく、最新までスクロールする
        await Idle(w);
        await Idle(w);
        var lastItem = (FrameworkElement?)list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1);
        var partialPanel = (FrameworkElement)w.FindName("PartialPanel");
        bool lastVisible = false;
        if (lastItem != null && lastItem.IsVisible)
        {
            var bottom = lastItem.TranslatePoint(new Point(0, lastItem.ActualHeight), list).Y;
            lastVisible = bottom <= list.ActualHeight + 1;
        }
        var sv = Descendants<ScrollViewer>(list).FirstOrDefault();
        Check(partialPanel.IsVisible && lastVisible, $"記録中の確定前の文字の欄と、最新の発言が重ならない (欄 {partialPanel.IsVisible}、最新の発言が見える {lastVisible}" +
            $" ・ 位置 {sv?.VerticalOffset:0}/{sv?.ExtentHeight:0} 見える高さ {sv?.ViewportHeight:0}、テーマを変えた後)");
        SavePng((FrameworkElement)w.Content, Path.ChangeExtension(reportPath, ".minutes.png"));

        // 18. 機能を選ぶ画面: 3 つの機能がそろい、最初はどれも「開く」
        text.PrepareForLauncher(); // 起動したときと同じく、文字の読み取りは閉じておく
        var home = new HomeWindow(text, settings)
        {
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        home.Show();
        await Idle(home);
        var tiles = Descendants<Button>(home).Where(b => b.DataContext is FeatureTile && b.Style == (Style)home.FindResource("TileButton")).ToList();
        Check(tiles.Count == 3 && tiles.All(b => b.IsVisible && b.ActualWidth >= 120 && b.ActualHeight >= 100),
            $"ホームに 3 つの機能のタイルが出る ({tiles.Count} 枚、幅 {string.Join("/", tiles.Select(b => b.ActualWidth.ToString("0")))})");
        Check(tiles.All(b => !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(b))),
            "タイルに読み上げの名前がある");
        // コマンドの一覧 (Ctrl+K): 検索すると合うものが上に出る
        var palette = new CommandPalette(AppCommands.Registry, CommandContext.Home)
        {
            ShowInTaskbar = false, ShowActivated = false, Left = -32000, Top = -32000,
        };
        palette.Show();
        palette.SetQuery("録画");
        await Idle(palette);
        Check(palette.Items.Count > 0 && palette.Items[0].Command.Id.StartsWith("feature.record"), $"コマンドの一覧で「録画」を探すと、画面の録画が先頭に出る ({palette.Items.FirstOrDefault()?.Title})");
        palette.SetQuery("preferences");
        await Idle(palette);
        Check(palette.Items.Any(i => i.Command.Id == "settings.open"), "コマンドの一覧は英語の名前でも見つかる (preferences → 設定を開く)");
        palette.SetQuery("search");
        await Idle(palette);
        Check(palette.Items.All(i => !i.Command.Id.StartsWith("ocr.", StringComparison.Ordinal)), "読み取りを閉じているときは、読み取りの操作を一覧に出さない");
        palette.CloseIfOpen();
        SavePng((FrameworkElement)home.Content, Path.ChangeExtension(reportPath, ".home.png"));
        // 読み取りを閉じたまま (読み取りの窓を一度も出さずに)、機能を選ぶ画面から設定を開ける
        string settingsError = "";
        try
        {
            text.OpenSettings(0, home);
        }
        catch (Exception ex)
        {
            settingsError = ex.GetType().Name + ": " + ex.Message;
        }
        await Idle(home);
        var openedSettings = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        Check(openedSettings is { IsVisible: true }, $"機能を選ぶ画面から設定を開ける {settingsError}");
        openedSettings?.Close();
        home.Hide();

        // 20. 読み取りの画面: 検索 (Ctrl+F) で件数と枠の上の印が出る / 枠・まとまりごとに分けられる
        var capture2 = new CaptureWindow(settings)
        {
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        capture2.Show();
        var text2 = new TextWindow(capture2, settings)
        {
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        text2.Show();
        await Idle(text2);
        OcrLineData L(string s, double x, double y) => new([s], x, y, x + s.Length * 9, y + 18);
        text2.ShowDemoLines([L("alpha beta", 10, 10), L("gamma alpha", 10, 40), L("delta", 10, 70)], null, 300, 120, null);
        text2.ShowDemoSearch("ALPHA");
        await Idle(text2);
        Check(((TextBlock)text2.FindName("SearchCount")).Text == "1/2" && capture2.HighlightCount >= 2,
            $"検索の件数と枠の上の印 (「{((TextBlock)text2.FindName("SearchCount")).Text}」、印 {capture2.HighlightCount})");
        ((Button)text2.FindName("SearchNextButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Idle(text2);
        // 上に行が増えても、今の一致 (2 件目の alpha) を選び続ける
        text2.ShowDemoLines([L("intro", 10, 0), L("alpha beta", 10, 20), L("gamma alpha", 10, 44), L("delta", 10, 70)], null, 300, 120, null);
        await Idle(text2);
        Check(((TextBlock)text2.FindName("SearchCount")).Text == "2/2", $"読み取りが更新されても、検索の位置は先頭に戻らない (「{((TextBlock)text2.FindName("SearchCount")).Text}」)");
        text2.CloseDemoSearch();
        Check(capture2.HighlightCount == 0, "検索を閉じると枠の上の印も消える");
        settings.OcrView = OcrView.Groups;
        var grid = new byte[300 * 120 * 4];
        Array.Fill(grid, (byte)255);
        void Box(int l, int t, int r, int b)
        {
            for (int x = l; x <= r; x++) foreach (int y in new[] { t, t + 1, b - 1, b }) { int i = (y * 300 + x) * 4; grid[i] = grid[i + 1] = grid[i + 2] = 60; }
            for (int y = t; y <= b; y++) foreach (int x in new[] { l, l + 1, r - 1, r }) { int i = (y * 300 + x) * 4; grid[i] = grid[i + 1] = grid[i + 2] = 60; }
        }
        Box(2, 2, 145, 117);
        Box(152, 2, 297, 117);
        text2.ShowDemoLines([L("A-1 山田", 12, 12), L("B-1 佐藤", 162, 12), L("A-2 鈴木", 12, 44), L("B-2 高橋", 162, 44)], grid, 300, 120, null);
        await Idle(text2);
        var grouped = ((TextBox)text2.FindName("ResultBox")).Text;
        Check(grouped.StartsWith("【1】\r\nA-1 山田\r\nA-2 鈴木") && grouped.Contains("【2】\r\nB-1 佐藤\r\nB-2 高橋"),
            $"枠・まとまりごとに分ける (「{grouped.Replace("\r\n", " / ")}」)");
        settings.OcrView = OcrView.Text;
        text2.ShowDemoLines([L("A-1 山田", 12, 12), L("B-1 佐藤", 162, 12)], grid, 300, 120, null);
        Check(!((TextBox)text2.FindName("ResultBox")).Text.Contains('【'), "上から順に戻すと見出しは付かない");
        text2.Hide();
        capture2.Hide();

        // 19. 画面の録画の画面: 録画するものを選べ、録画のボタンが押せる
        var recorderWindow = new RecorderWindow(settings)
        {
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        recorderWindow.LoadDemo();
        recorderWindow.Show();
        await Idle(recorderWindow);
        var targetBox = (ComboBox)recorderWindow.FindName("TargetBox");
        var recordButton = (Button)recorderWindow.FindName("RecordButton");
        Check(targetBox.Items.Count == 3 && recordButton.IsVisible && recordButton.ActualWidth >= 120,
            $"画面の録画の画面 (録画するもの {targetBox.Items.Count} 件、録画のボタン {recordButton.ActualWidth:0} px)");
        SavePng((FrameworkElement)recorderWindow.Content, Path.ChangeExtension(reportPath, ".recorder.png"));
        recorderWindow.Close();

        w.Hide();
        report.Insert(0, $"議事録の画面の操作の確認 ・ {(failed == 0 ? "すべて合格" : $"不合格 {failed} 件")}\n");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        return failed == 0 ? 0 : 2;
    }

    private static async Task Idle(Window w)
    {
        for (int i = 0; i < 3; i++) await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
    }

    // マウスの左ボタンを押した・離したことにする (窓全体の PreviewMouseLeftButtonDown も通る)
    private static void Click(UIElement target)
    {
        // 「左ボタン」の通知は、窓から押した先までの各要素に「マウスのボタン」の通知から作られるので、そちらを送る
        foreach (var ev in new[] { Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent, Mouse.PreviewMouseUpEvent, Mouse.MouseUpEvent })
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = ev });
    }

    // 確かめた画面の部分を画像に残す (結果のファイルの隣)
    private static void SavePng(FrameworkElement element, string path)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(element);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            // 窓の中の位置のまま描くと画像の外に出るので、要素だけを左上から描く
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            }
            bitmap.Render(visual);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            encoder.Save(file);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("画像を保存できませんでした: " + ex.Message);
        }
    }

    private static void Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)!;
        foreach (var ev in new[] { Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent })
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = ev });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }
}
