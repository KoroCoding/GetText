using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace GetText;

/// <summary>
/// 議事録のプロジェクト (.gettext): 議事録データ・Markdown・音声を 1 つのファイル (中身は ZIP) にまとめる。
/// 別の PC に渡しても、発言の時刻を押して音声を聞いたり、話者を直したりできる。
/// 中身: minutes.json (音声の場所は「audio/…」の相対パス) ・ 議事録.md (読むだけ用) ・ audio/ (記録の音声・元のファイルの音声)
/// </summary>
public static class MinutesProject
{
    public const string Extension = ".gettext";
    private const string DataEntry = "minutes.json";
    private const string ReadableEntry = "議事録.md";
    private const string AudioFolder = "audio/";

    /// <summary>保存した結果 (入れた音声の数・見つからずに入れられなかった音声の数)。</summary>
    public sealed record SaveResult(int AudioFiles, int MissingAudio, long Bytes);

    /// <summary>
    /// プロジェクトとして保存する。sourceAudio は、ファイルから作った議事録の元の音声 (動画なら取り出した音声。無ければ null)。
    /// 一時ファイルに書いてから入れ替えるので、途中で失敗しても前のファイルは壊れない。
    /// </summary>
    public static SaveResult Save(string path, MinutesFile file, string markdown, string? sourceAudio = null)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var include = new List<(string From, string Entry)>();
        int missing = 0;
        string Add(string from)
        {
            var existing = include.FirstOrDefault(i => string.Equals(i.From, from, StringComparison.OrdinalIgnoreCase));
            if (existing.Entry != null) return existing.Entry;
            var name = Path.GetFileName(from);
            var stem = Path.GetFileNameWithoutExtension(name);
            for (int n = 2; !names.Add(name); n++) name = $"{stem}_{n}{Path.GetExtension(from)}";
            include.Add((from, AudioFolder + name));
            return AudioFolder + name;
        }

        var audio = file.Audio?.Select(a =>
        {
            if (File.Exists(a.Path)) return a with { Path = Add(a.Path) };
            missing++;
            return a;
        }).ToList();
        string? sourcePath = file.SourcePath;
        if (file.FromFile)
        {
            var from = sourceAudio ?? file.SourcePath;
            if (from != null && File.Exists(from)) sourcePath = Add(from);
            else missing++;
        }
        var data = file with { Audio = audio, SourcePath = sourcePath };

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
                {
                    WriteText(zip, DataEntry, data.ToJson());
                    WriteText(zip, ReadableEntry, markdown);
                    foreach (var (from, entry) in include)
                    {
                        // 圧縮済みの音声 (m4a・mp3・動画) は縮まないので、そのまま入れて速く保存する
                        var level = Path.GetExtension(from).ToLowerInvariant() is ".wav" or ".flac" ? CompressionLevel.Fastest : CompressionLevel.NoCompression;
                        zip.CreateEntryFromFile(from, entry, level);
                    }
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        return new SaveResult(include.Count, missing, new FileInfo(full).Length);
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    /// <summary>
    /// プロジェクトを開く: 音声を extractRoot の下のフォルダーに取り出し (再生できるように)、音声の場所をそこに直した議事録データを返す。
    /// 同じプロジェクトを開き直したときは、中身が変わっていなければ取り出し直さない。
    /// </summary>
    public static MinutesFile Open(string path, string extractRoot)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists) throw new FileNotFoundException("プロジェクトのファイルが見つかりません", full);
        var stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        var dir = Path.Combine(extractRoot, FolderName(full));
        var stampFile = Path.Combine(dir, ".source");
        bool fresh = File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp + "|" + full && File.Exists(Path.Combine(dir, DataEntry));
        if (!fresh)
        {
            try
            {
                Extract(full, dir);
            }
            catch (IOException)
            {
                // 前に取り出した音声を再生中などで書き換えられないときは、別のフォルダーに取り出す
                dir = Path.Combine(extractRoot, FolderName(full) + "_" + DateTime.Now.ToString("HHmmss"));
                stampFile = Path.Combine(dir, ".source");
                Extract(full, dir);
            }
            File.WriteAllText(stampFile, stamp + "|" + full);
        }
        var file = MinutesFile.FromJson(File.ReadAllText(Path.Combine(dir, DataEntry)));
        string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(dir, p.Replace('/', Path.DirectorySeparatorChar)));
        return file with
        {
            Audio = file.Audio?.Select(a => a with { Path = Resolve(a.Path) }).ToList(),
            SourcePath = file.SourcePath is { } s ? Resolve(s) : null,
        };
    }

    /// <summary>プロジェクトかどうか (拡張子で判断)。</summary>
    public static bool IsProject(string path) => Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    private static void Extract(string zipPath, string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.GetEntry(DataEntry) == null) throw new InvalidDataException("GetText の議事録プロジェクトではありません (minutes.json がありません)");
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var target = Path.GetFullPath(Path.Combine(dir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            // フォルダーの外を指す名前 (../ など) は取り出さない
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    // プロジェクトの場所ごとに 1 つのフォルダー (名前 + 場所のハッシュ)
    private static string FolderName(string fullPath)
    {
        var name = Path.GetFileNameWithoutExtension(fullPath);
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length > 40) name = name[..40];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant())))[..8];
        return $"{name}_{hash}";
    }
}
