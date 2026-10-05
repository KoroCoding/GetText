using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace GetText;

/// <summary>録画するもの (ウィンドウ、または画面全体)。</summary>
public sealed record RecordTarget(int WindowId, int DisplayId, string Name, string Label, bool IsPlaying = false)
{
    public bool IsDisplay => WindowId == 0;
    public override string ToString() => Label;
}

/// <summary>
/// 画面の録画 (Mac 版): 選んだウィンドウ (または画面全体) を、その音も入れて MP4 に録画する
/// (補助プログラムの ScreenCaptureKit + AVAssetWriter)。
/// </summary>
public partial class RecorderWindow : Window
{
    private readonly AppSettings _settings;
    private bool _recording;
    private bool _busy;
    private bool _closeConfirmed;
    private string? _path;
    private string? _lastPath;
    private DateTime _startedAt;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _previewBusy;
    private int _previewFailures;

    public bool IsRecording => _recording;

    /// <summary>録画中、または始めている・書き終えている途中か (この間に GetText を終えるとファイルが壊れる)。</summary>
    public bool IsBusy => _recording || _busy;

    private int _token;   // 補助プログラムの録画の番号 (議事録の録画と取り違えない)
    private bool _closed; // 閉じた後は、プレビューを動かさない

    /// <summary>始めている途中なら待ち、録画中なら止めて、書き終えるまで待つ。</summary>
    public async Task StopAndWaitAsync()
    {
        while (_busy) await Task.Delay(100);
        if (_recording) await StopAsync();
        while (_busy) await Task.Delay(100);
    }
    public event Action? RecordingChanged;

    public RecorderWindow() : this(new AppSettings()) { }

    public RecorderWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        QualityBox.ItemsSource = RecordOptionsList.Quality;
        QualityBox.SelectedIndex = settings.RecordHighQuality ? 1 : 0;
        FpsBox.ItemsSource = RecordOptionsList.Fps;
        FpsBox.SelectedItem = RecordOptionsList.Fps.FirstOrDefault(o => o.Value == settings.RecordFps) ?? RecordOptionsList.Fps[1];
        AudioCheck.IsChecked = settings.RecordAudio;
        FolderText.Text = Folder;
        PreviewCheck.IsChecked = settings.RecordPreview;
        _clock.Tick += (_, _) => UpdateClock();
        _previewTimer.Tick += async (_, _) => await UpdatePreviewAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _previewTimer.Stop();
        };
        Closing += OnClosing;
        Closed += (_, _) => KeepAwake(false);
        MacHelper.Instance.EventReceived += OnHelperEvent;
        Closed += (_, _) => MacHelper.Instance.EventReceived -= OnHelperEvent;
        if (App.DemoMode) return;
        Opened += async (_, _) => await RefreshTargetsAsync();
    }

    private string Folder => string.IsNullOrWhiteSpace(_settings.RecordFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Movies", "GetText")
        : _settings.RecordFolder;

    /// <summary>見本の画面 (動作確認・操作手順の画像用)。</summary>
    internal void LoadDemo()
    {
        TargetBox.ItemsSource = new List<RecordTarget>
        {
            new(1, 0, "定例ミーティング", "🔊 定例ミーティング  —  zoom.us", true),
            new(2, 0, "チーム会議", "　 チーム会議  —  Microsoft Teams"),
            new(0, 1, "画面全体1", "🖥 画面全体 1 (メイン)  —  1512×982・GetText 以外のすべての音"),
        };
        TargetBox.SelectedIndex = 0;
        _lastPath = Path.Combine(Folder, "録画_20261004_101500_定例ミーティング.mp4");
        LastText.Text = "録画_20261004_101500_定例ミーティング.mp4  (00:42:10・512.3 MB)";
        LastPanel.IsVisible = true;
        StatusText.Text = "保存しました";
    }

    // ───────── 録画するもの ─────────

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshTargetsAsync();

    private async Task RefreshTargetsAsync()
    {
        if (_recording) return;
        var previous = TargetBox.SelectedItem as RecordTarget;
        var list = new List<RecordTarget>();
        try
        {
            var reply = await MacHelper.Instance.RequestAsync("record_targets", timeout: TimeSpan.FromSeconds(15));
            foreach (var w in reply["windows"]?.AsArray() ?? [])
            {
                if (w == null) continue;
                bool playing = w["playing"]?.GetValue<bool>() == true;
                var title = w["title"]?.GetValue<string>() ?? "";
                list.Add(new RecordTarget(w["id"]!.GetValue<int>(), 0, title,
                    $"{(playing ? "🔊 " : "　 ")}{title}  —  {w["app"]?.GetValue<string>()}", playing));
            }
            list = [.. list.OrderByDescending(t => t.IsPlaying)];
            int number = 1;
            foreach (var d in reply["displays"]?.AsArray() ?? [])
            {
                if (d == null) continue;
                bool main = d["main"]?.GetValue<bool>() == true;
                list.Add(new RecordTarget(0, d["id"]!.GetValue<int>(), $"画面全体{number}",
                    $"🖥 画面全体 {number++}{(main ? " (メイン)" : "")}  —  {d["w"]}×{d["h"]}・GetText 以外のすべての音"));
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "ウィンドウの一覧を読めませんでした: " + ex.Message;
        }
        TargetBox.ItemsSource = list;
        TargetBox.SelectedItem = list.FirstOrDefault(t => previous != null && t.WindowId == previous.WindowId && t.DisplayId == previous.DisplayId)
                                 ?? list.FirstOrDefault();
    }

    private void TargetBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        AudioCheck.Content = TargetBox.SelectedItem is RecordTarget { IsDisplay: true } ? "すべての音も録音する (GetText 以外)" : "このアプリの音も録音する";
        RestartPreview();
    }

    // ───────── プレビュー ─────────

    private bool PreviewOn => PreviewCheck.IsChecked == true;

    private void PreviewCheck_Click(object? sender, RoutedEventArgs e)
    {
        _settings.RecordPreview = PreviewOn;
        _settings.Save();
        RestartPreview();
    }

    private void RestartPreview()
    {
        if (_closed) return; // (ウィンドウの一覧を読んでいる間に閉じられた)
        _previewFailures = 0;
        (PreviewImage.Source as IDisposable)?.Dispose();
        PreviewImage.Source = null;
        PreviewSize.Text = "";
        if (!PreviewOn)
        {
            _previewTimer.Stop();
            ShowPreviewHint("プレビューを止めています (「表示する」で映します)");
            return;
        }
        if (App.DemoMode || TargetBox.SelectedItem is not RecordTarget)
        {
            _previewTimer.Stop();
            ShowPreviewHint("録画するウィンドウを選ぶと、ここに映ります");
            return;
        }
        ShowPreviewHint("映しています…");
        _previewTimer.Start();
    }

    private void ShowPreviewHint(string text)
    {
        PreviewHint.Text = text;
        PreviewHint.IsVisible = true;
    }

    // 選んでいるもの (録画中は録画しているもの) を小さく取り込んで映す (補助プログラムの ScreenCaptureKit)
    private async Task UpdatePreviewAsync()
    {
        if (_previewBusy || !PreviewOn || WindowState == WindowState.Minimized || TargetBox.SelectedItem is not RecordTarget target) return;
        _previewBusy = true;
        try
        {
            var reply = await MacHelper.Instance.RequestAsync("record_preview", new JsonObject
            {
                ["window"] = target.WindowId, ["display"] = target.DisplayId, ["max"] = 720,
            }, TimeSpan.FromSeconds(5));
            if (!ReferenceEquals(TargetBox.SelectedItem, target)) return; // 待つ間に選び直した
            var bytes = Convert.FromBase64String(reply["jpeg"]!.GetValue<string>());
            var old = PreviewImage.Source as IDisposable;
            PreviewImage.Source = new Bitmap(new MemoryStream(bytes));
            old?.Dispose();
            PreviewSize.Text = $"{reply["w"]}×{reply["h"]}";
            PreviewHint.IsVisible = false;
            _previewFailures = 0;
        }
        catch (Exception ex)
        {
            if (++_previewFailures == 3)
                ShowPreviewHint("映せません (ウィンドウを最小化している・閉じた・画面収録の許可が無いなど): " + ex.Message);
        }
        finally
        {
            _previewBusy = false;
        }
    }

    private void Option_Click(object? sender, RoutedEventArgs e) => SaveOptions();

    private void Option_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) SaveOptions();
    }

    private void SaveOptions()
    {
        _settings.RecordAudio = AudioCheck.IsChecked == true;
        _settings.RecordHighQuality = QualityBox.SelectedItem is Option<bool> { Value: true };
        if (FpsBox.SelectedItem is Option<int> fps) _settings.RecordFps = fps.Value;
        _settings.Save();
    }

    private async void ChangeFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "録画の保存先" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        _settings.RecordFolder = path;
        _settings.Save();
        FolderText.Text = Folder;
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Folder);
        try { Process.Start(new ProcessStartInfo("open", [Folder]) { UseShellExecute = false }); } catch { }
    }

    // ───────── 録画 ─────────

    private async void Record_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_recording) await StopAsync();
        else await StartAsync();
    }

    private async Task StartAsync()
    {
        if (TargetBox.SelectedItem is not RecordTarget target)
        {
            StatusText.Text = "録画するウィンドウを選んでください";
            return;
        }
        _busy = true;
        RecordButton.IsEnabled = false;
        StatusText.Text = "録画を準備しています…";
        SaveOptions();
        _path = UniquePath(Path.Combine(Folder, $"録画_{DateTime.Now:yyyyMMdd_HHmmss}_{SafeName(target.Name)}.mp4"));
        try
        {
            JsonObject reply;
            int token = MacHelper.NewRecordingToken();
            try
            {
                reply = await MacHelper.Instance.RequestAsync("record_start", new JsonObject
                {
                    ["window"] = target.WindowId, ["display"] = target.DisplayId, ["audio"] = _settings.RecordAudio,
                    ["fps"] = _settings.RecordFps, ["hq"] = _settings.RecordHighQuality, ["path"] = _path, ["token"] = token,
                }, TimeSpan.FromSeconds(20));
            }
            catch
            {
                // 待ちきれなかったなど: 補助プログラムが後から始めた録画を残さない (この番号の録画だけを止める)
                _ = MacHelper.Instance.RequestAsync("record_stop", new JsonObject { ["token"] = token }, TimeSpan.FromSeconds(60)).ContinueWith(_ => { });
                throw;
            }
            _token = token;
            _recording = true;
            _startedAt = DateTime.Now;
            SetRecordingUi(true);
            StatusText.Text = $"録画中 ・ {target.Name} ・ {reply["w"]}×{reply["h"]}・{_settings.RecordFps} コマ/秒" +
                              (_settings.RecordAudio ? (target.IsDisplay ? " ・ すべての音" : " ・ アプリの音") : " ・ 音なし");
        }
        catch (Exception ex)
        {
            App.Log("RecordStart", ex);
            StatusText.Text = "録画を始められませんでした: " + ex.Message;
        }
        finally
        {
            _busy = false;
            RecordButton.IsEnabled = true;
        }
    }

    public async Task StopAsync()
    {
        if (!_recording) return;
        _recording = false;
        _busy = true;
        RecordButton.IsEnabled = false;
        _clock.Stop();
        StatusText.Text = "録画を書き終えています…";
        try
        {
            var reply = await MacHelper.Instance.RequestAsync("record_stop", new JsonObject { ["token"] = _token }, TimeSpan.FromSeconds(60));
            _lastPath = _path;
            double seconds = reply["seconds"]?.GetValue<double>() ?? 0;
            long size = File.Exists(_path) ? new FileInfo(_path!).Length : 0;
            LastText.Text = $"{Path.GetFileName(_path)}  ({TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss}・{FormatSize(size)})";
            ToolTip.SetTip(LastText, _path);
            LastPanel.IsVisible = true;
            StatusText.Text = "保存しました: " + _path;
        }
        catch (Exception ex)
        {
            App.Log("RecordStop", ex);
            StatusText.Text = "録画を保存できませんでした: " + ex.Message;
        }
        finally
        {
            SetRecordingUi(false);
            _busy = false;
            RecordButton.IsEnabled = true;
        }
    }

    // 補助プログラムから: 録画が途中で止まった (ウィンドウを閉じた・許可を外したなど)
    private void OnHelperEvent(JsonObject json)
    {
        var kind = json["event"]?.GetValue<string>();
        if (kind is not ("record_error" or "helper_exited")) return;
        int? token = json["token"]?.GetValue<int>();
        var message = json["message"]?.GetValue<string>() ?? "補助プログラムが止まりました";
        Dispatcher.UIThread.Post(async () =>
        {
            if (!_recording) return;
            if (kind == "helper_exited")
            {
                // 補助プログラムが止まった: 録画は書き終えられていないので、止める要求は送らない
                _recording = false;
                SetRecordingUi(false);
                StatusText.Text = "補助プログラムが止まったため、録画が止まりました (録画のファイルは再生できない可能性があります)。";
                return;
            }
            if (token != null && token != _token) return; // (議事録が始めた録画の知らせ)
            await StopAsync();
            if (LastPanel.IsVisible && _lastPath == _path) StatusText.Text = "録画が止まりました (" + message + ")。そこまでを保存しました。";
        });
    }

    private void SetRecordingUi(bool recording)
    {
        TargetBox.IsEnabled = AudioCheck.IsEnabled = QualityBox.IsEnabled = FpsBox.IsEnabled = !recording;
        RecordLabel.Text = recording ? "■ 録画を停止" : "● 録画を開始";
        if (recording) _clock.Start();
        KeepAwake(recording); // 録画中はスリープしない
        if (!recording) Title = "GetText — 画面の録画";
        UpdateClock();
        RecordingChanged?.Invoke();
    }

    private void UpdateClock()
    {
        if (!_recording)
        {
            ElapsedText.Text = "00:00:00";
            SizeText.Text = "";
            return;
        }
        ElapsedText.Text = (DateTime.Now - _startedAt).ToString(@"hh\:mm\:ss");
        SizeText.Text = _path != null && File.Exists(_path) ? FormatSize(new FileInfo(_path).Length) : "";
        Title = $"● 録画中 {ElapsedText.Text} — GetText";
    }

    private void PlayLast_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastPath == null || !File.Exists(_lastPath)) return;
        try { Process.Start(new ProcessStartInfo("open", [_lastPath]) { UseShellExecute = false }); } catch { }
    }

    private void ShowLast_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastPath == null) return;
        try { Process.Start(new ProcessStartInfo("open", ["-R", _lastPath]) { UseShellExecute = false }); } catch { }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_recording && !_busy) return;
        e.Cancel = true;
        if (_busy || _closeConfirmed) return;
        if (!await Dialogs.ConfirmAsync(this, "録画中です。録画を止めて保存し、閉じますか？", "GetText — 画面の録画", "止めて保存")) return;
        _closeConfirmed = true;
        await StopAsync();
        Close();
    }

    private Process? _caffeinate;

    /// <summary>録画中は Mac がスリープしないようにする (caffeinate)。</summary>
    private void KeepAwake(bool on)
    {
        if (App.DemoMode || on == (_caffeinate is { HasExited: false })) return;
        try
        {
            if (on)
                _caffeinate = Process.Start(new ProcessStartInfo("/usr/bin/caffeinate", ["-i", "-w", Environment.ProcessId.ToString()])
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            else
            {
                _caffeinate?.Kill();
                _caffeinate?.Dispose();
                _caffeinate = null;
            }
        }
        catch (Exception)
        {
            // スリープを止められなくても録画は続ける
        }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{bytes / 1024} KB";

    private static string SafeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars().Concat([':', '/'])) name = name.Replace(c, '_');
        name = name.Trim();
        return name.Length > 40 ? name[..40] : name.Length == 0 ? "画面" : name;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        for (int n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{n}.mp4");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
