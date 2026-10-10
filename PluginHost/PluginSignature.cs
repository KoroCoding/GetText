using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GetText;

/// <summary>信頼する発行元の鍵 (公開鍵)。この鍵で署名を確かめられたパッケージだけ、Official / Verified にする。</summary>
/// <param name="KeyId">鍵の名前 (signature.json の keyId)。</param>
/// <param name="Publisher">発行元の名前 (画面に出す)。</param>
/// <param name="IdPrefix">この鍵で署名してよい拡張機能の id の始まり (例: "gettext.")。ほかの id には効かない。</param>
/// <param name="Trust">この鍵で確かめたときの信頼。</param>
/// <param name="PublicKey">公開鍵 (SubjectPublicKeyInfo の Base64。ECDSA P-256)。</param>
public sealed record PluginSigningKey(string KeyId, string Publisher, string IdPrefix, PluginTrust Trust, string PublicKey);

/// <summary>署名を確かめた結果。</summary>
public enum PluginSignatureState
{
    /// <summary>署名が無い。</summary>
    Unsigned,
    /// <summary>信頼する鍵で確かめた。</summary>
    Valid,
    /// <summary>署名は正しいが、鍵を信頼していない (知らない鍵・この id には使えない鍵)。</summary>
    Untrusted,
    /// <summary>署名が正しくない (中身が書き換えられた・壊れている)。入れない。</summary>
    Invalid,
}

public sealed record PluginSignatureResult(PluginSignatureState State, string? KeyId = null, PluginSigningKey? Key = null, string? Reason = null);

/// <summary>
/// 拡張機能のパッケージ (.gtplugin) の署名。パッケージの中の signature.json に、ほかのすべてのファイルの
/// 名前と SHA-256 の一覧への ECDSA P-256 (SHA-256) の署名を入れる。SHA-256 だけでは発行元は確かめられないので、
/// 「公式」「確認済み」は、信頼する鍵 (<see cref="TrustedKeys"/>) で署名を確かめたものだけにする。
/// </summary>
public static class PluginSignatures
{
    public const string FileName = "signature.json";
    public const string Algorithm = "ECDSA-P256-SHA256";
    private const string Header = "GetText plugin package signature v1\n";

    /// <summary>
    /// GetText が信頼する発行元の鍵。公式の鍵は、秘密鍵を GetText の開発元が管理する (docs/security/PLUGIN_SIGNING.md)。
    /// まだ公式の鍵を作っていない間は空 (署名の無いものは、同梱のもの以外「コミュニティ」になる)。
    /// </summary>
    public static IReadOnlyList<PluginSigningKey> TrustedKeys => _keys;

    private static List<PluginSigningKey> _keys = [];

    /// <summary>動作確認用: 信頼する鍵を差し替える (戻すには、返した値で呼び直す)。</summary>
    internal static List<PluginSigningKey> ReplaceTrustedKeysForTest(List<PluginSigningKey> keys)
    {
        var old = _keys;
        _keys = keys;
        return old;
    }

    /// <summary>署名する内容: signature.json 以外のファイルの「SHA-256 名前」の一覧 (名前の順。名前は / 区切り)。</summary>
    public static byte[] SignedContent(ZipArchive zip)
    {
        var lines = new List<string>();
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName == FileName) continue;
            using var stream = entry.Open();
            lines.Add(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() + "  " + entry.FullName.Replace('\\', '/'));
        }
        lines.Sort(StringComparer.Ordinal);
        return Encoding.UTF8.GetBytes(Header + string.Join("\n", lines) + "\n");
    }

    /// <summary>パッケージの署名を確かめる。id はマニフェストの id (鍵が使える id かを見る)。</summary>
    public static PluginSignatureResult Verify(string packagePath, string id)
    {
        try
        {
            using var zip = ZipFile.OpenRead(packagePath);
            if (zip.GetEntry(FileName) is not { } entry) return new(PluginSignatureState.Unsigned);
            JsonNode? json;
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) json = JsonNode.Parse(reader.ReadToEnd());
            string? keyId = json?["keyId"]?.GetValue<string>();
            string? algorithm = json?["algorithm"]?.GetValue<string>();
            string? signature = json?["signature"]?.GetValue<string>();
            if (keyId == null || signature == null || algorithm != Algorithm)
                return new(PluginSignatureState.Invalid, keyId, Reason: "署名の形が正しくありません");
            var key = _keys.FirstOrDefault(k => k.KeyId == keyId);
            byte[] content = SignedContent(zip);
            if (key == null)
                return new(PluginSignatureState.Untrusted, keyId, Reason: $"知らない鍵 ({keyId}) で署名されています");
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey), out _);
            if (!ecdsa.VerifyData(content, Convert.FromBase64String(signature), HashAlgorithmName.SHA256))
                return new(PluginSignatureState.Invalid, keyId, key, "署名が中身と合いません (書き換えられたか、壊れています)");
            if (!id.StartsWith(key.IdPrefix, StringComparison.Ordinal))
                return new(PluginSignatureState.Untrusted, keyId, key, $"鍵 ({keyId}) は {key.IdPrefix}… の拡張機能にだけ使えます");
            return new(PluginSignatureState.Valid, keyId, key);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or InvalidDataException or InvalidOperationException)
        {
            return new(PluginSignatureState.Invalid, Reason: "署名を読めませんでした: " + ex.GetType().Name);
        }
    }

    /// <summary>パッケージに署名を入れる (署名の道具・テストで使う。秘密鍵は PKCS#8 の Base64)。前の署名は置き換える。</summary>
    public static void Sign(string packagePath, string keyId, string privateKeyPkcs8)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8), out _);
        using var zip = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        zip.GetEntry(FileName)?.Delete();
        var signature = ecdsa.SignData(SignedContent(zip), HashAlgorithmName.SHA256);
        var json = new JsonObject { ["format"] = 1, ["keyId"] = keyId, ["algorithm"] = Algorithm, ["signature"] = Convert.ToBase64String(signature) };
        var entry = zip.CreateEntry(FileName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>新しい鍵を作る (秘密鍵 PKCS#8 の Base64, 公開鍵 SubjectPublicKeyInfo の Base64)。</summary>
    public static (string PrivateKey, string PublicKey) CreateKey()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()), Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    }
}
