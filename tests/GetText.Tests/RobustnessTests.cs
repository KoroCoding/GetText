using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GetText.Tests;

public class RobustnessTests
{
    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void ソースとスクリプトに制御文字が入っていない()
    {
        // 編集の道具で「\v」「\r」が制御文字に化けると、PowerShell の場所の文字列などが壊れる (update.ps1 で起きた)
        var bad = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Root(), "*.*", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (!Regex.IsMatch(path, @"\.(cs|py|ps1|bat|xaml|axaml|sh|swift|csproj|md)$")) continue;
            var lines = File.ReadAllText(path).Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i].TrimEnd('\r'), @"[\x00-\x08\x0B\x0C\x0E-\x1F]"))
                    bad.Add($"{Path.GetRelativePath(Root(), path)}:{i + 1}");
        }
        Assert.Empty(bad);
    }

    [Fact]
    public void Word_XMLに書けない文字は除いて開ける文書にする()
    {
        var bytes = DocxWriter.FromMarkdown("ほかのアプリから貼った\v文字と\u0001制御文字 😀");
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var stream = zip.GetEntry("word/document.xml")!.Open();
        var xml = XDocument.Load(stream); // 読めなければ例外
        Assert.Equal("ほかのアプリから貼った文字と制御文字 😀", string.Concat(xml.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value)));
    }

    [Fact]
    public void 音声の保存に失敗しても例外を外に出さない()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gettext_rec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "a.wav");
            var recorder = new SessionRecorder(path, DateTime.Now, ["win", "mic"]);
            recorder.Add("win", new byte[3200]);
            // ディスクが一杯のときの代わりに、書き込み先のファイルを先に閉じて書き込みを失敗させる
            var file = (IDisposable)typeof(SessionRecorder).GetField("_file", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(recorder)!;
            file.Dispose();
            recorder.Add("mic", new byte[3200]);   // 閉じたファイルへの書き込み → 例外を出さずに保存をやめる
            recorder.RemoveSource("win");
            recorder.Dispose();
            Assert.NotNull(recorder.WriteError);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 一時ファイルに書けなくても残さない()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gettext_atomic2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "議事録.md");
            File.WriteAllText(path, "前の内容");
            File.SetAttributes(path, FileAttributes.ReadOnly); // 入れ替えに失敗させる
            Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "新しい内容"));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal("前の内容", File.ReadAllText(path));
            File.SetAttributes(path, FileAttributes.Normal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 設定のショートカットが壊れていても既定のキーを使う()
    {
        var settings = new AppSettings { Hotkeys = null! };
        Assert.Equal("Ctrl+Alt+C", HotkeyText.For(settings, "copy-ocr"));
        Assert.False(HotkeyText.TryParse("Alt+F4", out _, out _)); // Windows で決まった働きのあるキーは使わない
        Assert.False(HotkeyText.TryParse(null, out _, out _));
    }
}
