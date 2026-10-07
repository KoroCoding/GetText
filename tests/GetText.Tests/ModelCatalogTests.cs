using System.IO;
using System.Text.RegularExpressions;

namespace GetText.Tests;

/// <summary>モデルの一覧 (状態・download_models.py との対応・進み具合の読み取り)。通信はしない。</summary>
public class ModelCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gettext-models-" + Guid.NewGuid().ToString("N"));

    public ModelCatalogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GetText.csproj"))) return d.FullName;
        throw new InvalidOperationException("GetText.csproj が見つかりません");
    }

    [Fact]
    public void CatalogMatchesDownloadScript()
    {
        // 一覧の名前とファイルは、実際にダウンロードする offline/download_models.py の MODELS と同じでなければならない
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "offline", "download_models.py"));
        var body = script[script.IndexOf("MODELS = {", StringComparison.Ordinal)..script.IndexOf("\ndef ", StringComparison.Ordinal)];
        var blocks = Regex.Matches(body, @"^    ""([^""]+)"": \{(.*?)^    \},", RegexOptions.Multiline | RegexOptions.Singleline);
        var fromScript = blocks.ToDictionary(
            m => m.Groups[1].Value,
            m => Regex.Matches(m.Groups[2].Value, @"^            ""([^""]+)"": ", RegexOptions.Multiline).Select(f => f.Groups[1].Value).Order().ToList());

        Assert.Equal(fromScript.Keys.Order(), ModelCatalog.All.Select(m => m.Id).Order());
        foreach (var model in ModelCatalog.All)
            Assert.Equal(fromScript[model.Id], model.Files.Order().ToList());
    }

    [Fact]
    public void NllbIsMarkedNonCommercial()
    {
        var nllb = ModelCatalog.Find("nllb-200-1.3B")!;
        Assert.True(nllb.NonCommercial);
        Assert.Contains("非商用", ModelCatalog.LicenseNote(nllb));
        Assert.All(ModelCatalog.All.Where(m => m.Id != nllb.Id), m => Assert.False(m.NonCommercial));
    }

    [Fact]
    public void DetectsInstalledPartialAndMissing()
    {
        var model = ModelCatalog.Find("reazonspeech-k2-v2")!;
        Assert.Equal(ModelState.Missing, ModelCatalog.Status(model, _dir).State);

        var dir = Path.Combine(_dir, model.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, model.Files[0]), new byte[10]);
        Assert.Equal(ModelState.Partial, ModelCatalog.Status(model, _dir).State);

        foreach (var f in model.Files) File.WriteAllBytes(Path.Combine(dir, f), new byte[10]);
        var installed = ModelCatalog.Status(model, _dir);
        Assert.Equal(ModelState.Installed, installed.State);
        Assert.Equal(10L * model.Files.Count, installed.Bytes);

        // ダウンロードの途中のファイル (.part) があれば「途中まで」
        File.WriteAllBytes(Path.Combine(dir, "model.bin.part"), new byte[3]);
        Assert.Equal(ModelState.Partial, ModelCatalog.Status(model, _dir).State);
    }

    [Fact]
    public void ShowsPluginsThatUseAModel()
    {
        var manifest = PluginManifests.Parse("""
            { "id": "gettext.sample", "name": "見本", "version": "1.0.0", "publisher": "KoroCoding", "description": "英語を訳す見本",
              "apiVersion": 1, "minHostVersion": "1.0.0", "platforms": ["any"], "permissions": [], "models": ["fugumt-en-ja"] }
            """, out var error);
        Assert.Null(error);
        var status = ModelCatalog.Status(ModelCatalog.Find("fugumt-en-ja")!, _dir, [manifest!]);
        Assert.Equal(["見本"], status.UsedByPlugins);
        Assert.Contains("使っている拡張機能: 見本", ModelTexts.Meta(status));
    }

    [Fact]
    public void RemoveDeletesOnlyThatModel()
    {
        foreach (var id in new[] { "fugumt-en-ja", "speaker-campplus" })
        {
            Directory.CreateDirectory(Path.Combine(_dir, id));
            File.WriteAllText(Path.Combine(_dir, id, "model.bin"), "x");
        }
        ModelCatalog.Remove(ModelCatalog.Find("fugumt-en-ja")!, _dir);
        Assert.False(Directory.Exists(Path.Combine(_dir, "fugumt-en-ja")));
        Assert.True(Directory.Exists(Path.Combine(_dir, "speaker-campplus")));
    }

    [Theory]
    [InlineData("     45% (120 / 260 MB)", 0.45)]
    [InlineData("    100% (260 / 260 MB)", 1.0)]
    [InlineData("  model.bin: ダウンロード中", null)]
    [InlineData("  model.bin: 取得済み", null)]
    public void ParsesDownloadProgress(string line, double? expected) => Assert.Equal(expected, ModelCatalog.ParseProgress(line));

    [Fact]
    public void DownloadCommandUsesOnlyAndNoShell()
    {
        var psi = ModelCatalog.DownloadCommand(ModelCatalog.Find("fugumt-en-ja")!, _dir);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(["-u", Path.Combine(PythonWorker.ScriptDir, "download_models.py"), _dir, "--only", "fugumt-en-ja"], psi.ArgumentList);
    }
}
