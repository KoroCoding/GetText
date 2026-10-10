using System.IO;
using System.IO.Compression;

namespace GetText.Tests;

/// <summary>
/// 拡張機能のパッケージの署名: 信頼する鍵で確かめたものだけ公式 / 確認済み。書き換えたパッケージは入れない。
/// SHA-256 (一覧と同じファイルか) だけでは発行元を確かめたことにしない。
/// </summary>
[Collection("PluginSigningKeys")] // (信頼する鍵の一覧を差し替えるので、ほかの署名のテストと同時に動かさない)
public class PluginSignatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gettext-signature-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<PluginSigningKey> _savedKeys;
    private readonly (string Private, string Public) _official = PluginSignatures.CreateKey();
    private readonly (string Private, string Public) _thirdParty = PluginSignatures.CreateKey();
    private static readonly SemVersion Host = new(1, 1, 0);

    public PluginSignatureTests()
    {
        Directory.CreateDirectory(_dir);
        _savedKeys = PluginSignatures.ReplaceTrustedKeysForTest(
        [
            new PluginSigningKey("test-official", "KoroCoding", "gettext.", PluginTrust.Official, _official.Public),
            new PluginSigningKey("test-acme", "Acme", "acme.", PluginTrust.Verified, _thirdParty.Public),
        ]);
    }

    public void Dispose()
    {
        PluginSignatures.ReplaceTrustedKeysForTest(_savedKeys);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Package(string id, string name = "p.gtplugin")
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string p, string c)
        {
            using var w = new StreamWriter(zip.CreateEntry(p).Open());
            w.Write(c);
        }
        Add("plugin.json", $$"""
            { "id": "{{id}}", "name": "テスト", "version": "1.0.0", "publisher": "Test", "description": "d", "apiVersion": 1,
              "minHostVersion": "1.0.0", "platforms": ["any"], "permissions": [] }
            """);
        Add("README.md", "readme");
        return path;
    }

    private PluginStore Store() => new(Path.Combine(_dir, "store"));

    [Fact]
    public void OfficialKeySignatureMakesItOfficial()
    {
        var package = Package("gettext.signed");
        PluginSignatures.Sign(package, "test-official", _official.Private);
        Assert.Equal(PluginSignatureState.Valid, PluginSignatures.Verify(package, "gettext.signed").State);

        var store = Store();
        // ファイルから入れても (索引の SHA-256 が無くても)、署名で発行元を確かめられるので公式
        store.InstallFromFile(package, null, PluginTrust.Community, PluginSource.LocalFile, hostVersion: Host);
        var record = store.Find("gettext.signed")!;
        Assert.Equal(PluginTrust.Official, record.Trust);
        Assert.Equal("test-official", record.SignedBy);
    }

    [Fact]
    public void TamperedPackageIsRejected()
    {
        var package = Package("gettext.tampered");
        PluginSignatures.Sign(package, "test-official", _official.Private);
        // 署名の後で中身を書き換える
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            zip.GetEntry("README.md")!.Delete();
            using var w = new StreamWriter(zip.CreateEntry("README.md").Open());
            w.Write("changed");
        }
        Assert.Equal(PluginSignatureState.Invalid, PluginSignatures.Verify(package, "gettext.tampered").State);
        var store = Store();
        var ex = Assert.Throws<PluginPackageException>(() => store.InstallFromFile(package, null, PluginTrust.Community, PluginSource.LocalFile, hostVersion: Host));
        Assert.Contains("署名が正しくありません", ex.Message);
        Assert.Null(store.Find("gettext.tampered"));
    }

    [Fact]
    public void AddedFileBreaksTheSignature()
    {
        var package = Package("gettext.extra");
        PluginSignatures.Sign(package, "test-official", _official.Private);
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Update))
        using (var w = new StreamWriter(zip.CreateEntry("bin/extra.dll").Open()))
            w.Write("injected");
        Assert.Equal(PluginSignatureState.Invalid, PluginSignatures.Verify(package, "gettext.extra").State);
    }

    [Fact]
    public void UnknownKeyAndOtherIdsAreCommunity()
    {
        var store = Store();
        // 知らない鍵 (正しく署名されていても、信頼していない)
        var unknown = PluginSignatures.CreateKey();
        var a = Package("gettext.unknownkey", "a.gtplugin");
        PluginSignatures.Sign(a, "someone-else", unknown.PrivateKey);
        Assert.Equal(PluginSignatureState.Untrusted, PluginSignatures.Verify(a, "gettext.unknownkey").State);
        store.InstallFromFile(a, null, PluginTrust.Official, PluginSource.LocalFile, hostVersion: Host);
        Assert.Equal(PluginTrust.Community, store.Find("gettext.unknownkey")!.Trust);

        // 第三者の鍵は、その発行元の id (acme.) にだけ効く。gettext. の id を名乗っても公式にならない
        var b = Package("gettext.impostor", "b.gtplugin");
        PluginSignatures.Sign(b, "test-acme", _thirdParty.Private);
        Assert.Equal(PluginSignatureState.Untrusted, PluginSignatures.Verify(b, "gettext.impostor").State);
        store.InstallFromFile(b, null, PluginTrust.Official, PluginSource.LocalFile, hostVersion: Host);
        Assert.Equal(PluginTrust.Community, store.Find("gettext.impostor")!.Trust);

        // 第三者の鍵で、その発行元の id なら確認済み
        var c = Package("acme.tool", "c.gtplugin");
        PluginSignatures.Sign(c, "test-acme", _thirdParty.Private);
        store.InstallFromFile(c, null, PluginTrust.Community, PluginSource.LocalFile, hostVersion: Host);
        Assert.Equal(PluginTrust.Verified, store.Find("acme.tool")!.Trust);
    }

    [Fact]
    public void ShaFromAnOnlineIndexAloneIsNotOfficial()
    {
        // オンラインの一覧の SHA-256 と一致しても、署名が無ければ発行元は確かめていない → コミュニティ
        var store = Store();
        var online = Package("gettext.online", "online.gtplugin");
        store.InstallFromFile(online, PluginPackages.Sha256(online), PluginTrust.Official, PluginSource.Index, hostVersion: Host);
        Assert.Equal(PluginTrust.Community, store.Find("gettext.online")!.Trust);

        // GetText に同梱の公式のもの (同梱の一覧の SHA-256 と一致) は、配布物そのものが根拠なので公式のまま
        var bundled = Package("gettext.bundled", "bundled.gtplugin");
        store.InstallFromFile(bundled, PluginPackages.Sha256(bundled), PluginTrust.Official, PluginSource.Bundled, hostVersion: Host);
        Assert.Equal(PluginTrust.Official, store.Find("gettext.bundled")!.Trust);
        Assert.Null(store.Find("gettext.bundled")!.SignedBy);
    }

    [Fact]
    public void OldRecordsTrustedWithoutSignatureAreDowngraded()
    {
        // 前の版は、オンラインの一覧の SHA-256・verified だけで公式・確認済みにしていた。署名で確かめていないものはコミュニティに戻す
        var root = Path.Combine(_dir, "old");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "plugin-state.json"), """
            { "Schema": 1, "Plugins": [
              { "Id": "gettext.online", "Version": "1.0.0", "Enabled": true, "Trust": "Official", "Source": "Index" },
              { "Id": "acme.listed", "Version": "1.0.0", "Enabled": true, "Trust": "Verified", "Source": "Index" },
              { "Id": "gettext.bundled", "Version": "1.0.0", "Enabled": true, "Trust": "Official", "Source": "Bundled" },
              { "Id": "gettext.signed", "Version": "1.0.0", "Enabled": true, "Trust": "Official", "Source": "Index", "SignedBy": "test-official" }
            ] }
            """);
        var store = new PluginStore(root);
        Assert.Equal(PluginTrust.Community, store.Find("gettext.online")!.Trust);
        Assert.Equal(PluginTrust.Community, store.Find("acme.listed")!.Trust);
        Assert.Equal(PluginTrust.Official, store.Find("gettext.bundled")!.Trust);
        Assert.Equal(PluginTrust.Official, store.Find("gettext.signed")!.Trust);
    }

    [Fact]
    public void InstallReadsThePackageOnceAndLeavesNoCopy()
    {
        // 確かめた物と入れる物を同じにするため、パッケージの写しで確かめて入れる。写しは後に残さない
        var package = Package("gettext.copied");
        PluginSignatures.Sign(package, "test-official", _official.Private);
        var store = Store();
        store.InstallFromFile(package, null, PluginTrust.Community, PluginSource.LocalFile, hostVersion: Host);
        Assert.Equal(PluginTrust.Official, store.Find("gettext.copied")!.Trust);
        var staging = Path.Combine(store.Root, ".staging");
        Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFiles(staging, "*.gtplugin").Any());
        Assert.True(File.Exists(package)); // (元のファイルはそのまま)
    }

    [Fact]
    public void MalformedSignatureIsRejected()
    {
        var package = Package("gettext.malformed");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Update))
        using (var w = new StreamWriter(zip.CreateEntry(PluginSignatures.FileName).Open()))
            w.Write("""{ "keyId": "test-official", "algorithm": "ECDSA-P256-SHA256", "signature": "not base64!" }""");
        Assert.Equal(PluginSignatureState.Invalid, PluginSignatures.Verify(package, "gettext.malformed").State);
        Assert.Throws<PluginPackageException>(() => Store().InstallFromFile(package, null, PluginTrust.Community, PluginSource.LocalFile, hostVersion: Host));
    }
}
