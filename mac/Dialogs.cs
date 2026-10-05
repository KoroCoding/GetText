using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace GetText;

/// <summary>確認・お知らせ・ファイルの選択・用語の登録・覚えた声の小さな画面。</summary>
internal static class Dialogs
{
    private static Window Frame(string title, double width, double height) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        MinWidth = 360,
        MinHeight = 200,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
        CanResize = true,
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Classes = { "caption" },
        TextTrimming = TextTrimming.None,
    };

    /// <summary>
    /// 画面に出さずに試すとき (動作確認) の答え。null なら実際に聞く。
    /// </summary>
    internal static bool? AutoAnswer { get; set; }

    /// <summary>OK / 取り消し を聞く。OK なら true。</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string message, string title = "GetText — 議事録", string ok = "OK")
    {
        if (AutoAnswer is { } answer) return answer;
        var w = Frame(title, 440, 190);
        w.SizeToContent = SizeToContent.Height;
        w.CanResize = false;
        bool result = false;
        var okButton = new Button { Content = ok, MinWidth = 90, IsDefault = true, Classes = { "accent" }, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "取り消し", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        okButton.Click += (_, _) => { result = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 16),
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (IBrush?)Application.Current!.FindResource("Text1") },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0),
                    Children = { okButton, cancel },
                },
            },
        };
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>いくつかの中から 1 つを選んでもらう (選べないものは IsEnabled = false)。取り消しなら null。</summary>
    public static async Task<int?> ChooseAsync(Window owner, string title, string message,
        IReadOnlyList<(string Label, string Detail, bool Enabled)> options, int selected, string ok, string note = "")
    {
        if (AutoAnswer is { } answer) return answer ? selected : null;
        var w = Frame(title, 520, 300);
        w.SizeToContent = SizeToContent.Height;
        w.CanResize = false;
        int? result = null;
        int current = selected;
        Button? okButtonRef = null;
        var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Foreground = (IBrush?)Application.Current!.FindResource("Text1") });
        for (int i = 0; i < options.Count; i++)
        {
            int index = i;
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = options[i].Label, FontWeight = FontWeight.SemiBold });
            content.Children.Add(new TextBlock { Text = options[i].Detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
            var radio = new RadioButton { Content = content, GroupName = "choice", IsChecked = i == selected, IsEnabled = options[i].Enabled, Margin = new Thickness(0, 0, 0, 8) };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked != true) return;
                current = index;
                okButtonRef?.SetValue(Button.IsEnabledProperty, true);
            };
            panel.Children.Add(radio);
        }
        if (note.Length > 0)
            panel.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 0) });
        // 選べるものが無ければ (空き容量が足りないなど) 決定できない
        var okButton = new Button { Content = ok, MinWidth = 120, IsDefault = true, Classes = { "accent" }, HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = selected >= 0 && selected < options.Count && options[selected].Enabled };
        var cancel = new Button { Content = "取り消し", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        okButtonRef = okButton;
        okButton.Click += (_, _) => { result = current; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0),
            Children = { okButton, cancel },
        });
        w.Content = panel;
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>お知らせ (OK だけ)。</summary>
    public static async Task AlertAsync(Window owner, string message, string title = "GetText — 議事録")
    {
        if (AutoAnswer != null) return;
        var w = Frame(title, 440, 170);
        w.SizeToContent = SizeToContent.Height;
        w.CanResize = false;
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, IsCancel = true, Classes = { "accent" }, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 16),
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (IBrush?)Application.Current!.FindResource("Text1") },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), Children = { ok } },
            },
        };
        await w.ShowDialog(owner);
    }

    /// <summary>ファイルの種類 (名前と拡張子)。</summary>
    public sealed record FileKind(string Name, params string[] Extensions);

    public static readonly FileKind[] MediaKinds =
    [
        new("動画・音声ファイル", "mp4", "m4a", "mov", "mkv", "webm", "avi", "wmv", "mp3", "wav", "flac", "ogg", "opus", "aac", "wma", "caf", "aiff"),
        new("すべてのファイル", "*"),
    ];

    private static List<FilePickerFileType> Types(IEnumerable<FileKind> kinds) =>
        kinds.Select(k => new FilePickerFileType(k.Name)
        {
            Patterns = k.Extensions.Select(e => e == "*" ? "*" : "*." + e).ToList(),
            AppleUniformTypeIdentifiers = k.Extensions.Contains("*") ? ["public.item"] : null,
        }).ToList();

    /// <summary>開くファイルを選ぶ。選ばなければ null。</summary>
    public static async Task<string?> OpenFileAsync(Window owner, string title, IEnumerable<FileKind> kinds, string? folder = null)
    {
        var options = new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = Types(kinds) };
        if (folder != null && Directory.Exists(folder))
            options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(folder);
        var files = await owner.StorageProvider.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>保存先を選ぶ。選ばなければ null。</summary>
    public static async Task<string?> SaveFileAsync(Window owner, string title, string name, IEnumerable<FileKind> kinds)
    {
        var list = kinds.ToList();
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = name,
            FileTypeChoices = Types(list),
            DefaultExtension = list[0].Extensions[0],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>文字をクリップボードに入れる。</summary>
    public static async Task<bool> CopyAsync(Window owner, string text)
    {
        try
        {
            if (owner.Clipboard is not { } clipboard) return false;
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            App.Log("Clipboard", ex);
            return false;
        }
    }

    // ───────── 用語の登録・覚えた声 ─────────

    /// <summary>用語の登録。保存なら新しい一覧、取り消しなら null。</summary>
    public static async Task<List<string>?> EditTermsAsync(Window owner, IReadOnlyList<string> terms)
    {
        var w = Frame("GetText — 用語の登録", 560, 520);
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
        var ok = new Button { Content = "保存", MinWidth = 90, Classes = { "accent" }, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "取り消し", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Text = string.Join("\n", terms),
            FontSize = 14,
        };
        root.Children.Add(box);
        w.Content = root;
        List<string>? result = null;
        ok.Click += (_, _) =>
        {
            result = (box.Text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) =>
        {
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        };
        await w.ShowDialog(owner);
        return result;
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
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>覚えた声の一覧。忘れる名前 (すべてなら null) を forget に渡す。</summary>
    public static async Task ManageVoicesAsync(Window owner, Action<string?> forget)
    {
        var w = Frame("GetText — 覚えた声", 460, 460);
        var root = new DockPanel { Margin = new Thickness(16) };
        var help = Caption(
            "話者に名前を付けると、その人の声の特徴を覚え、次の会議から似た声の話者に自動で名前を付けます。" +
            "覚えるのは声の特徴 (数値) だけで、音声は保存しません。保存先: ~/Library/Application Support/GetText/voices.json");
        help.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(help, Dock.Top);
        root.Children.Add(help);
        var remove = new Button { Content = "選んだ声を忘れる", MinWidth = 120 };
        var clear = new Button { Content = "すべて忘れる", MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
        var close = new Button { Content = "閉じる", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { remove, clear, close } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var list = new ListBox { SelectionMode = SelectionMode.Multiple };
        root.Children.Add(list);
        List<(string Name, int Count)> voices = [];
        void Refresh()
        {
            voices = LoadVoices();
            list.ItemsSource = voices.Select(v => $"{v.Name}  ({v.Count} 回)").ToList();
            remove.IsEnabled = clear.IsEnabled = voices.Count > 0;
        }
        remove.Click += async (_, _) =>
        {
            foreach (var i in list.Selection.SelectedIndexes.ToList()) forget(voices[i].Name);
            await Task.Delay(300); // 忘れるのは裏の処理が保存し直すので、少し待ってから読み直す
            Refresh();
        };
        clear.Click += async (_, _) =>
        {
            if (!await ConfirmAsync(w, "覚えた声をすべて忘れますか？", "GetText")) return;
            forget(null);
            await Task.Delay(300);
            Refresh();
        };
        close.Click += (_, _) => w.Close();
        w.Content = root;
        Refresh();
        await w.ShowDialog(owner);
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
