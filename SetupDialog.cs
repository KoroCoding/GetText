using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace GetText;

/// <summary>
/// AI の機能のセットアップ (設定 → モデルとセットアップ → セットアップを実行)。入れる機能 (フル・標準・最小) を、この PC に合う
/// おすすめ付きで選んでもらい、setup.bat (Python が無ければ入れるところから行う) を起動する。
/// </summary>
internal static class SetupDialog
{
    private sealed record Choice(string Mode, string Title, string Detail, double DownloadGb, double NeedGb);

    private static readonly Choice[] Choices =
    [
        new("Full", "フル", "文字の読み取り・英語の翻訳・議事録 (文脈による聞き間違いの補正・要約まで)", 8, 10),
        new("Standard", "標準", "フルから「文脈による聞き間違いの補正」と「要約」を除く (GPU の無い PC やメモリ 16GB 未満の PC 向け)", 4, 6),
        new("Minimal", "最小", "文字の読み取りと英語の翻訳だけ (議事録は使えません)", 1, 2),
    ];

    /// <summary>この PC に合う機能 (NVIDIA の GPU とメモリ 16GB 以上ならフル、メモリ 8GB 以上なら標準)。</summary>
    public static (string Mode, string Reason) Recommend()
    {
        bool nvidia = File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll")); // NVIDIA のドライバーがあれば入っている
        double ramGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024));
        if (nvidia && ramGb >= 15) return ("Full", $"NVIDIA の GPU とメモリ {ramGb:0}GB があるため");
        if (ramGb >= 7) return ("Standard", nvidia ? $"メモリが {ramGb:0}GB のため (補正の AI は約 8GB 使います)" : "GPU が無いため (補正の AI は CPU では重く、議事録が遅れます)");
        return ("Minimal", $"メモリが {ramGb:0}GB と少ないため");
    }

    /// <summary>入れる機能を選んでもらう。取り消しなら null。</summary>
    public static string? Ask(Window owner)
    {
        var (recommended, reason) = Recommend();
        // 入っている AI の部品 (取得済みのモデルなど) はそのまま使うので、その分は要らない (setup.ps1 と同じ)
        double haveGb = 0;
        try
        {
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "offline");
            if (Directory.Exists(installed))
                haveGb = new DirectoryInfo(installed).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / (1024.0 * 1024 * 1024);
        }
        catch
        {
            // 調べられないときは差し引かない
        }
        double Need(Choice c) => Math.Max(1, c.NeedGb - haveGb);
        double freeGb = double.MaxValue; // 調べられないとき (ネットワーク上のフォルダなど) は空き容量で止めない
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!);
            if (drive.IsReady) freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
        }
        catch (ArgumentException)
        {
        }

        var w = new Window
        {
            Owner = owner,
            Title = "GetText — AI の機能のセットアップ",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont"),
            FontSize = 13,
        };
        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(Text("入れる機能を選んでください。あとからもう一度セットアップすれば追加できます。", 0, 12));
        string? selected = recommended;
        var radios = new List<RadioButton>();
        foreach (var c in Choices)
        {
            bool enough = freeGb >= Need(c);
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = $"{c.Title} ・ ダウンロード 約 {c.DownloadGb:0}GB" + (c.Mode == recommended ? "　(おすすめ)" : ""),
                FontWeight = FontWeights.SemiBold,
            });
            panel.Children.Add(Text(c.Detail + (enough ? "" : $"\n空きが足りません (必要 約 {Need(c):0.#}GB)"), 0, 0));
            var radio = new RadioButton { Content = panel, GroupName = "mode", IsChecked = c.Mode == recommended && enough, IsEnabled = enough, Margin = new Thickness(0, 0, 0, 10) };
            radio.Checked += (_, _) => selected = c.Mode;
            radios.Add(radio);
            root.Children.Add(radio);
        }
        if (!radios.Any(r => r.IsChecked == true)) selected = Choices.FirstOrDefault(c => freeGb >= Need(c))?.Mode;
        if (selected != null) radios[Array.FindIndex(Choices, c => c.Mode == selected)].IsChecked = true;

        root.Children.Add(Text($"おすすめの理由: {reason}。\n空き容量: {(freeGb == double.MaxValue ? "不明" : $"{freeGb:0.#}GB")} ・ 回線によっては 30 分以上かかります。" +
                               "\nセットアップの間は GetText を閉じます (記録中の議事録は保存されます)。終わると GetText を起動できます。", 4, 14));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "セットアップを始める", MinWidth = 140, IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButtonStyle"), IsEnabled = selected != null };
        var cancel = new Button { Content = "取り消し", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        ok.Click += (_, _) => w.DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        w.Content = root;
        return w.ShowDialog() == true ? selected : null;
    }

    private static TextBlock Text(string text, double top, double bottom) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, bottom),
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextFillColorSecondaryBrush"),
    };

    /// <summary>setup.bat の場所 (配布版は GetText.exe の隣、ソースから動かしているときはプロジェクトのフォルダ)。</summary>
    public static string? FindSetupScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
        {
            var bat = Path.Combine(dir.FullName, "setup.bat");
            if (File.Exists(bat) && File.Exists(Path.Combine(dir.FullName, "setup.ps1"))) return bat;
        }
        return null;
    }

    /// <summary>選んだ機能で setup.bat を起動する (コマンドの窓で進み具合を見せる)。</summary>
    public static bool Start(string script, string mode)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\" -Mode {mode}\"")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(script)!,
            });
            return true;
        }
        catch (Exception ex)
        {
            App.Log("Setup", ex);
            return false;
        }
    }
}
