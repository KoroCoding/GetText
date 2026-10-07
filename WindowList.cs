using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GetText;

public sealed record WindowInfo(IntPtr Handle, string Title, int ProcessId, string ProcessName, bool IsPlaying = false, int Area = 0)
{
    /// <summary>「GetText 以外のすべての音」を表す特別な項目。</summary>
    public static readonly WindowInfo AllAudio = new(IntPtr.Zero, "すべての音 (GetText 以外)", 0, "", false);

    public bool IsAll => ReferenceEquals(this, AllAudio);

    public override string ToString() =>
        IsAll ? "🔈 " + Title : $"{(IsPlaying ? "🔊 " : "　 ")}{Title}  —  {ProcessName}";
}

/// <summary>音声を取り込む対象に選べる、表示中のウィンドウの一覧。</summary>
public static class WindowList
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// 表示中のアプリ (1 アプリ 1 項目)。音を出しているもの (🔊) を先に並べる。
    /// LINE の通話画面 (LineCall) のように、アプリが子プロセスで開いたウィンドウは親のアプリにまとめる。
    /// 音声は親のプロセスとその子プロセスをまとめて取り込むので、通話の音も入る。
    /// まとめるのは同じアプリのフォルダーにある実行ファイルどうしだけ (エクスプローラーから起動したブラウザを
    /// エクスプローラーにまとめて、一覧から消してしまわないように)。
    /// </summary>
    public static List<WindowInfo> GetWindows()
    {
        var playing = AudioSessions.PlayingProcessTree();
        var windows = GetAllWindows();
        var roots = windows.Select(w => w.ProcessId).Distinct().ToDictionary(pid => pid, pid => AppRoot(ImagePath(pid)));
        return GroupByApp(windows, AudioSessions.ParentMap(), roots)
            .Select(w => w with { IsPlaying = playing.Contains(w.ProcessId) })
            .OrderByDescending(w => w.IsPlaying)
            .ToList();
    }

    /// <summary>
    /// ウィンドウを持つ親プロセスまでたどって、アプリごとに代表のウィンドウ (いちばん大きいもの) を 1 つ選ぶ。
    /// roots (プロセス → アプリのフォルダー) を渡すと、同じアプリのフォルダーの親にだけまとめる。
    /// </summary>
    internal static List<WindowInfo> GroupByApp(List<WindowInfo> windows, IReadOnlyDictionary<int, int> parents,
        IReadOnlyDictionary<int, string?>? roots = null)
    {
        var root = RootFinder(windows, parents, roots);
        return windows
            .GroupBy(w => root(w.ProcessId))
            // アプリ本体 (親プロセス) のウィンドウのうち、いちばん大きいものを代表にする
            // (まとめ先の親は必ずウィンドウを持っているプロセス)
            .Select(g => g.Where(w => w.ProcessId == g.Key).OrderByDescending(w => w.Area).First())
            .ToList();
    }

    /// <summary>
    /// 録画に選べるウィンドウ (アプリにまとめず 1 つずつ。大きい順、音を出しているものを先に)。
    /// 音を取り込むプロセスは、そのアプリのいちばん上の親にする (子プロセスが鳴らす音も入るように)。
    /// </summary>
    public static List<WindowInfo> GetEachWindow()
    {
        var playing = AudioSessions.PlayingProcessTree();
        var windows = GetAllWindows();
        var roots = windows.Select(w => w.ProcessId).Distinct().ToDictionary(pid => pid, pid => AppRoot(ImagePath(pid)));
        var root = RootFinder(windows, AudioSessions.ParentMap(), roots);
        return windows
            .Select(w => w with { ProcessId = root(w.ProcessId) })
            .Select(w => w with { IsPlaying = playing.Contains(w.ProcessId) })
            .OrderByDescending(w => w.IsPlaying).ThenByDescending(w => w.Area)
            .ToList();
    }

    // ウィンドウを持つ親プロセス (同じアプリのフォルダーのもの) までたどる関数を作る
    private static Func<int, int> RootFinder(List<WindowInfo> windows, IReadOnlyDictionary<int, int> parents,
        IReadOnlyDictionary<int, string?>? roots)
    {
        var owners = windows.Select(w => w.ProcessId).ToHashSet();
        bool SameApp(int child, int parent)
        {
            if (roots == null) return true;
            return roots.TryGetValue(child, out var a) && roots.TryGetValue(parent, out var b)
                   && a != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        int Root(int pid)
        {
            for (int depth = 0; depth < 8 && parents.TryGetValue(pid, out int parent) && owners.Contains(parent) && parent != pid
                                && SameApp(pid, parent); depth++)
                pid = parent;
            return pid;
        }
        return Root;
    }

    // アプリのフォルダーとみなす場所: この直下のフォルダーが 1 つのアプリ (長いものから比べる)
    private static readonly string[][] AppBases =
    [
        ["AppData", "Local", "Programs"], ["AppData", "Local"], ["AppData", "Roaming"],
        ["Program Files (x86)"], ["Program Files"], ["WindowsApps"],
    ];

    /// <summary>
    /// 実行ファイルが属するアプリのフォルダー。Program Files や AppData の直下のフォルダー
    /// (例: …\AppData\Local\LINE)。それ以外の場所は実行ファイルのフォルダーそのもの。
    /// </summary>
    internal static string? AppRoot(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        var dir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(dir)) return null;
        var parts = dir.Split('\\');
        for (int i = 0; i < parts.Length; i++)
        {
            foreach (var b in AppBases)
            {
                if (i + b.Length >= parts.Length) continue;
                bool match = true;
                for (int k = 0; k < b.Length && match; k++)
                    match = string.Equals(parts[i + k], b[k], StringComparison.OrdinalIgnoreCase);
                if (match) return string.Join('\\', parts[..(i + b.Length + 1)]);
            }
        }
        return dir;
    }

    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// 画面のその位置 (物理ピクセル) にある、いちばん手前の窓のアプリの名前と題名 (GetText の窓は飛ばす)。無ければ null。
    /// 読み取りの範囲の下のアプリを拡張機能に知らせる (読み取りの履歴で、除くアプリを効かせるため)。
    /// </summary>
    public static (string App, string Title)? WindowAt(int x, int y)
    {
        int self = Environment.ProcessId;
        (string, string)? found = null;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (!GetWindowRect(hwnd, out var r) || x < r.Left || x >= r.Right || y < r.Top || y >= r.Bottom) return true;
            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == self || pid == 0) return true;
            int length = GetWindowTextLength(hwnd);
            var title = new StringBuilder(length + 1);
            if (length > 0) GetWindowText(hwnd, title, title.Capacity);
            try
            {
                var name = Process.GetProcessById(pid).ProcessName;
                if (string.Equals(name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) && HostedApp(hwnd, pid) is int app)
                    name = Process.GetProcessById(app).ProcessName;
                found = (name, title.ToString());
            }
            catch (ArgumentException)
            {
                return true; // (終わったプロセス)
            }
            return false;
        }, IntPtr.Zero);
        return found;
    }

    // ApplicationFrameHost の窓の中にある、別のプロセス (ストアアプリ本体) の画面のプロセス ID
    private static int? HostedApp(IntPtr frame, int framePid)
    {
        int? found = null;
        EnumChildWindows(frame, (child, _) =>
        {
            GetWindowThreadProcessId(child, out int pid);
            if (pid == 0 || pid == framePid) return true;
            found = pid;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static List<WindowInfo> GetAllWindows()
    {
        int self = Environment.ProcessId;
        var list = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            // 仮想デスクトップの別画面やストアアプリの待機中のウィンドウなど、見えていないもの
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
            int length = GetWindowTextLength(hwnd);
            if (length == 0) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == self || pid == 0) return true;
            string name;
            try
            {
                name = Process.GetProcessById(pid).ProcessName;
                // ストアアプリ (Skype・電話リンクなど) の窓は ApplicationFrameHost が持ち、音はアプリ本体が出す
                // (本体は ApplicationFrameHost の子プロセスではないので、窓の中のアプリ本体の画面から本体を探す)
                if (string.Equals(name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) && HostedApp(hwnd, pid) is int app)
                {
                    pid = app;
                    name = Process.GetProcessById(pid).ProcessName;
                }
            }
            catch
            {
                return true; // 終了済み
            }
            if (title.ToString() == "Program Manager") return true; // デスクトップ
            GetWindowRect(hwnd, out var r);
            int area = Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
            list.Add(new WindowInfo(hwnd, title.ToString(), pid, name, Area: area));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
