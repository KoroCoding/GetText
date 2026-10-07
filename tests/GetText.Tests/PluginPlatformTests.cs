using System.IO;
using System.IO.Compression;
using System.Text;
using GetText.Plugins;

namespace GetText.Tests;

/// <summary>拡張機能のマニフェスト・互換性・依存関係・パッケージの安全な展開・入れる / 更新 / 戻す / 消す。</summary>
public class PluginPlatformTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gettext-plugin-tests-" + Guid.NewGuid().ToString("N"));

    public PluginPlatformTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Manifest(string id = "gettext.sample", string version = "1.0.0", string api = "1", string minHost = "1.0.0",
        string platforms = "[\"any\"]", string extra = "", string deps = "[]", string permissions = "[\"notifications\"]") => $$"""
        {
          "id": "{{id}}", "name": {"ja": "見本", "en": "Sample"}, "version": "{{version}}", "publisher": "KoroCoding",
          "description": "見本の拡張機能", "apiVersion": {{api}}, "minHostVersion": "{{minHost}}", "platforms": {{platforms}},
          "capabilities": [], "permissions": {{permissions}}, "dependencies": {{deps}}, "optionalDependencies": [],
          "requiresRestart": true {{extra}}
        }
        """;

    private static readonly SemVersion Host = new(1, 1, 0);

    // ───────── マニフェスト ─────────

    [Fact]
    public void ParsesValidManifestWithLocalizedText()
    {
        var m = PluginManifests.Parse(Manifest(extra: ", \"futureField\": 1"), out var error);
        Assert.Null(error);
        Assert.NotNull(m);
        Assert.Equal("見本", m!.Name.For("ja"));
        Assert.Equal("Sample", m.Name.For("en"));
        Assert.Equal("見本", m.Name.For("fr")); // 無い言語は ja
        Assert.Equal(new SemVersion(1, 0, 0), m.Version);
        Assert.Equal(["futureField"], m.UnknownFields); // 知らない項目は読み込みを止めず、診断に出す
        Assert.Null(m.EntryPoint);
    }

    [Theory]
    [InlineData("{ not json", "JSON")]
    [InlineData("[]", "JSON")]
    [InlineData("{\"id\": \"gettext.x\"}", "version")]
    public void RejectsBrokenManifest(string json, string hint)
    {
        Assert.Null(PluginManifests.Parse(json, out var error));
        Assert.Contains(hint, error);
    }

    [Theory]
    [InlineData("Upper.Case")]
    [InlineData("noprefix")]
    [InlineData("../evil")]
    [InlineData("a..b")]
    [InlineData("gettext.with space")]
    public void RejectsInvalidIds(string id)
    {
        Assert.False(PluginManifests.IsValidId(id));
        Assert.Null(PluginManifests.Parse(Manifest(id: id), out _));
    }

    [Fact]
    public void RejectsBadVersionsPlatformsPermissionsAndEntryPoints()
    {
        Assert.Null(PluginManifests.Parse(Manifest(version: "1.0"), out _));
        Assert.Null(PluginManifests.Parse(Manifest(minHost: "x"), out _));
        Assert.Null(PluginManifests.Parse(Manifest(api: "\"one\""), out _));
        Assert.Null(PluginManifests.Parse(Manifest(platforms: "[]"), out _));
        Assert.Null(PluginManifests.Parse(Manifest(platforms: "[\"amiga\"]"), out _));
        Assert.Null(PluginManifests.Parse(Manifest(permissions: "[\"root\"]"), out var why));
        Assert.Contains("root", why);
        Assert.Null(PluginManifests.Parse(Manifest(extra: ", \"entryPoint\": {\"assembly\": \"../x.dll\", \"type\": \"T\"}"), out _));
        Assert.Null(PluginManifests.Parse(Manifest(extra: ", \"entryPoint\": {\"assembly\": \"C:/x.dll\", \"type\": \"T\"}"), out _));
        Assert.Null(PluginManifests.Parse(Manifest(extra: ", \"entryPoint\": {\"assembly\": \"bin/x.exe\", \"type\": \"T\"}"), out _));
        Assert.NotNull(PluginManifests.Parse(Manifest(extra: ", \"entryPoint\": {\"assembly\": \"bin/x.dll\", \"type\": \"T\"}"), out _));
    }

    [Fact]
    public void VersionsAndRanges()
    {
        Assert.True(SemVersion.TryParse("1.10.0", out var a));
        Assert.True(SemVersion.TryParse("1.9.3", out var b));
        Assert.True(a > b);
        Assert.True(SemVersion.TryParse("2.0.0-beta.1", out var pre));
        Assert.True(pre < new SemVersion(2, 0, 0));
        Assert.True(VersionRange.TryParse(">=1.2.0", out var range));
        Assert.True(range.Allows(new SemVersion(1, 2, 0)));
        Assert.False(range.Allows(new SemVersion(1, 1, 9)));
        Assert.True(VersionRange.TryParse("1.0.0", out var exact));
        Assert.False(exact.Allows(new SemVersion(1, 0, 1)));
        Assert.False(VersionRange.TryParse("~1", out _));
    }

    // ───────── 互換性 ─────────

    [Fact]
    public void CompatibilityExplainsWhy()
    {
        var m = PluginManifests.Parse(Manifest(minHost: "1.2.0"), out _)!;
        Assert.Contains("1.2.0 以上", PluginCompatibility.Check(m, Host, "win-x64"));
        var newer = PluginManifests.Parse(Manifest(api: "2"), out _)!;
        Assert.Contains("新しい Plugin API", PluginCompatibility.Check(newer, Host, "win-x64"));
        var mac = PluginManifests.Parse(Manifest(platforms: "[\"osx-arm64\"]"), out _)!;
        Assert.Contains("win-x64", PluginCompatibility.Check(mac, Host, "win-x64"));
        Assert.Null(PluginCompatibility.Check(mac, Host, "osx-arm64"));
        Assert.Null(PluginCompatibility.Check(PluginManifests.Parse(Manifest(), out _)!, Host, "osx-x64"));
    }

    // ───────── 依存関係 ─────────

    private static PluginManifest P(string id, string deps = "[]", string version = "1.0.0") => PluginManifests.Parse(Manifest(id: id, deps: deps, version: version), out _)!;

    [Fact]
    public void DependenciesAreOrderedAndProblemsExplained()
    {
        var r = PluginDependencies.Resolve([
            P("gettext.b", "[{\"id\": \"gettext.a\", \"version\": \">=1.0.0\"}]"),
            P("gettext.a"),
            P("gettext.c", "[{\"id\": \"gettext.missing\"}]"),
            P("gettext.d", "[{\"id\": \"gettext.c\"}]"),
            P("gettext.e", "[{\"id\": \"gettext.a\", \"version\": \">=2.0.0\"}]"),
        ]);
        var order = r.Order.Select(m => m.Id).ToList();
        Assert.True(order.IndexOf("gettext.a") < order.IndexOf("gettext.b"));
        Assert.DoesNotContain("gettext.c", order);
        Assert.Contains("入っていません", r.Problems["gettext.c"]);
        Assert.DoesNotContain("gettext.d", order); // 依存が使えないものに依存
        Assert.Contains("使えません", r.Problems["gettext.d"]);
        Assert.Contains("条件", r.Problems["gettext.e"]);
    }

    [Fact]
    public void CyclesAndDuplicatesAreRejectedWithoutBlockingOthers()
    {
        var r = PluginDependencies.Resolve([
            P("gettext.x", "[{\"id\": \"gettext.y\"}]"),
            P("gettext.y", "[{\"id\": \"gettext.x\"}]"),
            P("gettext.ok"),
            P("gettext.dup"),
            P("gettext.dup", version: "2.0.0"),
        ]);
        Assert.Equal(["gettext.ok"], r.Order.Select(m => m.Id));
        Assert.Contains("循環", r.Problems["gettext.x"]);
        Assert.Contains("循環", r.Problems["gettext.y"]);
        Assert.Contains("2 つ", r.Problems["gettext.dup"]);
    }

    // ───────── パッケージの安全 ─────────

    private string Zip(string name, params (string Path, string Content)[] files)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (p, c) in files)
        {
            var e = zip.CreateEntry(p);
            using var w = new StreamWriter(e.Open());
            w.Write(c);
        }
        return path;
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("bin/../../evil.txt")]
    [InlineData("/abs.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("bin/CON.txt")]
    [InlineData("bin/aux")]
    [InlineData("bin/name.")]
    [InlineData("bin/a:b")]
    public void RejectsUnsafePaths(string entry)
    {
        Assert.False(PluginPackages.IsSafeRelativePath(entry));
        var zip = Zip("bad.gtplugin", ("plugin.json", Manifest()), (entry, "x"));
        var dest = Path.Combine(_dir, "out");
        Assert.Throws<PluginPackageException>(() => PluginPackages.ExtractSafely(zip, dest));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.True(!Directory.Exists(dest) || !Directory.EnumerateFileSystemEntries(dest).Any()); // 何も書かない
    }

    [Fact]
    public void RejectsSymlinksZipBombsAndTooManyFiles()
    {
        // シンボリックリンク (Unix の mode 0120777)
        var link = Path.Combine(_dir, "link.gtplugin");
        using (var zip = ZipFile.Open(link, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("bin/link");
            e.ExternalAttributes = unchecked((int)(0xA1FF0000u));
            using var w = new StreamWriter(e.Open());
            w.Write("/etc/passwd");
        }
        Assert.Contains("シンボリックリンク", Assert.Throws<PluginPackageException>(() => PluginPackages.ExtractSafely(link, Path.Combine(_dir, "o1"))).Message);

        // 圧縮率が極端 (同じ文字の繰り返し)
        var bomb = Zip("bomb.gtplugin", ("big.txt", new string('0', 5_000_000)));
        Assert.Contains("圧縮率", Assert.Throws<PluginPackageException>(() => PluginPackages.ExtractSafely(bomb, Path.Combine(_dir, "o2"))).Message);

        // 展開後の大きさ・数の上限
        var many = Zip("many.gtplugin", Enumerable.Range(0, 30).Select(i => ($"f{i}.txt", "abc")).ToArray());
        Assert.Contains("多すぎ", Assert.Throws<PluginPackageException>(() => PluginPackages.ExtractSafely(many, Path.Combine(_dir, "o3"), new PackageLimits(MaxFiles: 10))).Message);
        var big = Zip("big.gtplugin", ("a.txt", Guid.NewGuid().ToString() + Guid.NewGuid()));
        Assert.Throws<PluginPackageException>(() => PluginPackages.ExtractSafely(big, Path.Combine(_dir, "o4"), new PackageLimits(MaxTotalBytes: 10)));
    }

    [Fact]
    public void RejectsTruncatedAndDuplicateEntries()
    {
        var good = Zip("good.gtplugin", ("plugin.json", Manifest()), ("README.md", "readme"));
        var bytes = File.ReadAllBytes(good);
        var truncated = Path.Combine(_dir, "truncated.gtplugin");
        File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);
        Assert.Throws<PluginPackageException>(() => PluginPackages.ExtractSafely(truncated, Path.Combine(_dir, "o5")));

        var dup = Zip("dup.gtplugin", ("plugin.json", Manifest()), ("Plugin.json", Manifest()));
        Assert.Contains("同じ名前", Assert.Throws<PluginPackageException>(() => PluginPackages.ExtractSafely(dup, Path.Combine(_dir, "o6"))).Message);

        PluginPackages.ExtractSafely(good, Path.Combine(_dir, "o7"));
        Assert.Equal("readme", File.ReadAllText(Path.Combine(_dir, "o7", "README.md")));
    }

    // ───────── 入れる・更新・戻す・消す ─────────

    private PluginStore NewStore()
    {
        var store = new PluginStore(Path.Combine(_dir, "plugins"));
        store.Load();
        return store;
    }

    [Fact]
    public void InstallVerifiesShaAndCompatibility()
    {
        var store = NewStore();
        var pkg = Zip("p1.gtplugin", ("plugin.json", Manifest()));
        Assert.Contains("SHA-256", Assert.Throws<PluginPackageException>(() =>
            store.InstallFromFile(pkg, new string('0', 64), PluginTrust.Official, PluginSource.Index, hostVersion: Host)).Message);
        Assert.Empty(store.Records); // 途中で止めたら何も入れない

        var future = Zip("p2.gtplugin", ("plugin.json", Manifest(id: "gettext.future", minHost: "9.0.0")));
        Assert.Contains("9.0.0 以上", Assert.Throws<PluginPackageException>(() =>
            store.InstallFromFile(future, null, PluginTrust.Community, PluginSource.LocalFile, hostVersion: Host)).Message);

        var noManifest = Zip("p3.gtplugin", ("README.md", "x"));
        Assert.Contains("plugin.json", Assert.Throws<PluginPackageException>(() =>
            store.InstallFromFile(noManifest, null, PluginTrust.Community, PluginSource.LocalFile, hostVersion: Host)).Message);

        var missingDll = Zip("p4.gtplugin", ("plugin.json", Manifest(extra: ", \"entryPoint\": {\"assembly\": \"bin/x.dll\", \"type\": \"T\"}")));
        Assert.Contains("bin/x.dll", Assert.Throws<PluginPackageException>(() =>
            store.InstallFromFile(missingDll, null, PluginTrust.Community, PluginSource.LocalFile, hostVersion: Host)).Message);

        var sha = PluginPackages.Sha256(pkg);
        var m = store.InstallFromFile(pkg, sha, PluginTrust.Official, PluginSource.Index, "gettext.sample", new SemVersion(1, 0, 0), Host);
        Assert.Equal("gettext.sample", m.Id);
        var record = Assert.Single(store.Records);
        Assert.Equal(PluginTrust.Official, record.Trust);
        Assert.True(File.Exists(Path.Combine(store.VersionDirectory("gettext.sample", "1.0.0"), "plugin.json")));
        Assert.False(Directory.Exists(Path.Combine(store.Root, ".staging")) && Directory.EnumerateFileSystemEntries(Path.Combine(store.Root, ".staging")).Any());
        // 同じ版をもう一度
        Assert.Contains("もう入っています", Assert.Throws<PluginPackageException>(() =>
            store.InstallFromFile(pkg, sha, PluginTrust.Official, PluginSource.Index, hostVersion: Host)).Message);
    }

    [Fact]
    public void OfficialTrustRequiresOfficialIdAndVerifiedHash()
    {
        var store = NewStore();
        var pkg = Zip("third.gtplugin", ("plugin.json", Manifest(id: "acme.tool")));
        store.InstallFromFile(pkg, PluginPackages.Sha256(pkg), PluginTrust.Official, PluginSource.Index, hostVersion: Host);
        Assert.Equal(PluginTrust.Community, store.Find("acme.tool")!.Trust); // 公式を名乗れない
        var local = Zip("local.gtplugin", ("plugin.json", Manifest(id: "gettext.local")));
        store.InstallFromFile(local, null, PluginTrust.Official, PluginSource.LocalFile, hostVersion: Host);
        Assert.Equal(PluginTrust.Community, store.Find("gettext.local")!.Trust); // SHA を確かめていない
    }

    [Fact]
    public void UpdateRollbackAndUninstallAcrossRestarts()
    {
        var store = NewStore();
        store.InstallFromFile(Zip("v1.gtplugin", ("plugin.json", Manifest(version: "1.0.0"))), null, PluginTrust.Community, PluginSource.LocalFile, hostVersion: Host);
        store.InstallFromFile(Zip("v2.gtplugin", ("plugin.json", Manifest(version: "2.0.0"))), null, PluginTrust.Community, PluginSource.LocalFile, hostVersion: Host);
        Assert.Equal("1.0.0", store.Find("gettext.sample")!.Version);
        Assert.Equal("2.0.0", store.Find("gettext.sample")!.PendingVersion);

        // 再起動: 新しい版に切り替わる (前の版は残しておく)
        store = NewStore();
        store.ApplyPending();
        Assert.Equal("2.0.0", store.Find("gettext.sample")!.Version);
        Assert.Equal("1.0.0", store.Find("gettext.sample")!.PreviousVersion);

        // 新しい版の読み込み中に落ちた → 次の起動で前の版に戻す
        store.BeginLoading("gettext.sample");
        store = NewStore();
        store.ApplyPending();
        Assert.Equal("gettext.sample", store.CrashedWhileLoading);
        var r = store.Find("gettext.sample")!;
        Assert.Equal("1.0.0", r.Version);
        Assert.Contains("前の版に戻しました", r.LastError);
        Assert.False(Directory.Exists(store.VersionDirectory("gettext.sample", "2.0.0")));

        // 前の版が無いのに落ちた → 止める
        store.BeginLoading("gettext.sample");
        store = NewStore();
        store.ApplyPending();
        Assert.False(store.Find("gettext.sample")!.Enabled);

        // 消す: 次の起動でファイルも消える
        store.Uninstall("gettext.sample");
        store = NewStore();
        store.ApplyPending();
        Assert.Null(store.Find("gettext.sample"));
        Assert.False(Directory.Exists(Path.Combine(store.Root, "gettext.sample")));
    }

    [Fact]
    public void LoadSucceededRemovesPreviousVersion()
    {
        var store = NewStore();
        store.InstallFromFile(Zip("a1.gtplugin", ("plugin.json", Manifest(version: "1.0.0"))), null, PluginTrust.Community, PluginSource.LocalFile, hostVersion: Host);
        store.InstallFromFile(Zip("a2.gtplugin", ("plugin.json", Manifest(version: "1.1.0"))), null, PluginTrust.Community, PluginSource.LocalFile, hostVersion: Host);
        store = NewStore();
        store.ApplyPending();
        store.BeginLoading("gettext.sample");
        store.EndLoading("gettext.sample");
        Assert.Null(store.Find("gettext.sample")!.PreviousVersion);
        Assert.False(Directory.Exists(store.VersionDirectory("gettext.sample", "1.0.0")));
        var installed = Assert.Single(store.ReadInstalled());
        Assert.NotNull(installed.Manifest);
        Assert.Equal("1.1.0", installed.Manifest!.Version.ToString());
    }

    [Fact]
    public void CorruptStateFileDoesNotBlockStartup()
    {
        var root = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "plugin-state.json"), "{ broken");
        var store = new PluginStore(root);
        store.Load();
        Assert.NotNull(store.RecoveredFrom);
        Assert.Empty(store.Records);
        Assert.True(File.Exists(Path.Combine(root, "plugin-state.json.bad")));
        store.ApplyPending(); // 書き直せる

        // 置き場所のマニフェストが消された・書き換えられた
        store.InstallFromFile(Zip("c.gtplugin", ("plugin.json", Manifest())), null, PluginTrust.Community, PluginSource.LocalFile, hostVersion: Host);
        File.Delete(Path.Combine(store.VersionDirectory("gettext.sample", "1.0.0"), "plugin.json"));
        var item = Assert.Single(store.ReadInstalled());
        Assert.Null(item.Manifest);
        Assert.Contains("見つかりません", item.Error);
    }

    // ───────── 索引 ─────────

    [Fact]
    public void IndexSkipsBadEntriesAndMergesBundled()
    {
        string sha = new('a', 64);
        var json = $$"""
            {"schema": 1, "plugins": [
              {"id": "gettext.keyword-monitor", "name": "Keyword Monitor", "publisher": "KoroCoding", "description": "d", "version": "1.0.0",
               "url": "https://example.com/k.gtplugin", "sha256": "{{sha}}", "size": 100, "platforms": ["any"], "verified": true},
              {"id": "gettext.http", "version": "1.0.0", "url": "http://example.com/x.gtplugin", "sha256": "{{sha}}"},
              {"id": "gettext.nosha", "version": "1.0.0", "url": "https://example.com/x.gtplugin", "sha256": "abc"},
              {"id": "BAD ID", "version": "1.0.0", "url": "https://example.com/x.gtplugin", "sha256": "{{sha}}"},
              {"id": "gettext.badver", "version": "one", "url": "https://example.com/x.gtplugin", "sha256": "{{sha}}"},
              {"id": "gettext.traversal", "version": "1.0.0", "file": "../x.gtplugin", "sha256": "{{sha}}"}
            ]}
            """;
        var entries = PluginIndex.Parse(json, _dir);
        var e = Assert.Single(entries);
        Assert.Equal("gettext.keyword-monitor", e.Id);
        Assert.True(e.IsOfficial);
        Assert.Empty(PluginIndex.Parse("not json"));

        var bundled = new PluginIndexEntry { Id = e.Id, Name = e.Name, Publisher = e.Publisher, Description = e.Description, Version = e.Version, BundledFile = "x", Sha256 = sha };
        var merged = Assert.Single(PluginIndex.Merge([bundled], entries));
        Assert.Equal("x", merged.BundledFile); // 同じ版なら同梱 (ネットワーク不要)
        var newer = new PluginIndexEntry { Id = e.Id, Name = e.Name, Publisher = e.Publisher, Description = e.Description, Version = new SemVersion(1, 1, 0), Url = e.Url, Sha256 = sha };
        Assert.Equal(new SemVersion(1, 1, 0), Assert.Single(PluginIndex.Merge([bundled], [newer])).Version);
        Assert.Contains("以上", new PluginIndexEntry { Id = "gettext.x", Name = "x", Publisher = "p", Description = "d", Version = e.Version, Url = e.Url, Sha256 = sha,
            MinHostVersion = new SemVersion(5, 0, 0) }.Incompatibility(Host, "win-x64"));
    }

    [Fact]
    public async Task IndexFallsBackToCacheWhenOffline()
    {
        var cache = Path.Combine(_dir, "index-cache.json");
        File.WriteAllText(cache, "{\"plugins\": []}");
        using var http = new System.Net.Http.HttpClient(new FailingHandler());
        var (entries, fromCache, error) = await PluginIndex.FetchAsync(http, new Uri("https://example.invalid/plugins-index.json"), cache, CancellationToken.None);
        Assert.True(fromCache);
        Assert.NotNull(error);
        Assert.Empty(entries);
        var (_, _, httpError) = await PluginIndex.FetchAsync(http, new Uri("http://example.invalid/i.json"), cache, CancellationToken.None);
        Assert.Contains("https", httpError);
    }

    private sealed class FailingHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct) =>
            throw new System.Net.Http.HttpRequestException("名前を解決できません (DNS)");
    }

    [Fact]
    public void LogRedactsSecretsAndUserName()
    {
        var redacted = PluginLog.Redact("api_key=abcd1234 token: xyz " + Environment.UserName);
        Assert.DoesNotContain("abcd1234", redacted);
        Assert.DoesNotContain("xyz", redacted);
        if (Environment.UserName.Length >= 2) Assert.DoesNotContain(Environment.UserName, redacted);
    }

    [Theory]
    [InlineData("Could not find file 'C:\\My Files\\給与明細 10月.pptx'.", "給与明細")]
    [InlineData("Access to the path C:\\Data\\秘密.pdf is denied.", "秘密")]
    [InlineData("書けません: /Users/someone/Desktop/計画書.pdf", "計画書")]
    [InlineData("共有: \\\\server\\share\\名簿.xlsx", "名簿")]
    [InlineData("authorization: Bearer 0123456789abcdef", "0123456789abcdef")]
    public void LogRedactsPathsAndBearerTokens(string message, string secret)
    {
        var redacted = PluginLog.Redact(message);
        Assert.DoesNotContain(secret, redacted);
    }
}
