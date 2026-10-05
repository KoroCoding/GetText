namespace GetText.Tests;

public class WindowListTests
{
    private static WindowInfo W(int pid, string name, string title, int w, int h) =>
        new(new IntPtr(pid * 10 + title.Length), title, pid, name, Area: w * h);

    [Fact]
    public void LINE_の通話画面は親の_LINE_にまとめる()
    {
        // 実際の LINE と同じ構成: LINE 本体 + 子プロセス LineCall の 4 つのウィンドウ (通話画面・透明な全画面など)
        var windows = new List<WindowInfo>
        {
            W(34116, "LineCall", "通話", 1515, 847),
            W(34116, "LineCall", "LINE", 1920, 1080),
            W(34116, "LineCall", "ツールバー", 572, 50),
            W(34116, "LineCall", "オーバーレイ", 1920, 1080),
            W(40948, "LINE", "LINE", 1089, 831),
            W(5000, "chrome", "会議 - Google Chrome", 1600, 900),
            W(5000, "chrome", "別のタブ - Google Chrome", 800, 600),
            W(6000, "Zoom", "Zoom ミーティング", 1280, 720),
        };
        var parents = new Dictionary<int, int> { [34116] = 40948, [40948] = 31532, [5000] = 100, [6000] = 100 };

        var apps = WindowList.GroupByApp(windows, parents);

        Assert.Equal(3, apps.Count);
        var line = Assert.Single(apps, a => a.ProcessName == "LINE");
        Assert.Equal(40948, line.ProcessId); // 親のプロセスを取り込めば、子の LineCall の音も入る
        Assert.Equal("会議 - Google Chrome", apps.Single(a => a.ProcessName == "chrome").Title); // いちばん大きいウィンドウ
        Assert.Contains(apps, a => a.ProcessName == "Zoom");
    }

    [Fact]
    public void エクスプローラーから起動したブラウザはエクスプローラーにまとめない()
    {
        // エクスプローラーのフォルダーの画面が開いていると、そこから起動したアプリの親がウィンドウを持つことになる
        var windows = new List<WindowInfo>
        {
            W(100, "explorer", "ダウンロード", 1000, 700),
            W(5000, "brave", "講演 - YouTube - Brave", 1600, 900),
            W(34116, "LineCall", "通話", 1515, 847),
            W(40948, "LINE", "LINE", 1089, 831),
        };
        var parents = new Dictionary<int, int> { [5000] = 100, [40948] = 100, [34116] = 40948 };
        var roots = new Dictionary<int, string?>
        {
            [100] = WindowList.AppRoot(@"C:\Windows\explorer.exe"),
            [5000] = WindowList.AppRoot(@"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe"),
            [40948] = WindowList.AppRoot(@"C:\Users\u\AppData\Local\LINE\bin\current\LINE.exe"),
            [34116] = WindowList.AppRoot(@"C:\Users\u\AppData\Local\LINE\Data\plugin\LineCall\1.0.0.911\LineCall.exe"),
        };

        var apps = WindowList.GroupByApp(windows, parents, roots);

        Assert.Equal(3, apps.Count);
        Assert.Contains(apps, a => a.ProcessName == "brave");
        Assert.Contains(apps, a => a.ProcessName == "explorer");
        Assert.Equal(40948, Assert.Single(apps, a => a.ProcessName == "LINE").ProcessId); // LINE の通話画面は今までどおりまとめる
    }

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\LINE\bin\current\LINE.exe", @"C:\Users\u\AppData\Local\LINE")]
    [InlineData(@"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe", @"C:\Program Files\BraveSoftware")]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\Microsoft VS Code\Code.exe", @"C:\Users\u\AppData\Local\Programs\Microsoft VS Code")]
    [InlineData(@"C:\Windows\explorer.exe", @"C:\Windows")]
    public void アプリのフォルダー(string exe, string root) => Assert.Equal(root, WindowList.AppRoot(exe));

    [Fact]
    public void 親がウィンドウを持たなければ子のアプリとして表示する()
    {
        var windows = new List<WindowInfo> { W(200, "Teams", "会議", 1200, 800) };
        var parents = new Dictionary<int, int> { [200] = 100 }; // 親 (100) はウィンドウを持たない
        var app = Assert.Single(WindowList.GroupByApp(windows, parents));
        Assert.Equal(200, app.ProcessId);
    }
}
