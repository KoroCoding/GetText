using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GetText;

/// <summary>録画するもの (ウィンドウ、または画面全体)。</summary>
public sealed record RecordTarget(IntPtr Handle, bool IsMonitor, int ProcessId, string Name, string Label, bool IsPlaying = false)
{
    public override string ToString() => Label;
}

/// <summary>
/// 画面の録画: 選んだウィンドウ (または画面全体) を、その音も入れて MP4 に録画する (<see cref="ScreenRecorder"/>)。
/// </summary>
public partial class RecorderWindow : Window
{
    private readonly AppSettings _settings;
    private ScreenRecorder? _recorder;
    private bool _busy;
    private bool _closeConfirmed;
    private string? _lastPath;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private CapturePreview? _preview;
    private WriteableBitmap? _previewBitmap;
    private DateTime _previewStarted;
    private bool _previewShown;

    /// <summary>録画中か (アプリを閉じる前の確認に使う)。</summary>
    public bool IsRecording => _recorder != null;

    /// <summary>録画を始めた・止めたとき (機能を選ぶ画面の表示を変える)。</summary>
    public event Action? RecordingChanged;

    public RecorderWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        QualityBox.ItemsSource = RecordOptionsList.Quality;
        QualityBox.SelectedIndex = settings.RecordHighQuality ? 1 : 0;
        FpsBox.ItemsSource = RecordOptionsList.Fps;
        FpsBox.SelectedItem = RecordOptionsList.Fps.FirstOrDefault(o => o.Value == settings.RecordFps) ?? RecordOptionsList.Fps[1];
        AudioCheck.IsChecked = settings.RecordAudio;
        MicCheck.IsChecked = settings.RecordMic;
        FolderText.Text = Folder;
        PreviewCheck.IsChecked = settings.RecordPreview;
        _clock.Tick += (_, _) => UpdateClock();
        _previewTimer.Tick += (_, _) => UpdatePreview();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            PowerRequest.Release(this);
            StopPreview();
        };
        // しまっている間はプレビューを止める (軽くする)
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) _previewTimer.Stop();
            else if (PreviewOn) _previewTimer.Start();
        };
        if (!ScreenRecorder.IsSupported)
        {
            RecordButton.IsEnabled = false;
            StatusText.Text = "この Windows では画面を録画できません (Windows 10 バージョン 2004 以降が必要です)";
        }
        // 小さな画面 (1280×720・拡大率 150% など) では、プレビューを低くして画面に収める
        Loaded += (_, _) => Dispatcher.BeginInvoke(FitToScreen, DispatcherPriority.Loaded);
        if (App.DemoMode) return;
        Loaded += (_, _) => RefreshTargets();
    }

    /// <summary>保存先 (設定が空なら「ビデオ\GetText」)。</summary>
    private string Folder => string.IsNullOrWhiteSpace(_settings.RecordFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "GetText")
        : _settings.RecordFolder;

    /// <summary>見本の画面 (動作確認・操作手順の画像用)。recording なら録画中の見た目、preview は見本のプレビューの絵 (BGRA)。</summary>
    internal void LoadDemo(bool recording = false, (byte[] Pixels, int Width, int Height)? preview = null)
    {
        TargetBox.ItemsSource = new List<RecordTarget>
        {
            new(IntPtr.Zero, false, 1, "定例ミーティング", "🔊 定例ミーティング  —  Zoom", true),
            new(IntPtr.Zero, false, 2, "チーム会議", "　 チーム会議  —  ms-teams"),
            new(IntPtr.Zero, true, 0, "画面全体1", "🖥 画面全体 1 (メイン)  —  2560×1440・GetText 以外のすべての音"),
        };
        TargetBox.SelectedIndex = 0;
        // 見本の画面には、この PC の利用者名の入った場所を出さない
        var folder = @"C:\Users\(ユーザー名)\Videos\GetText";
        FolderText.Text = folder;
        _lastPath = Path.Combine(folder, "録画_20261004_101500_定例ミーティング.mp4");
        LastText.Text = "録画_20261004_101500_定例ミーティング.mp4  (00:42:10・512.3 MB)";
        LastPanel.Visibility = Visibility.Visible;
        StatusText.Text = "保存しました: " + _lastPath;
        // 見本のプレビュー (会議アプリの窓に見立てた絵)
        int w = 640, h = 360;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool bar = y < 28, tile = y > 60 && y < 300 && (x / 160 + y / 120) % 2 == 0;
                pixels[i] = (byte)(bar ? 240 : tile ? 120 : 60);
                pixels[i + 1] = (byte)(bar ? 240 : tile ? 90 : 50);
                pixels[i + 2] = (byte)(bar ? 240 : tile ? 60 : 45);
            }
        if (preview is { } p) ShowFrame(p.Pixels, p.Width, p.Height, bottomUp: false);
        else ShowFrame(pixels, w, h, bottomUp: false);
        if (recording)
        {
            TargetBox.IsEnabled = AudioCheck.IsEnabled = MicCheck.IsEnabled = QualityBox.IsEnabled = FpsBox.IsEnabled = false;
            RecordIcon.Text = "";
            RecordLabel.Text = "録画を停止";
            RecDot.Visibility = Visibility.Visible;
            ElapsedText.Text = "00:12:34";
            SizeText.Text = "152.6 MB";
            LastPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "録画中 ・ 定例ミーティング ・ 1920×1040・30 コマ/秒 ・ アプリの音 + マイク";
        }
    }

    // ───────── 録画するもの ─────────

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshTargets();

    private void RefreshTargets()
    {
        if (_recorder != null) return;
        var previous = TargetBox.SelectedItem as RecordTarget;
        var list = new List<RecordTarget>();
        try
        {
            foreach (var w in WindowList.GetEachWindow())
                list.Add(new RecordTarget(w.Handle, false, w.ProcessId, w.Title,
                    $"{(w.IsPlaying ? "🔊 " : "　 ")}{w.Title}  —  {w.ProcessName}", w.IsPlaying));
        }
        catch (Exception ex)
        {
            App.Log("RecordWindows", ex);
        }
        int number = 1;
        foreach (var (monitor, primary, width, height) in Monitors())
            list.Add(new RecordTarget(monitor, true, 0, $"画面全体{number}",
                $"🖥 画面全体 {number++}{(primary ? " (メイン)" : "")}  —  {width}×{height}・GetText 以外のすべての音"));
        TargetBox.ItemsSource = list;
        TargetBox.SelectedItem = list.FirstOrDefault(t => previous != null && t.Handle == previous.Handle) ?? list.FirstOrDefault();
        if (list.Count == 0) StatusText.Text = "録画できるウィンドウが見つかりません";
    }

    private void TargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_recorder == null) RestartPreview();
        var target = TargetBox.SelectedItem as RecordTarget;
        AudioCheck.Content = target?.IsMonitor == true ? "すべての音も録音する (GetText 以外)" : "このアプリの音も録音する";
        UpdateTargetWarning();
    }

    /// <summary>録画するものの注意 (取り込みを禁止している窓・画面の更新より多いコマ数)。</summary>
    private void UpdateTargetWarning()
    {
        var target = TargetBox.SelectedItem as RecordTarget;
        var notes = new List<string>();
        if (target is { IsMonitor: false } && ScreenRecorder.IsCaptureProtected(target.Handle))
            notes.Add("このウィンドウは画面の取り込みを禁止しています (主催者の設定・著作権保護など)。録画すると黒い画面になります。");
        int fps = FpsBox.SelectedItem is Option<int> o ? o.Value : 30;
        if (target != null && RefreshRate(target.Handle, target.IsMonitor) is int hz && fps > hz + 1)
            notes.Add($"この画面は {hz}Hz なので、{fps} コマ/秒を選んでも {hz} コマ/秒より多くはなりません (同じ画面がくり返されます)。{fps}Hz 以上の画面に出して録画してください。");
        TargetWarning.Text = string.Join("\n", notes);
        TargetWarning.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Option_Click(object sender, RoutedEventArgs e) => SaveOptions();

    private void Option_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SaveOptions();
        UpdateTargetWarning();
    }

    private void SaveOptions()
    {
        _settings.RecordAudio = AudioCheck.IsChecked == true;
        _settings.RecordMic = MicCheck.IsChecked == true;
        _settings.RecordHighQuality = QualityBox.SelectedItem is Option<bool> { Value: true };
        if (FpsBox.SelectedItem is Option<int> fps) _settings.RecordFps = fps.Value;
        _settings.Save();
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "録画の保存先", InitialDirectory = Directory.Exists(Folder) ? Folder : null };
        if (dialog.ShowDialog(this) != true) return;
        _settings.RecordFolder = dialog.FolderName;
        _settings.Save();
        FolderText.Text = Folder;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{Folder}\"");
        }
        catch (Exception ex)
        {
            StatusText.Text = "フォルダーを開けませんでした: " + ex.Message;
        }
    }

    // ───────── 録画 ─────────

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_recorder != null) await StopAsync();
        else await StartAsync();
    }

    private Task _work = Task.CompletedTask;     // 始めている・止めている途中の処理
    private ScreenRecorder? _finishing;          // 書き終えている途中の録画
    private bool _lastStopOk;

    /// <summary>録画中、または始めている・書き終えている途中か (この間に GetText を終えるとファイルが壊れる)。</summary>
    public bool IsBusy => _recorder != null || _busy;

    /// <summary>始めている途中なら待ち、録画中なら止めて、書き終えるまで待つ。</summary>
    public async Task StopAndWaitAsync()
    {
        // 待っている間に別の止める処理 (ウィンドウが閉じられたなど) が始まることもあるので、落ち着くまで待つ
        while (true)
        {
            var work = _work;
            await work;
            if (_recorder != null)
            {
                await StopAsync();
                continue;
            }
            if (ReferenceEquals(work, _work)) break;
        }
    }

    private Task StartAsync() => _work = StartCoreAsync();

    private async Task StartCoreAsync()
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
        var path = UniquePath(Path.Combine(Folder, $"録画_{DateTime.Now:yyyyMMdd_HHmmss}_{SafeName(target.Name)}.mp4"));
        var options = new RecordOptions(_settings.RecordFps, _settings.RecordHighQuality, _settings.RecordAudio, _settings.RecordMic,
            _settings.MinutesMicDevice);
        try
        {
            var recorder = target.IsMonitor
                ? await ScreenRecorder.StartMonitorAsync(target.Handle, path, options)
                : await ScreenRecorder.StartWindowAsync(target.Handle, target.ProcessId, path, options);
            _recorder = recorder;
            _preview?.Dispose(); // 録画中は録画している画面をプレビューに使う (取り込みを重ねない)
            _preview = null;
            Action<string> failed = message => Dispatcher.BeginInvoke(async () =>
            {
                if (_recorder != recorder) return;
                await StopAsync();
                if (_lastStopOk) StatusText.Text = message + "。そこまでの録画を保存しました。";
            });
            recorder.Failed += failed;
            if (recorder.Failure is { } early) failed(early); // 始めている間にウィンドウが閉じられた
            SetRecordingUi(true);
            var audio = (options.WindowAudio ? (target.IsMonitor ? "すべての音" : "アプリの音") : "") +
                        (options.Microphone ? (options.WindowAudio ? " + マイク" : "マイク") : "");
            StatusText.Text = $"録画中 ・ {target.Name} ・ {recorder.Width}×{recorder.Height}・{options.Fps} コマ/秒" +
                              (audio.Length > 0 ? " ・ " + audio : " ・ 音なし");
        }
        catch (Exception ex)
        {
            App.Log("RecordStart", ex);
            StatusText.Text = "録画を始められませんでした: " + ex.Message;
            try { if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path); } catch { }
        }
        finally
        {
            _busy = false;
            RecordButton.IsEnabled = ScreenRecorder.IsSupported;
        }
    }

    /// <summary>録画を止めて保存する。</summary>
    public Task StopAsync() => _work = StopCoreAsync();

    private async Task StopCoreAsync()
    {
        var recorder = _recorder;
        if (recorder == null) return;
        _recorder = null;
        _finishing = recorder;
        _lastStopOk = false;
        _busy = true;
        RecordButton.IsEnabled = false;
        _clock.Stop();
        StatusText.Text = "録画を書き終えています…";
        try
        {
            await recorder.StopAsync();
            _lastPath = recorder.Path;
            var size = recorder.FileSize;
            LastText.Text = $"{Path.GetFileName(recorder.Path)}  ({recorder.Duration:hh\\:mm\\:ss}・{FormatSize(size)})";
            LastText.ToolTip = recorder.Path;
            LastPanel.Visibility = Visibility.Visible;
            double captured = recorder.CapturedFps;
            StatusText.Text = "保存しました: " + recorder.Path +
                              (recorder.Options.Fps > 30 && captured > 0
                                  ? $" (取り込めた画面: 平均 {captured:0} コマ/秒。画面が動いていない間は少なくなります)"
                                  : "");
            _lastStopOk = true;
        }
        catch (Exception ex)
        {
            App.Log("RecordStop", ex);
            StatusText.Text = "録画を保存できませんでした: " + ex.Message;
        }
        finally
        {
            SetRecordingUi(false);
            _finishing = null;
            _busy = false;
            RecordButton.IsEnabled = ScreenRecorder.IsSupported;
            RestartPreview();
        }
    }

    // ───────── プレビュー ─────────

    private void FitToScreen()
    {
        var work = ScreenUtil.WorkArea(new Rect(Left, Top, ActualWidth, ActualHeight), this);
        double over = ActualHeight - (work.Height - 16);
        if (over <= 0) return;
        PreviewFrame.Height = Math.Max(110, PreviewFrame.Height - over);
        if (Top + ActualHeight > work.Bottom || Top < work.Top) Top = work.Top + 8;
    }

    private bool PreviewOn => PreviewCheck.IsChecked == true;

    private void PreviewCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.RecordPreview = PreviewOn;
        _settings.Save();
        RestartPreview();
    }

    /// <summary>選んでいるもののプレビューを始め直す (録画中は録画している画面を映す)。</summary>
    private void RestartPreview()
    {
        StopPreview();
        _previewShown = false;
        _previewStarted = DateTime.Now;
        if (!PreviewOn)
        {
            ShowPreviewHint("プレビューを止めています (「表示する」で映します)");
            return;
        }
        if (WindowState != WindowState.Minimized) _previewTimer.Start();
        if (_recorder != null) return;
        if (App.DemoMode || TargetBox.SelectedItem is not RecordTarget target)
        {
            ShowPreviewHint("録画するウィンドウを選ぶと、ここに映ります");
            return;
        }
        ShowPreviewHint("映しています…");
        _preview = CapturePreview.TryStart(target.Handle, target.IsMonitor);
        if (_preview == null)
        {
            ShowPreviewHint("このウィンドウは映せません");
            return;
        }
        var preview = _preview;
        preview.Closed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_preview != preview) return;
            ShowPreviewHint("ウィンドウが閉じられました。一覧を読み直してください");
        });
    }

    private void StopPreview()
    {
        _previewTimer.Stop();
        _preview?.Dispose();
        _preview = null;
        _previewBitmap = null;
        PreviewImage.Source = null;
        PreviewSize.Text = "";
    }

    private void ShowPreviewHint(string text)
    {
        PreviewHint.Text = text;
        PreviewHint.Visibility = Visibility.Visible;
    }

    private void UpdatePreview()
    {
        if (!PreviewOn) return;
        if (_recorder is { } recorder)
            recorder.CopyPreview((pixels, width, height) => ShowFrame(pixels, width, height, bottomUp: true));
        else if (_preview?.TryTake(out var pixels, out int width, out int height) == true)
            ShowFrame(pixels, width, height, bottomUp: false);
        else if (!_previewShown && _preview != null && DateTime.Now - _previewStarted > TimeSpan.FromSeconds(2))
            ShowPreviewHint("まだ映りません (ウィンドウを最小化していると映りません)");
    }

    private void ShowFrame(byte[] pixels, int width, int height, bool bottomUp)
    {
        if (width <= 0 || height <= 0) return;
        if (_previewBitmap == null || _previewBitmap.PixelWidth != width || _previewBitmap.PixelHeight != height)
        {
            _previewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            PreviewImage.Source = _previewBitmap;
        }
        _previewBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        // 録画している画面は下の行から並んでいるので、上下を戻して映す
        PreviewImage.RenderTransform = bottomUp ? new ScaleTransform(1, -1) : Transform.Identity;
        PreviewSize.Text = $"{width}×{height}";
        PreviewHint.Visibility = Visibility.Collapsed;
        _previewShown = true;
    }

    /// <summary>Windows のログオフ・シャットダウン: 待たずに止めて書き終える (最大 10 秒)。書き終えている途中の録画も待つ。</summary>
    public void StopNow()
    {
        var recorder = _recorder ?? _finishing;
        if (recorder == null) return;
        bool wasRecording = _recorder != null;
        _recorder = null;
        try { Task.Run(recorder.StopAsync).Wait(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) { App.Log("RecordSessionEnd", ex); }
        if (wasRecording) SetRecordingUi(false);
        PowerRequest.Release(this);
    }

    private void SetRecordingUi(bool recording)
    {
        TargetBox.IsEnabled = AudioCheck.IsEnabled = MicCheck.IsEnabled = QualityBox.IsEnabled = FpsBox.IsEnabled = !recording;
        RecordIcon.Text = recording ? "" : ""; // 停止 / 録画
        RecordLabel.Text = recording ? "録画を停止" : "録画を開始";
        RecDot.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (recording)
        {
            _clock.Start();
            PowerRequest.Acquire(this); // 録画中はスリープしない
        }
        else
        {
            PowerRequest.Release(this);
            Title = "GetText — 画面の録画";
        }
        UpdateClock();
        RecordingChanged?.Invoke();
    }

    private void UpdateClock()
    {
        if (_recorder is not { } recorder)
        {
            ElapsedText.Text = "00:00:00";
            SizeText.Text = "";
            return;
        }
        ElapsedText.Text = recorder.Duration.ToString(@"hh\:mm\:ss");
        SizeText.Text = FormatSize(recorder.FileSize);
        Title = $"● 録画中 {ElapsedText.Text} — GetText";
    }

    private void PlayLast_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPath == null || !File.Exists(_lastPath)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_lastPath) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "再生できませんでした: " + ex.Message; }
    }

    private void ShowLast_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPath == null) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_lastPath}\""); } catch { }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_recorder == null && !_busy) return;
        e.Cancel = true;
        if (_busy || _closeConfirmed) return;
        if (MessageBox.Show(this, "録画中です。録画を止めて保存し、閉じますか？", "GetText — 画面の録画",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return;
        _closeConfirmed = true;
        await StopAsync();
        Close();
    }

    // ───────── 補助 ─────────

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{bytes / 1024} KB";

    private static string SafeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
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

    // 画面の更新の速さ (リフレッシュレート、Hz)。分からなければ null
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    internal static int? RefreshRate(IntPtr handle, bool isMonitor)
    {
        try
        {
            var monitor = isMonitor ? handle : MonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
            var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(info.szDevice, -1 /* ENUM_CURRENT_SETTINGS */, ref mode)) return null;
            return mode.dmDisplayFrequency > 1 ? mode.dmDisplayFrequency : null;
        }
        catch
        {
            return null;
        }
    }

    // 画面 (モニター) の一覧
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public int dwFlags;
    }

    private static List<(IntPtr Monitor, bool Primary, int Width, int Height)> Monitors()
    {
        var list = new List<(IntPtr, bool, int, int)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
                list.Add((monitor, (info.dwFlags & 1) != 0, info.MonitorRight - info.MonitorLeft, info.MonitorBottom - info.MonitorTop));
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.Item2).ToList();
    }
}

/// <summary>
/// 記録中・録画中にスリープしないよう Windows に頼む。議事録と録画が同時に頼んでも、両方が終わるまで続ける。
/// </summary>
public static class PowerRequest
{
    private static readonly HashSet<object> Holders = [];

    public static void Acquire(object owner)
    {
        if (App.DemoMode) return;
        if (Holders.Add(owner) && Holders.Count == 1)
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);
    }

    public static void Release(object owner)
    {
        if (Holders.Remove(owner) && Holders.Count == 0)
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
    }
}
