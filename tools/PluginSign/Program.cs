// 拡張機能のパッケージの署名の道具。
//   keygen                         新しい鍵を作る (秘密鍵は標準出力に出さず、指定のファイルに書く)
//   keygen <秘密鍵のファイル>       → 公開鍵 (Base64) を表示。GetText の PluginSignatures.TrustedKeys に登録する
//   sign <パッケージ> <鍵の名前>      秘密鍵は環境変数 GETTEXT_PLUGIN_SIGNING_KEY (PKCS#8 の Base64) から読む
//   verify <パッケージ> <鍵の名前> <公開鍵> <id の始まり> <id>
using GetText;

if (args.Length >= 2 && args[0] == "keygen")
{
    if (File.Exists(args[1])) { Console.Error.WriteLine("ファイルがもうあります (上書きしません): " + args[1]); return 2; }
    var (privateKey, publicKey) = PluginSignatures.CreateKey();
    File.WriteAllText(args[1], privateKey);
    Console.WriteLine("公開鍵 (GetText に登録する): " + publicKey);
    Console.WriteLine("秘密鍵は " + args[1] + " に書きました。GitHub の Secrets (GETTEXT_PLUGIN_SIGNING_KEY) に入れたら、このファイルは安全な場所に移すか消してください。");
    return 0;
}
if (args.Length >= 3 && args[0] == "sign")
{
    var key = Environment.GetEnvironmentVariable("GETTEXT_PLUGIN_SIGNING_KEY");
    if (string.IsNullOrWhiteSpace(key)) { Console.Error.WriteLine("環境変数 GETTEXT_PLUGIN_SIGNING_KEY がありません"); return 2; }
    PluginSignatures.Sign(args[1], args[2], key.Trim());
    Console.WriteLine("署名しました: " + Path.GetFileName(args[1]));
    return 0;
}
if (args.Length >= 6 && args[0] == "verify")
{
    PluginSignatures.ReplaceTrustedKeysForTest([new PluginSigningKey(args[2], "-", args[4], PluginTrust.Official, args[3])]);
    var result = PluginSignatures.Verify(args[1], args[5]);
    Console.WriteLine($"{result.State} {result.KeyId} {result.Reason}");
    return result.State == PluginSignatureState.Valid ? 0 : 1;
}
Console.Error.WriteLine("使い方: keygen <秘密鍵のファイル> | sign <パッケージ> <鍵の名前> | verify <パッケージ> <鍵の名前> <公開鍵> <id の始まり> <id>");
return 2;
