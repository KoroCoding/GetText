using System.IO;
using System.IO.Compression;
using System.Text;

namespace GetText.Tests;

/// <summary>拡張機能の一覧 (入れたもの・見つける・更新) の状態と操作。通信はしない (同梱の索引だけ)。</summary>
public class PluginCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gettext-plugin-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _bundled;
    private readonly List<LoadedPlugin> _loaded = [];

    public PluginCatalogTests()
    {
        _root = Path.Combine(_dir, "plugins");
        _bundled = Path.Combine(_dir, "bundled-plugins");
        Directory.CreateDirectory(_bundled);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Manifest(string id, string version, string minHost = "1.0.0", string permissions = "[\"notifications\"]", string publisher = "KoroCoding") => $$"""
        {
          "id": "{{id}}", "name": {"ja": "{{id}} の名前", "en": "{{id}}"}, "version": "{{version}}", "publisher": "{{publisher}}",
          "description": "説明 {{id}}", "apiVersion": 1, "minHostVersion": "{{minHost}}", "platforms": ["any"],
          "capabilities": [], "permissions": {{permissions}}, "dependencies": [], "optionalDependencies": [],
          "entryPoint": { "assembly": "bin/Plugin.dll", "type": "X.Plugin" }, "requiresRestart": true
        }
        """;

    /// <summary>同梱のパッケージを作り、索引に載せる。</summary>
    private void Bundle(params (string Id, string Version, string MinHost, string Permissions, bool BadSha)[] plugins)
    {
        var items = new List<string>();
        foreach (var p in plugins)
        {
            var name = $"{p.Id}-{p.Version}.gtplugin";
            var path = Path.Combine(_bundled, name);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("plugin.json").Open(), new UTF8Encoding(false)))
                    w.Write(Manifest(p.Id, p.Version, p.MinHost, p.Permissions));
                using var dll = zip.CreateEntry("bin/Plugin.dll").Open();
                dll.Write([1, 2, 3]);
            }
            var sha = p.BadSha ? new string('0', 64) : PluginPackages.Sha256(path);
            items.Add($$"""
                { "id": "{{p.Id}}", "name": {"ja": "{{p.Id}} の名前"}, "publisher": "KoroCoding", "description": "説明",
                  "version": "{{p.Version}}", "file": "{{name}}", "sha256": "{{sha}}", "size": {{new FileInfo(path).Length}},
                  "minHostVersion": "{{p.MinHost}}", "apiVersion": 1, "platforms": ["any"], "permissions": {{p.Permissions}}, "verified": true }
                """);
        }
        File.WriteAllText(Path.Combine(_bundled, "plugins-index.json"), $$"""{ "schema": 1, "plugins": [{{string.Join(",", items)}}] }""");
    }

    private PluginCatalog Catalog(bool safeMode = false)
    {
        var c = new PluginCatalog(new PluginStore(_root), () => _loaded, _bundled, safeMode);
        c.LoadLocal();
        return c;
    }

    /// <summary>起動したときに読み込んだことにする。</summary>
    private void MarkLoaded(PluginCatalog c, string id, bool loaded = true, string? error = null)
    {
        var record = c.Store.Find(id)!;
        _loaded.Add(new LoadedPlugin { Record = record, Loaded = loaded, Error = error });
    }

    [Fact]
    public void AvailableListsBundledAndFlagsIncompatible()
    {
        Bundle(("gettext.alpha", "1.0.0", "1.0.0", "[\"notifications\"]", false),
               ("gettext.future", "1.0.0", "99.0.0", "[]", false));
        var c = Catalog();

        var available = c.Available();
        Assert.Equal(["gettext.alpha", "gettext.future"], available.Select(i => i.Id));
        Assert.Equal(PluginItemState.Available, available[0].State);
        Assert.Equal(PluginTrust.Official, available[0].Trust);
        Assert.Equal(PluginItemState.Incompatible, available[1].State);
        Assert.Contains("99.0.0", available[1].Detail);
        Assert.Empty(c.Installed());
        Assert.False(c.CheckedOnline); // 開いただけでは通信しない
    }

    [Fact]
    public async Task InstallingBundledNeedsRestartAndIsOfficial()
    {
        Bundle(("gettext.alpha", "1.0.0", "1.0.0", "[\"notifications\"]", false));
        var c = Catalog();

        await c.InstallAsync(c.Available()[0].Entry!, null, null, CancellationToken.None);

        var item = Assert.Single(c.Installed());
        Assert.Equal(PluginItemState.RestartRequired, item.State);
        Assert.Equal("再起動すると使えます", item.Detail);
        Assert.Equal(PluginTrust.Official, item.Trust);
        Assert.Equal("公式", item.TrustLabel);
        Assert.Empty(c.Available());
        Assert.True(c.RestartRequired);
    }

    [Fact]
    public async Task NewStoreInstanceDoesNotDropOtherRecords()
    {
        // 別の PluginStore (読み込む前) で入れたり止めたりしても、ほかの拡張機能の記録は消えない
        Bundle(("gettext.alpha", "1.0.0", "1.0.0", "[]", false), ("gettext.beta", "1.0.0", "1.0.0", "[]", false));
        var c = Catalog();
        await c.InstallAsync(c.Index.First(e => e.Id == "gettext.alpha"), null, null, CancellationToken.None);
        var other = new PluginCatalog(new PluginStore(_root), () => _loaded, _bundled, false);
        await other.InstallAsync(c.Index.First(e => e.Id == "gettext.beta"), null, null, CancellationToken.None);
        new PluginStore(_root).SetEnabled("gettext.alpha", false);

        Assert.Equal(["gettext.alpha", "gettext.beta"], Catalog().Installed().Select(i => i.Id).Order());
        Assert.False(Catalog().Store.Find("gettext.alpha")!.Enabled);
    }

    [Fact]
    public async Task UninstallRemovesFilesAndDataOnRestart()
    {
        Bundle(("gettext.alpha", "1.0.0", "1.0.0", "[]", false));
        var c = Catalog();
        await c.InstallAsync(c.Available()[0].Entry!, null, null, CancellationToken.None);
        c.Store.ApplyPending();
        var data = c.Store.DataDirectory("gettext.alpha");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), "{}");

        c.Uninstall("gettext.alpha");
        Assert.True(Directory.Exists(data)); // 再起動までは消さない
        var next = new PluginStore(_root);
        next.ApplyPending();

        Assert.Empty(next.Records);
        Assert.False(Directory.Exists(data));
        Assert.False(Directory.Exists(Path.Combine(_root, "gettext.alpha")));
    }

    private string PackageFor(string version) => Path.Combine(_bundled, $"gettext.alpha-{version}.gtplugin");

    [Fact]
    public void FailedLoadOfNewVersionRollsBackOnNextStart()
    {
        Bundle(("gettext.alpha", "1.0.0", "1.0.0", "[]", false));
        Bundle(("gettext.alpha", "1.1.0", "1.0.0", "[]", false));
        var store = new PluginStore(_root);
        store.InstallFromFile(PackageFor("1.0.0"), null, PluginTrust.Community, PluginSource.LocalFile);
        store.ApplyPending();
        store.InstallFromFile(PackageFor("1.1.0"), null, PluginTrust.Community, PluginSource.LocalFile);
        store.ApplyPending(); // 次の起動: 1.1.0 に切り替え、1.0.0 は前の版として残る
        Assert.Equal(("1.1.0", "1.0.0"), (store.Find("gettext.alpha")!.Version, store.Find("gettext.alpha")!.PreviousVersion));

        // 新しい版を読み込めなかった (落ちたのではなく、例外などで): 前の版は消さず、次の起動で戻す
        store.BeginLoading("gettext.alpha");
        store.SetError("gettext.alpha", "読み込めませんでした: X");
        store.EndLoading("gettext.alpha", loaded: false);
        Assert.True(Directory.Exists(store.VersionDirectory("gettext.alpha", "1.0.0")));
        var c = new PluginCatalog(store, () => [], _bundled, false);
        Assert.Contains("前の版 1.0.0 に戻します", c.Installed()[0].Detail);

        var next = new PluginStore(_root);
        next.ApplyPending();
        Assert.Equal("1.0.0", next.Find("gettext.alpha")!.Version);
        next.EndLoading("gettext.alpha", loaded: true); // 前の版を読み込めた → 失敗した新しい版を消す
        Assert.False(Directory.Exists(next.VersionDirectory("gettext.alpha", "1.1.0")));
        Assert.True(Directory.Exists(next.VersionDirectory("gettext.alpha", "1.0.0")));
    }

    [Fact]
    public void ReinstallingSameVersionAfterUninstallKeepsTheFolder()
    {
        Bundle(("gettext.alpha", "1.0.0", "1.0.0", "[]", false));
        var store = new PluginStore(_root);
        store.InstallFromFile(PackageFor("1.0.0"), null, PluginTrust.Community, PluginSource.LocalFile);
        store.ApplyPending();
        store.Uninstall("gettext.alpha");
        store.InstallFromFile(PackageFor("1.0.0"), null, PluginTrust.Community, PluginSource.LocalFile);

        var r = store.Find("gettext.alpha")!;
        Assert.False(r.PendingRemoval);
        Assert.Null(r.PendingVersion);
        var dir = store.VersionDirectory("gettext.alpha", "1.0.0");
        Assert.True(File.Exists(Path.Combine(dir, "plugin.json")));
        var next = new PluginStore(_root);
        next.ApplyPending();
        next.EndLoading("gettext.alpha", loaded: true);
        Assert.True(File.Exists(Path.Combine(dir, "plugin.json"))); // 今の版を「前の版」として消さない
        Assert.Single(next.ReadInstalled(), x => x.Manifest != null);
    }

    [Fact]
    public void ReplacingAPendingUpdateRemovesTheOlderPendingFolder()
    {
        Bundle(("gettext.alpha", "1.0.0", "1.0.0", "[]", false));
        Bundle(("gettext.alpha", "1.1.0", "1.0.0", "[]", false));
        Bundle(("gettext.alpha", "1.2.0", "1.0.0", "[]", false));
        var store = new PluginStore(_root);
        store.InstallFromFile(PackageFor("1.0.0"), null, PluginTrust.Community, PluginSource.LocalFile);
        store.ApplyPending();
        store.InstallFromFile(PackageFor("1.1.0"), null, PluginTrust.Community, PluginSource.LocalFile);
        store.InstallFromFile(PackageFor("1.2.0"), null, PluginTrust.Community, PluginSource.LocalFile);
        Assert.Equal("1.2.0", store.Find("gettext.alpha")!.PendingVersion);
        Assert.False(Directory.Exists(store.VersionDirectory("gettext.alpha", "1.1.0")));
        Assert.True(Directory.Exists(store.VersionDirectory("gettext.alpha", "1.0.0")));
    }

    [Fact]
    public async Task WrongShaIsRejected()
    {
        Bundle(("gettext.alpha", "1.0.0", "1.0.0", "[]", true));
        var c = Catalog();
        await Assert.ThrowsAsync<PluginPackageException>(() => c.InstallAsync(c.Available()[0].Entry!, null, null, CancellationToken.None));
        Assert.Empty(c.Installed());
    }

    [Fact]
    public async Task UpdatesShowNewerIndexVersionAndPendAfterInstall()
    {
        Bundle(("gettext.alpha", "1.0.0", "1.0.0", "[]", false));
        var c = Catalog();
        await c.InstallAsync(c.Available()[0].Entry!, null, null, CancellationToken.None);
        c.Store.ApplyPending();
        MarkLoaded(c, "gettext.alpha");

        // 新しい版が索引に載った
        foreach (var f in Directory.GetFiles(_bundled)) File.Delete(f);
        Bundle(("gettext.alpha", "1.2.0", "1.0.0", "[]", false));
        c = Catalog();
        Assert.Equal(PluginItemState.Running, c.Installed()[0].State);
        var update = Assert.Single(c.Updates());
        Assert.Equal("1.2.0", update.UpdateVersion);

        await c.InstallAsync(update.Entry!, null, null, CancellationToken.None);
        var pending = Assert.Single(c.Installed());
        Assert.Equal(PluginItemState.RestartRequired, pending.State);
        Assert.Contains("1.2.0", pending.Detail);
        Assert.Empty(c.Updates());
    }

    [Fact]
    public async Task DisableUninstallAndCancelNeedRestart()
    {
        Bundle(("gettext.alpha", "1.0.0", "1.0.0", "[]", false));
        var c = Catalog();
        await c.InstallAsync(c.Available()[0].Entry!, null, null, CancellationToken.None);
        c.Store.ApplyPending();
        MarkLoaded(c, "gettext.alpha");
        Assert.False(c.RestartRequired);

        c.SetEnabled("gettext.alpha", false);
        Assert.Equal("再起動すると止まります", c.Installed()[0].Detail);
        c.SetEnabled("gettext.alpha", true);
        Assert.Equal(PluginItemState.Running, c.Installed()[0].State);

        c.Uninstall("gettext.alpha");
        Assert.Equal("再起動すると消えます", c.Installed()[0].Detail);
        Assert.True(c.Installed()[0].PendingRemoval);
        c.CancelUninstall("gettext.alpha");
        Assert.Equal(PluginItemState.Running, c.Installed()[0].State);
    }

    [Fact]
    public async Task ErrorAndSafeModeStates()
    {
        Bundle(("gettext.alpha", "1.0.0", "1.0.0", "[]", false));
        var c = Catalog();
        await c.InstallAsync(c.Available()[0].Entry!, null, null, CancellationToken.None);
        c.Store.ApplyPending();
        MarkLoaded(c, "gettext.alpha", loaded: false, error: "読み込めませんでした: X");
        Assert.Equal(PluginItemState.Error, c.Installed()[0].State);
        Assert.Equal("読み込めませんでした: X", c.Installed()[0].Detail);

        _loaded.Clear();
        MarkLoaded(c, "gettext.alpha", loaded: false);
        Assert.Equal(PluginItemState.SafeMode, Catalog(safeMode: true).Installed()[0].State);
    }

    [Fact]
    public void FilterMatchesNamePublisherAndPermissionLabels()
    {
        Bundle(("gettext.alpha", "1.0.0", "1.0.0", "[\"network\"]", false),
               ("gettext.beta", "1.0.0", "1.0.0", "[\"clipboard\"]", false));
        var items = Catalog().Available();

        Assert.Equal(["gettext.alpha"], PluginCatalog.Filter(items, "インターネット").Select(i => i.Id));
        Assert.Equal(["gettext.beta"], PluginCatalog.Filter(items, "beta の名前").Select(i => i.Id));
        Assert.Equal(2, PluginCatalog.Filter(items, "").Count);
        Assert.False(items[0].Local); // network を要求するものは「オンライン」
    }

    /// <summary>続き (Post) を実行しない同期の文脈 (止まっている UI のスレッドと同じ)。</summary>
    private sealed class NeverRunsContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
    }

    [Theory]
    [InlineData("installed")]
    [InlineData("updates")]
    [InlineData("discover")]
    public void DemoCatalogDoesNotWaitOnTheUiThread(string scenario)
    {
        // 画面の画像 (CI) は見本を UI のスレッドで作る。非同期の処理の続きを UI のスレッドで待つと止まる (以前の不具合)
        var done = new ManualResetEventSlim();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NeverRunsContext());
            try
            {
                var catalog = PluginDemo.Create(Path.Combine(_dir, "demo-" + scenario), scenario);
                Assert.NotEmpty(scenario == "discover" ? catalog.Available() : catalog.Installed());
            }
            catch (Exception ex)
            {
                error = ex;
            }
            done.Set();
        }) { IsBackground = true };
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "見本づくりが、UI のスレッドで続きを待って止まりました");
        Assert.Null(error);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(500, "1 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024 / 2, "1.5 GB")]
    public void FormatsSizes(long bytes, string text) => Assert.Equal(text, PluginCatalog.FormatSize(bytes));
}
