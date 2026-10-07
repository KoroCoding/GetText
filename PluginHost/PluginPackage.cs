using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace GetText;

/// <summary>パッケージ (.gtplugin) を安全に展開するときの上限。</summary>
public sealed record PackageLimits(long MaxTotalBytes = 512L * 1024 * 1024, int MaxFiles = 4000, double MaxRatio = 200, long MaxFileBytes = 256L * 1024 * 1024)
{
    public static readonly PackageLimits Default = new();
}

/// <summary>パッケージを入れられなかった理由 (利用者に見せる文)。</summary>
public sealed class PluginPackageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 拡張機能のパッケージ (.gtplugin = ZIP。中は plugin.json・bin/・resources/・README.md・LICENSE) の確認と安全な展開。
/// ../・絶対パス・ドライブ・シンボリックリンク・予約された名前・大きすぎる展開 (ZIP 爆弾) は拒否する。
/// </summary>
public static class PluginPackages
{
    public const string Extension = ".gtplugin";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>パッケージの中の安全な相対パスか (/ 区切り。.. や絶対パス・ドライブ・予約名・制御文字を含まない)。</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 260) return false;
        if (path.Contains('\0') || path.Any(char.IsControl)) return false;
        var p = path.Replace('\\', '/');
        if (p.StartsWith('/') || p.StartsWith("//", StringComparison.Ordinal)) return false;
        if (p.Length >= 2 && p[1] == ':') return false; // C: など
        foreach (var part in p.Split('/'))
        {
            if (part.Length == 0 || part == "." || part == "..") return false;
            if (part.EndsWith('.') || part.EndsWith(' ')) return false; // Windows で別の名前になる
            if (part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0) return false;
            var stem = part.Split('.')[0];
            if (Reserved.Contains(stem)) return false;
        }
        return true;
    }

    /// <summary>ファイルの SHA-256 (小文字の 16 進)。</summary>
    public static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// ZIP を destination に展開する (destination は空の新しいフォルダ)。
    /// 中身を先にすべて確かめ、問題があれば何も書かずに例外。書き出すときも、宣言より大きい中身は途中で止める。
    /// </summary>
    public static void ExtractSafely(string zipPath, string destination, PackageLimits? limits = null)
    {
        limits ??= PackageLimits.Default;
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(zipPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new PluginPackageException("パッケージが壊れています (ZIP として読めません)", ex);
        }
        using (archive)
        {
            if (archive.Entries.Count > limits.MaxFiles) throw new PluginPackageException($"パッケージのファイルが多すぎます ({archive.Entries.Count} 個)");
            long total = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in archive.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                bool directory = name.EndsWith('/');
                var path = directory ? name.TrimEnd('/') : name;
                if (path.Length == 0) continue;
                if (!IsSafeRelativePath(path)) throw new PluginPackageException($"パッケージに安全でない場所のファイルがあります: {e.FullName}");
                // Unix のシンボリックリンク (外部属性の上位 16 ビットが mode) は拒否
                int mode = (e.ExternalAttributes >> 16) & 0xF000;
                if (mode == 0xA000) throw new PluginPackageException($"パッケージにシンボリックリンクがあります: {e.FullName}");
                if (!names.Add(path)) throw new PluginPackageException($"パッケージに同じ名前のファイルが 2 つあります: {e.FullName}");
                if (directory) continue;
                if (e.Length > limits.MaxFileBytes) throw new PluginPackageException($"パッケージのファイルが大きすぎます: {e.FullName}");
                if (e.CompressedLength > 0 && e.Length / (double)e.CompressedLength > limits.MaxRatio)
                    throw new PluginPackageException($"パッケージの圧縮率が異常です (ZIP 爆弾の疑い): {e.FullName}");
                total += e.Length;
                if (total > limits.MaxTotalBytes) throw new PluginPackageException("パッケージを展開すると大きすぎます");
            }

            Directory.CreateDirectory(destination);
            var root = Path.GetFullPath(destination);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
            long written = 0;
            foreach (var e in archive.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                var target = Path.GetFullPath(Path.Combine(root, name.TrimEnd('/')));
                if (!target.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new PluginPackageException($"パッケージに安全でない場所のファイルがあります: {e.FullName}");
                if (name.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = e.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long entryBytes = 0;
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    entryBytes += n;
                    written += n;
                    // 宣言した大きさを超えて出てくる中身 (ヘッダーを偽った ZIP) は止める
                    if (entryBytes > e.Length || written > limits.MaxTotalBytes)
                        throw new PluginPackageException($"パッケージの中身が宣言より大きいです: {e.FullName}");
                    output.Write(buffer, 0, n);
                }
            }
        }
    }
}
