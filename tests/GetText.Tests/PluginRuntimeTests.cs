using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using GetText.Plugins;

namespace GetText.Tests;

/// <summary>PluginRuntime は static (コマンドの登録先も共有) なので、ほかのテストと並べて動かさない。</summary>
[CollectionDefinition("PluginRuntime", DisableParallelization = true)]
public sealed class PluginRuntimeCollection;

/// <summary>
/// 本物の拡張機能 (見本・わざと壊れる拡張機能) をビルドしたものをパッケージにして入れ、読み込んで確かめる。
/// 壊れた拡張機能でも GetText が落ちない・登録したものが残らないこと。
/// </summary>
[Collection("PluginRuntime")]
public class PluginRuntimeTests : IDisposable
{
    private const string SampleId = "gettext.sample";
    private const string FaultyId = "gettext.test.faulty";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gettext-plugin-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _previousLog = PluginLog.Path;
    private readonly FakeUi _ui = new();

    public PluginRuntimeTests()
    {
        Directory.CreateDirectory(_dir);
        _root = Path.Combine(_dir, "plugins");
        PluginLog.Path = Path.Combine(_dir, "plugins.log");
    }

    public void Dispose()
    {
        PluginRuntime.ShutdownAll();
        foreach (var c in AppCommands.Registry.All.Where(c => c.Id.StartsWith("plugin:", StringComparison.Ordinal)).ToList())
            AppCommands.Registry.Unregister(c.Id);
        AppCommands.Features.RemoveAll(f => f.Id.StartsWith("plugin:", StringComparison.Ordinal));
        PluginLog.Path = _previousLog;
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ───────── 用意 ─────────

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GetText.csproj"))) return d.FullName;
        throw new InvalidOperationException("GetText.csproj が見つかりません (テストの出力先をリポジトリの中にしてください)");
    }

    /// <summary>拡張機能のプロジェクトのビルドの出力から、make_plugins.ps1 と同じ形のパッケージを作る。</summary>
    private string Package(string projectDir, string dll)
    {
        var project = Path.Combine(RepoRoot(), projectDir);
        var bin = new[] { "Debug", "Release" }
            .Select(c => Path.Combine(project, "bin", c, "net10.0"))
            .Where(d => File.Exists(Path.Combine(d, dll)))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, dll)))
            .FirstOrDefault() ?? throw new InvalidOperationException($"{dll} がビルドされていません ({projectDir})");
        var package = Path.Combine(_dir, Path.GetFileName(projectDir) + ".gtplugin");
        using var zip = ZipFile.Open(package, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(Path.Combine(project, "plugin.json"), "plugin.json");
        foreach (var f in Directory.GetFiles(bin))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith("GetText.Plugin.Abstractions", StringComparison.Ordinal) || name.EndsWith(".pdb", StringComparison.Ordinal)
                || name is "plugin.json" or "README.md") continue;
            zip.CreateEntryFromFile(f, "bin/" + name);
        }
        return package;
    }

    private void InstallSample() =>
        new PluginStore(_root).InstallFromFile(Package(@"plugins\GetText.Plugin.Sample", "GetText.Plugin.Sample.dll"), null, PluginTrust.Community, PluginSource.LocalFile);

    private void InstallFaulty(string mode)
    {
        var store = new PluginStore(_root);
        store.InstallFromFile(Package(@"tests\TestPlugins\GetText.Plugin.Faulty", "GetText.Plugin.Faulty.dll"), null, PluginTrust.Community, PluginSource.LocalFile);
        Directory.CreateDirectory(store.DataDirectory(FaultyId));
        File.WriteAllText(Path.Combine(store.DataDirectory(FaultyId), "mode.txt"), mode);
    }

    private void Start(bool safeMode = false)
    {
        PluginRuntime.Prepare(_ui, safeMode, _root);
        PluginRuntime.LoadAll();
    }

    private static LoadedPlugin Plugin(string id) => PluginRuntime.Plugins.Single(p => p.Record.Id == id);

    private static bool Registered(string id) =>
        AppCommands.Registry.All.Any(c => c.Id.StartsWith($"plugin:{id}:", StringComparison.Ordinal))
        || AppCommands.Features.Any(f => f.Id.StartsWith($"plugin:{id}:", StringComparison.Ordinal));

    private static bool WaitFor(Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    private static OcrFrameEventArgs Frame(string text) => new()
    {
        Time = DateTimeOffset.Now,
        Lines = [new OcrTextLine(text, 0, 0, 100, 20)],
        Text = text,
        Region = (0, 0, 100, 20),
    };

    // ───────── 読み込み ─────────

    [Fact]
    public void SampleLoadsAndRegistersCommandFeatureAndSettings()
    {
        InstallSample();
        Start();

        var p = Plugin(SampleId);
        Assert.True(p.Loaded, p.Error);
        Assert.Null(p.Error);
        var command = Assert.Single(AppCommands.Registry.All, c => c.Id == "plugin:gettext.sample:greet");
        Assert.Equal(CommandCategory.Extension, command.Category);
        Assert.Equal("見本: あいさつを出す", command.Title);
        var feature = Assert.Single(AppCommands.Features, f => f.Id == "plugin:gettext.sample:sample");
        Assert.True(feature.Local);
        Assert.Equal(FeatureState.Closed, feature.Status().State);
        Assert.Single(p.Context!.SettingsPages);
        Assert.True(PluginRuntime.HasCapability("sample.greeting"));
        Assert.Contains(_ui.ContributionsChangedCount, n => n > 0);

        // コマンドを実行すると、GetText の知らせが出る
        Assert.True(AppCommands.Registry.TryRun(command.Id));
        Assert.True(WaitFor(() => _ui.Notifications.Count > 0));
        Assert.Equal("こんにちは。拡張機能が動いています。", _ui.Notifications[0].Notification.Message!.For("ja"));
        Assert.Equal(FeatureState.Open, feature.Status().State);

        // 終えた後は、コマンドは使えない
        PluginRuntime.ShutdownAll();
        Assert.False(command.IsAvailable());
    }

    [Fact]
    public void SettingsAreStoredInThePluginDataDirectory()
    {
        InstallSample();
        Start();
        var context = Plugin(SampleId).Context!;
        context.Settings.Set("greeting", "テストの文");
        Assert.StartsWith(Path.Combine(_root, ".data"), context.DataDirectory);
        Assert.True(File.Exists(Path.Combine(context.DataDirectory, "settings.json")));

        // 次の起動でも残る
        PluginRuntime.ShutdownAll();
        Start();
        Assert.Equal("テストの文", Plugin(SampleId).Context!.Settings.Get("greeting"));
    }

    [Fact]
    public void SafeModeLoadsNothing()
    {
        InstallSample();
        Start(safeMode: true);

        Assert.True(PluginRuntime.SafeMode);
        Assert.False(Plugin(SampleId).Loaded);
        Assert.False(Registered(SampleId));
    }

    [Fact]
    public void DisabledPluginIsNotLoaded()
    {
        InstallSample();
        var store = new PluginStore(_root);
        store.Load();
        store.SetEnabled(SampleId, false);
        Start();

        Assert.False(Plugin(SampleId).Loaded);
        Assert.False(Registered(SampleId));
    }

    // ───────── 壊れた拡張機能 ─────────

    [Fact]
    public void InitializeFailureRollsBackAndKeepsOtherPlugins()
    {
        InstallSample();
        InstallFaulty("throw-init");
        Start();

        var faulty = Plugin(FaultyId);
        Assert.False(faulty.Loaded);
        Assert.True(faulty.Error?.Contains("initialize failed") == true, faulty.Error);
        Assert.False(Registered(FaultyId));
        Assert.True(Plugin(SampleId).Loaded);
        Assert.True(Registered(SampleId));

        // 問題は記録され、次の起動でも見える
        var store = new PluginStore(_root);
        store.Load();
        Assert.Contains("initialize failed", store.Find(FaultyId)!.LastError);
    }

    [Fact]
    public void EmojiCommandTitleIsRejected()
    {
        InstallFaulty("emoji-command");
        Start();

        var faulty = Plugin(FaultyId);
        Assert.False(faulty.Loaded);
        Assert.True(faulty.Error?.Contains("絵文字") == true, faulty.Error);
        Assert.False(Registered(FaultyId));
    }

    [Fact]
    public void CrashWhileLoadingDisablesThePluginOnNextStart()
    {
        InstallFaulty("ok");
        // 読み込みの途中で落ちた (読み込み中の印が残った)
        var store = new PluginStore(_root);
        store.Load();
        store.BeginLoading(FaultyId);

        Start();

        Assert.Equal(FaultyId, PluginRuntime.CrashedPlugin);
        Assert.False(Plugin(FaultyId).Record.Enabled);
        Assert.False(Plugin(FaultyId).Loaded);
        Assert.False(Registered(FaultyId));
    }

    [Fact]
    public void ThrowingCommandAndStatusDoNotEscape()
    {
        InstallFaulty("throw-command");
        Start();
        Assert.True(AppCommands.Registry.TryRun("plugin:gettext.test.faulty:run"));
        Assert.True(WaitFor(() => _ui.Notifications.Any(n => n.Notification.Kind == PluginNotificationKind.Error)));

        PluginRuntime.ShutdownAll();
        File.WriteAllText(Path.Combine(new PluginStore(_root).DataDirectory(FaultyId), "mode.txt"), "throw-status");
        Start();
        var feature = Assert.Single(AppCommands.Features, f => f.Id == "plugin:gettext.test.faulty:tile");
        Assert.Equal(FeatureState.Closed, feature.Status().State);
    }

    [Theory]
    [InlineData("throw-shutdown")]
    [InlineData("hang-shutdown")]
    public void ShutdownProblemsDoNotBlockExit(string mode)
    {
        InstallFaulty(mode);
        Start();
        Assert.True(Plugin(FaultyId).Loaded);

        var sw = Stopwatch.StartNew();
        PluginRuntime.ShutdownAll();
        // 終えるのは 2 秒で待つのをやめる (止まった拡張機能で GetText の終了が止まらない)
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"{sw.Elapsed}");
    }

    // ───────── 読み取りの結果を渡す ─────────

    [Fact]
    public void OcrFramesAreDeliveredOffTheCallerThread()
    {
        InstallFaulty("ok");
        Start();
        Assert.True(PluginRuntime.HasOcrSubscribers);

        PluginRuntime.PublishOcrFrame(Frame("読み取った文字"));
        Assert.True(WaitFor(() => _ui.Notifications.Any(n => n.Notification.Title.For("ja") == "ocr")));

        PluginRuntime.ShutdownAll();
        Assert.False(PluginRuntime.HasOcrSubscribers);
    }

    [Fact]
    public void SlowOcrHandlerDoesNotSlowDownReadingAndFramesAreDropped()
    {
        // 遅い拡張機能 (1 回 0.3 秒): 読み取りの側は待たされず、処理中の分は飛ばされる (溜め込まない)
        InstallFaulty("slow-ocr");
        Start();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) PluginRuntime.PublishOcrFrame(Frame("frame " + i));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"読み取りの側が {sw.Elapsed} 待たされた");
        Thread.Sleep(1000);
        int handled = _ui.Notifications.Count(n => n.Notification.Title.For("ja") == "ocr");
        Assert.InRange(handled, 1, 5); // (200 回のうち、処理できた分だけ)
        // 処理が終われば、また受け取れる
        PluginRuntime.PublishOcrFrame(Frame("after"));
        Assert.True(WaitFor(() => _ui.Notifications.Count(n => n.Notification.Title.For("ja") == "ocr") > handled));
    }

    [Fact]
    public void ThrowingOcrHandlerIsLoggedWithoutTheText()
    {
        InstallFaulty("throw-ocr");
        Start();

        const string secret = "秘密の読み取りの文 4111-1111";
        PluginRuntime.PublishOcrFrame(Frame(secret));
        Assert.True(WaitFor(() => File.Exists(PluginLog.Path) && File.ReadAllText(PluginLog.Path).Contains("ocr handler failed")));
        Assert.DoesNotContain(secret, File.ReadAllText(PluginLog.Path));

        // 例外の後も次の分を受け取れる (処理中の印が戻っている)
        File.WriteAllText(PluginLog.Path, "");
        Assert.True(WaitFor(() =>
        {
            PluginRuntime.PublishOcrFrame(Frame(secret));
            Thread.Sleep(50);
            return File.ReadAllText(PluginLog.Path).Contains("ocr handler failed");
        }));
    }

    [Fact]
    public void KeywordMonitorNotifiesThroughTheHost()
    {
        // 公式の拡張機能を、配布と同じパッケージにして入れ、読み取りの結果を渡すと GetText の知らせが出る
        var store = new PluginStore(_root);
        store.InstallFromFile(Package(@"plugins\GetText.Plugin.KeywordMonitor", "GetText.Plugin.KeywordMonitor.dll"), null, PluginTrust.Community, PluginSource.LocalFile);
        Directory.CreateDirectory(store.DataDirectory("gettext.keyword-monitor"));
        File.WriteAllText(Path.Combine(store.DataDirectory("gettext.keyword-monitor"), "settings.json"),
            """{"words":"至急\n締め切り","active":"true","sound":"false"}""");
        Start();

        Assert.True(Plugin("gettext.keyword-monitor").Loaded, Plugin("gettext.keyword-monitor").Error);
        var feature = Assert.Single(AppCommands.Features, f => f.Id == "plugin:gettext.keyword-monitor:monitor");
        Assert.Equal(FeatureState.Active, feature.Status().State);
        // 読み取りの画面が読んだ文字を使うので、ホームのタイルではなく読み取りの画面の「表示」メニューに出す
        Assert.True(feature.InReadingWindow);
        Assert.True(PluginRuntime.HasOcrSubscribers);

        PluginRuntime.PublishOcrFrame(Frame("至急 ご確認ください"));
        Assert.True(WaitFor(() => _ui.Notifications.Any(n => n.Notification.Title.For("ja") == "「至急」が出ました")));
        Assert.DoesNotContain("至急", File.Exists(PluginLog.Path) ? File.ReadAllText(PluginLog.Path) : "");

        // 止めると読み取りの結果を受け取らない (GetText は結果を作らなくてよい)
        Assert.True(AppCommands.Registry.TryRun("plugin:gettext.keyword-monitor:toggle"));
        Assert.True(WaitFor(() => !PluginRuntime.HasOcrSubscribers));
        Assert.Equal(FeatureState.Closed, feature.Status().State);
    }

    [Fact]
    public async Task HistoryIsOffByDefaultAndSearchableWhenOn()
    {
        var store = new PluginStore(_root);
        store.InstallFromFile(Package(@"plugins\GetText.Plugin.History", "GetText.Plugin.History.dll"), null, PluginTrust.Community, PluginSource.LocalFile);
        Start();
        Assert.True(Plugin("gettext.history").Loaded, Plugin("gettext.history").Error);

        // 既定は保存しない: 読み取りの結果も受け取らない
        Assert.False(PluginRuntime.HasOcrSubscribers);
        Assert.Empty(await PluginSearch.SearchAsync("予算", 10, CancellationToken.None));

        // 始めると保存し、コマンドの一覧の検索で見つかる (押すとその回の文字をコピー)
        Assert.True(AppCommands.Registry.TryRun("plugin:gettext.history:toggle"));
        Assert.True(WaitFor(() => PluginRuntime.HasOcrSubscribers));
        PluginRuntime.PublishOcrFrame(Frame("来年度の予算の見直し"));
        List<AppCommand> found = [];
        Assert.True(WaitFor(() =>
        {
            found = PluginSearch.SearchAsync("予算", 10, CancellationToken.None, copied: text => _ui.ShowNotification("copied", new(text))).GetAwaiter().GetResult();
            return found.Count == 1;
        }));
        Assert.Equal(CommandCategory.SearchResult, found[0].Category);
        Assert.Equal("来年度の予算の見直し", found[0].Title);
        found[0].Execute();
        Assert.Contains(_ui.Notifications, n => n.Plugin == "copied" && n.Notification.Title.For("ja") == "来年度の予算の見直し");

        // 文字はログに書かない
        Assert.DoesNotContain("予算", File.Exists(PluginLog.Path) ? File.ReadAllText(PluginLog.Path) : "");
        var data = Plugin("gettext.history").Context!.DataDirectory;
        Assert.Single(Directory.GetFiles(Path.Combine(data, "history"), "*.jsonl"));
        Assert.True(Assert.Single(AppCommands.Features, f => f.Id == "plugin:gettext.history:history").InReadingWindow);

        // 続けて変わった後に止まった画面 (前の保存から間もない最後の回) も、間隔があいたら保存する
        PluginRuntime.PublishOcrFrame(Frame("変わった直後の画面"));
        Thread.Sleep(300); // (前の回を処理している間に届いた回は、GetText が飛ばす決まり)
        PluginRuntime.PublishOcrFrame(Frame("止まった最後の画面の文字"));
        Assert.True(WaitFor(() => PluginSearch.SearchAsync("止まった最後", 10, CancellationToken.None).GetAwaiter().GetResult().Count == 1, ms: 8000));
    }

    [Fact]
    public void DiagnosticsDoNotContainUserName()
    {
        InstallFaulty("throw-init");
        Start();
        var text = PluginRuntime.Diagnostics(Plugin(FaultyId));
        Assert.Contains(FaultyId, text);
        Assert.Contains("Plugin API v1", text);
        if (Environment.UserName.Length >= 2) Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeUi : IPluginHostUi
    {
        private readonly object _lock = new();
        private readonly List<(string Plugin, PluginNotification Notification)> _notifications = [];

        public List<(string Plugin, PluginNotification Notification)> Notifications
        {
            get { lock (_lock) return [.. _notifications]; }
        }

        public List<int> ContributionsChangedCount { get; } = [0];

        public void ShowNotification(string pluginName, PluginNotification notification)
        {
            lock (_lock) _notifications.Add((pluginName, notification));
        }

        public void ContributionsChanged() => ContributionsChangedCount[0]++;

        public void Post(Action action) => action();

        public Task<IReadOnlyList<string>> PickFilesAsync(LocalizedText title, IReadOnlyList<(string Name, string[] Extensions)> filters, bool multiple,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickFolderAsync(LocalizedText title, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(LocalizedText title, string suggestedName, IReadOnlyList<(string Name, string[] Extensions)> filters,
            CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<int?> ChooseAsync(LocalizedText title, LocalizedText? message, IReadOnlyList<LocalizedText> options, CancellationToken cancellationToken) =>
            Task.FromResult<int?>(null);

        public Task<CapturedImage?> SelectScreenRegionAsync(LocalizedText title, CancellationToken cancellationToken) => Task.FromResult<CapturedImage?>(null);

        public IPluginProgress BeginProgress(LocalizedText title) => throw new NotSupportedException();

        public void Reveal(string path) { }

        public Task<bool> CopyTextAsync(string text) => Task.FromResult(true);

        public void OpenUrl(Uri url) { }
    }
}
