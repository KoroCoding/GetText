using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using GetText.Plugins;
using Microsoft.Win32;

namespace GetText;

/// <summary>
/// 拡張機能のための Windows の部品 (知らせ・ファイルを選ぶ・進み具合・エクスプローラーで開く・クリップボード・URL)。
/// 拡張機能は自分で画面を描かず、ここを通して GetText の部品を使う。
/// </summary>
public sealed class WindowsPluginHost : IPluginHostUi
{
    private static Dispatcher Ui => Application.Current.Dispatcher;

    /// <summary>
    /// 起動したとき: 記録を準備し、ホームを出した後 (手が空いたとき) に読み込む。
    /// Safe Mode (--safe-mode・環境変数 GETTEXT_SAFE_MODE=1) では拡張機能を読み込まない。
    /// </summary>
    public static void Start(string[] args, TextWindow text, Action? openExtensions)
    {
        bool safeMode = args.Contains("--safe-mode", StringComparer.OrdinalIgnoreCase)
            || Environment.GetEnvironmentVariable("GETTEXT_SAFE_MODE") is "1" or "true";
        var host = new WindowsPluginHost();
        PluginRuntime.RegisterService<IPluginUi>(host);
        PluginRuntime.RegisterService<IScreenCaptureService>(new WindowsScreenCapture());
        // 読み取り・翻訳は、読み取りの画面と同じ方式・補助プロセスを使う (この PC の中だけ)
        PluginRuntime.RegisterService<IOcrService>(new HostOcrService([new AiOcrProvider(text.AiOcr), new WindowsOcrProvider()], () => text.PreferredOcrProvider));
        PluginRuntime.RegisterService<ITranslationService>(new LocalTranslationService(text.Translator.Local));
        PluginRuntime.RegisterService<IDocumentService>(new WindowsDocumentService());
        PluginRuntime.RegisterService<IOverlayService>(text.Overlay); // (読み取りの画面と同じ部品。隠すと消える)
        // 開発者向けの API (既定でオフ。設定でオンにしたときだけ 127.0.0.1 で受ける)
        DeveloperApiControl.Host = new DeveloperApiHost
        {
            ReadingText = text.ReadingTextForApi,
            Ocr = PluginRuntime.GetService<IOcrService>(),
            Translation = PluginRuntime.GetService<ITranslationService>(),
            Documents = PluginRuntime.GetService<IDocumentService>(),
        };
        DeveloperApiControl.Apply(text.Settings);
        PluginRuntime.AddBuiltInCapability("ocr");
        PluginRuntime.AddBuiltInCapability("ocr.local");
        PluginRuntime.AddBuiltInCapability("meeting.transcript");
        PluginRuntime.AddBuiltInCapability("screen.record");
        PluginRuntime.Prepare(host, safeMode);
        Ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            PluginRuntime.LoadAll();
            if (PluginRuntime.CrashedPlugin is { } crashed)
            {
                var name = PluginRuntime.Plugins.FirstOrDefault(p => p.Record.Id == crashed)?.Manifest?.Name.For("ja") ?? crashed;
                PluginToast.Show("GetText", new PluginNotification(
                    $"拡張機能「{name}」を止めました",
                    "前回、この拡張機能を読み込んでいる途中で GetText が終了しました。設定 → 拡張機能 で、有効に戻すか消すかを選べます。",
                    PluginNotificationKind.Warning, openExtensions != null ? new LocalizedText("拡張機能を開く") : null, openExtensions));
            }
            else if (PluginRuntime.SafeMode)
            {
                PluginToast.Show("GetText", new PluginNotification("Safe Mode で起動しました",
                    "拡張機能は読み込んでいません。普通に起動し直すと、元に戻ります。", PluginNotificationKind.Info));
            }
        });
    }

    public void ShowNotification(string pluginName, PluginNotification notification) => PluginToast.Show(pluginName, notification);

    public void ContributionsChanged() => Post(() => App.Home?.ReloadFeatures());

    public void Post(Action action) => Ui.BeginInvoke(() =>
    {
        try { action(); }
        catch (Exception ex) { App.Log("Plugin", ex); }
    });

    public Task<IReadOnlyList<string>> PickFilesAsync(LocalizedText title, IReadOnlyList<(string Name, string[] Extensions)> filters, bool multiple,
        CancellationToken cancellationToken) => OnUi<IReadOnlyList<string>>(() =>
    {
        var dialog = new OpenFileDialog { Title = title.For("ja"), Filter = Filter(filters), Multiselect = multiple };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileNames : [];
    });

    public Task<string?> PickFolderAsync(LocalizedText title, CancellationToken cancellationToken) => OnUi(() =>
    {
        var dialog = new OpenFolderDialog { Title = title.For("ja") };
        return dialog.ShowDialog(Owner()) == true ? dialog.FolderName : null;
    });

    public Task<string?> PickSaveFileAsync(LocalizedText title, string suggestedName, IReadOnlyList<(string Name, string[] Extensions)> filters,
        CancellationToken cancellationToken) => OnUi(() =>
    {
        var dialog = new SaveFileDialog { Title = title.For("ja"), Filter = Filter(filters), FileName = Path.GetFileName(suggestedName) };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    });

    public Task<CapturedImage?> SelectScreenRegionAsync(LocalizedText title, CancellationToken cancellationToken) =>
        Ui.InvokeAsync(() => QuickOcr.SelectAndCaptureAsync($"{title.For("ja")} ・ ドラッグで範囲を選ぶ ・ Esc でやめる")).Task.Unwrap();

    /// <summary>一覧から 1 つ選んでもらう (多くてもよいよう、スクロールする一覧。ダブルクリック・Enter でも決まる)。</summary>
    public Task<int?> ChooseAsync(LocalizedText title, LocalizedText? message, IReadOnlyList<LocalizedText> options, CancellationToken cancellationToken) =>
        OnUi<int?>(() =>
        {
            if (options.Count == 0) return null;
            var w = new Window
            {
                Owner = Owner(),
                Title = "GetText — " + title.For("ja"),
                Width = 520,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = Owner() != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont"),
            };
            w.SetResourceReference(Window.FontSizeProperty, "Gt.Font.Body");
            var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            if (message != null)
            {
                var text = new System.Windows.Controls.TextBlock { Text = message.For("ja"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
                text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Gt.TextSecondary");
                root.Children.Add(text);
            }
            var list = new System.Windows.Controls.ListBox
            {
                ItemsSource = options.Select(o => o.For("ja")).ToList(),
                SelectedIndex = 0,
                MaxHeight = 360,
                Margin = new Thickness(0, 0, 0, 14),
            };
            System.Windows.Automation.AutomationProperties.SetName(list, title.For("ja"));
            root.Children.Add(list);
            var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new System.Windows.Controls.Button { Content = "選ぶ", MinWidth = 100, IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButtonStyle") };
            var cancel = new System.Windows.Controls.Button { Content = "取り消し", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            ok.Click += (_, _) => w.DialogResult = list.SelectedIndex >= 0;
            list.MouseDoubleClick += (_, _) => { if (list.SelectedIndex >= 0) w.DialogResult = true; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            w.Content = root;
            w.Loaded += (_, _) => list.Focus();
            using var registration = cancellationToken.Register(() => w.Dispatcher.BeginInvoke(() => { if (w.IsVisible) w.DialogResult = false; }));
            return w.ShowDialog() == true ? list.SelectedIndex : null;
        });

    public IPluginProgress BeginProgress(LocalizedText title)
    {
        IPluginProgress? progress = null;
        Ui.Invoke(() => progress = PluginToast.Progress("GetText", title.For("ja")));
        return progress!;
    }

    /// <summary>エクスプローラーで開く (ファイルなら選んだ状態で)。引数は 1 つに分けて渡し、シェルの文字列にはしない。</summary>
    public void Reveal(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            if (File.Exists(full)) info.ArgumentList.Add("/select," + full);
            else if (Directory.Exists(full)) info.ArgumentList.Add(full);
            else return;
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            App.Log("PluginReveal", ex);
        }
    }

    public Task<bool> CopyTextAsync(string text) => OnUi(() =>
    {
        // ほかのアプリがクリップボードを開いていると失敗する: 少し待って試し直す
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }
        return false;
    });

    public void OpenUrl(Uri url)
    {
        if (!PluginUrls.IsAllowed(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.Log("PluginOpenUrl", ex);
        }
    }

    private static Window? Owner() => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    private static string Filter(IReadOnlyList<(string Name, string[] Extensions)> filters)
    {
        var parts = filters
            .Where(f => f.Extensions.Length > 0)
            .Select(f =>
            {
                var patterns = string.Join(";", f.Extensions.Select(e => "*." + e.TrimStart('.', '*').Replace("|", "")));
                return $"{f.Name.Replace("|", "")} ({patterns})|{patterns}";
            })
            .Append("すべてのファイル (*.*)|*.*");
        return string.Join("|", parts);
    }

    private static Task<T> OnUi<T>(Func<T> func) => Ui.InvokeAsync(func).Task;
}
