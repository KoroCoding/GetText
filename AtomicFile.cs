using System.IO;
using System.Text;

namespace GetText;

/// <summary>
/// 一時ファイルに書いてから入れ替える書き込み。書いている途中で落ちたり電源が切れたりしても、
/// 前に保存した内容が空や途中までのファイルにならない。
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text) =>
        WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { } // 入れ替えられなかった (読み取り専用・ほかのアプリで開いている) 一時ファイルを残さない
            throw;
        }
    }
}
