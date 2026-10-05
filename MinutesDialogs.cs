using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace GetText;

/// <summary>議事録の小さな画面 (用語の登録・覚えた声)。</summary>
internal static class MinutesDialogs
{
    private static Window Frame(Window owner, string title, double width, double height) => new()
    {
        Owner = owner,
        Title = title,
        Width = width,
        Height = height,
        MinWidth = 380,
        MinHeight = 300,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont"),
        FontSize = 13,
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.FindResource("Caption"),
        TextTrimming = TextTrimming.None,
    };

    /// <summary>用語の登録。OK なら新しい一覧、取り消しなら null。</summary>
    public static List<string>? EditTerms(Window owner, IReadOnlyList<string> terms)
    {
        var w = Frame(owner, "GetText — 用語の登録", 560, 520);
        var root = new DockPanel { Margin = new Thickness(16) };
        var help = Caption(
            "会議に出てくる人名・社名・製品名・専門用語を、1 行に 1 つ書きます。音声認識が別の書き方 (ひらがな・別の漢字) にしたとき、" +
            "読みが同じなら登録した表記に直し、文脈による補正と要約でもこの表記を使います。\n" +
            "・読みが分かりにくい語は「九条(くじょう)」のように読みを添えます\n" +
            "・決まった直し方は「誤り→正しい」で書けます (例: 車内→社内)\n" +
            "・# で始まる行はメモとして無視します");
        help.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(help, Dock.Top);
        root.Children.Add(help);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "保存", MinWidth = 90, IsDefault = false, Style = (Style)Application.Current.FindResource("AccentButtonStyle") };
        var cancel = new Button { Content = "取り消し", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = string.Join(Environment.NewLine, terms),
            FontSize = 14,
        };
        root.Children.Add(box);
        w.Content = root;
        List<string>? result = null;
        ok.Click += (_, _) =>
        {
            result = box.Text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
            w.DialogResult = true;
        };
        w.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
        return w.ShowDialog() == true ? result : null;
    }

    /// <summary>覚えた声の場所 (サーバーの voices.py と同じ)。</summary>
    public static readonly string VoicesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "voices.json");

    /// <summary>覚えた声の名前と、覚えた回数。</summary>
    public static List<(string Name, int Count)> LoadVoices()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(VoicesPath));
            return doc.RootElement.EnumerateObject()
                .Select(p => (p.Name, p.Value.TryGetProperty("n", out var n) ? n.GetInt32() : 1)).OrderBy(v => v.Name).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return []; // 読めない・形の違うファイルでも、議事録の画面は開けるように
        }
    }

    /// <summary>覚えた声の一覧。忘れる名前 (すべてなら "*") を forget に渡す。</summary>
    public static void ManageVoices(Window owner, Action<string?> forget)
    {
        var w = Frame(owner, "GetText — 覚えた声", 460, 460);
        var root = new DockPanel { Margin = new Thickness(16) };
        var help = Caption(
            "話者に名前を付けると、その人の声の特徴を覚え、次の会議から似た声の話者に自動で名前を付けます。" +
            "覚えるのは声の特徴 (数値) だけで、音声は保存しません。保存先: %LOCALAPPDATA%\\GetText\\voices.json");
        help.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(help, Dock.Top);
        root.Children.Add(help);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var remove = new Button { Content = "選んだ声を忘れる", MinWidth = 120 };
        var clear = new Button { Content = "すべて忘れる", MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
        var close = new Button { Content = "閉じる", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(remove);
        buttons.Children.Add(clear);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var list = new ListBox { SelectionMode = SelectionMode.Extended };
        root.Children.Add(list);
        void Refresh()
        {
            var voices = LoadVoices();
            list.ItemsSource = voices.Select(v => $"{v.Name}  ({v.Count} 回)").ToList();
            list.Tag = voices;
            remove.IsEnabled = clear.IsEnabled = voices.Count > 0;
        }
        remove.Click += (_, _) =>
        {
            if (list.Tag is not List<(string Name, int Count)> voices) return;
            foreach (var i in list.SelectedItems.Cast<string>().Select(s => list.Items.IndexOf(s)).ToList()) forget(voices[i].Name);
            Wait();
            Refresh();
        };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(w, "覚えた声をすべて忘れますか？", "GetText", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            forget(null);
            Wait();
            Refresh();
        };
        close.Click += (_, _) => w.Close();
        w.Content = root;
        Refresh();
        w.ShowDialog();

        // 忘れるのは裏の処理が保存し直すので、少し待ってから読み直す
        static void Wait() => Thread.Sleep(300);
    }

    /// <summary>覚えた声をファイルから直接消す (議事録の音声認識がまだ動いていないとき)。</summary>
    public static void ForgetInFile(string? name)
    {
        try
        {
            if (!File.Exists(VoicesPath)) return;
            if (name == null)
            {
                File.WriteAllText(VoicesPath, "{}");
                return;
            }
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(VoicesPath)) ?? [];
            if (data.Remove(name)) File.WriteAllText(VoicesPath, JsonSerializer.Serialize(data));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            App.Log("ForgetVoice", ex);
        }
    }
}
