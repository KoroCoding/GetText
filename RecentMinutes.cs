using System.Globalization;
using System.IO;

namespace GetText;

/// <summary>自動保存した最近の議事録 (「開く」のメニューに出す。Windows 版・Mac 版で共通)。</summary>
public static class RecentMinutes
{
    /// <summary>
    /// フォルダの議事録を新しい順に (場所, 表示する名前)。議事録データ (.json) を優先し、
    /// 古い版で Markdown だけを保存したものも入れる。
    /// </summary>
    public static List<(string Path, string Label)> List(string folder, int count)
    {
        if (!Directory.Exists(folder)) return [];
        return new DirectoryInfo(folder).GetFiles("議事録_*.*")
            .Where(f => f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                        || (f.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.ChangeExtension(f.FullName, ".json"))))
            .OrderByDescending(f => f.LastWriteTime)
            .Take(count)
            .Select(f => (f.FullName, Label(f.Name, f.LastWriteTime)))
            .ToList();
    }

    /// <summary>
    /// 「議事録_20261003_140012.json」→「10月3日 (土) 14:00 ・ 記録」、「議事録_会議_20261003_140012.json」→「… ・ 会議」。
    /// 名前から日時が読めなければ、ファイルの更新日時を使う。
    /// </summary>
    public static string Label(string fileName, DateTime modified)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        name = name.StartsWith("議事録_") ? name["議事録_".Length..] : name;
        var parts = name.Split('_');
        var when = modified;
        string source = "記録";
        if (parts.Length >= 2 && DateTime.TryParseExact(parts[^2] + parts[^1], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var started))
        {
            when = started;
            if (parts.Length > 2) source = string.Join("_", parts[..^2]);
        }
        else if (name.Length > 0)
            source = name;
        return when.ToString("M月d日 (ddd) H:mm", CultureInfo.GetCultureInfo("ja-JP")) + " ・ " + source;
    }
}
