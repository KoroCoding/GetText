using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace GetText;

/// <summary>AI のモデル 1 つ (offline/download_models.py の MODELS と同じ名前・ファイル)。</summary>
public sealed record ModelInfo(
    string Id,
    string Name,
    string Purpose,
    string License,
    bool NonCommercial,
    IReadOnlyList<string> Files);

public enum ModelState
{
    /// <summary>必要なファイルがそろっている。</summary>
    Installed,
    /// <summary>一部だけある (途中で止まった・消しかけ)。</summary>
    Partial,
    Missing,
}

/// <summary>モデルの今の状態 (画面に出す)。</summary>
public sealed record ModelStatus(ModelInfo Model, ModelState State, long Bytes, IReadOnlyList<string> UsedByPlugins)
{
    public string StateLabel => State switch
    {
        ModelState.Installed => "入っています",
        ModelState.Partial => "途中まで",
        _ => "入っていません",
    };

    public string SizeLabel => Bytes > 0 ? PluginCatalog.FormatSize(Bytes) : "";
}

/// <summary>
/// AI のモデルの一覧と状態 (Windows 版・Mac 版で共有)。置き場所は %LOCALAPPDATA%\GetText\offline\models (Mac も同じ構成)。
/// 入れるときは offline/download_models.py を使う (版と SHA-256 を固定。取得済みのファイルはダウンロードしない)。
/// 「セットアップを実行」(フル・標準・最小) は今までどおり使える。ここでは 1 つずつ入れる・消すができる。
/// </summary>
public static class ModelCatalog
{
    public static string Folder => Path.Combine(PythonWorker.Root, "models");

    public static readonly ModelInfo[] All =
    [
        new("fugumt-en-ja", "FuguMT (英語 → 日本語)", "翻訳: 英語", "CC BY-SA 4.0", false,
            ["config.json", "model.bin", "shared_vocabulary.json", "source.spm", "target.spm"]),
        new("nllb-200-1.3B", "NLLB-200 1.3B (約 200 言語 → 日本語)", "翻訳: 英語以外の言語", "CC BY-NC 4.0", true,
            ["config.json", "model.bin", "shared_vocabulary.json", "tokenizer.json"]),
        new("ppocrv6-rec-medium", "PP-OCRv6 rec medium", "文字の読み取り: 小さい・ぼやけた文字の読み直し", "Apache-2.0", false,
            ["PP-OCRv6_rec_medium.onnx"]),
        new("kotoba-whisper-v2.0", "kotoba-whisper v2.0", "議事録: 日本語の音声認識 (正確)", "MIT", false,
            ["config.json", "model.bin", "preprocessor_config.json", "tokenizer.json", "vocabulary.json"]),
        new("reazonspeech-k2-v2", "ReazonSpeech k2 v2", "議事録: 日本語の音声認識 (GPU が無くても速い)", "Apache-2.0", false,
            ["encoder-epoch-99-avg-1.int8.onnx", "decoder-epoch-99-avg-1.int8.onnx", "joiner-epoch-99-avg-1.int8.onnx", "tokens.txt"]),
        new("whisper-large-v3-turbo", "Whisper large-v3 turbo", "議事録: 日本語以外の音声認識と言語の判定", "MIT", false,
            ["config.json", "model.bin", "preprocessor_config.json", "tokenizer.json", "vocabulary.json"]),
        new("speaker-campplus", "3D-Speaker CAM++", "議事録: 話者の聞き分け", "Apache-2.0", false,
            ["model.onnx"]),
        new("qwen3-4b-instruct", "Qwen3-4B-Instruct-2507", "議事録: 前後の文脈による聞き間違いの補正・要約", "Apache-2.0", false,
            ["config.json", "generation_config.json", "model.bin", "tokenizer.json", "tokenizer_config.json", "vocabulary.json"]),
    ];

    public static ModelInfo? Find(string id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>モデルを入れるための Python (セットアップで入る) があるか。</summary>
    public static bool RuntimeInstalled => File.Exists(PythonWorker.PythonPath);

    /// <summary>モデルの状態。plugins は「このモデルを使う」と書いた拡張機能 (manifest の models)。</summary>
    public static ModelStatus Status(ModelInfo model, string? folder = null, IEnumerable<PluginManifest>? plugins = null)
    {
        var dir = Path.Combine(folder ?? Folder, model.Id);
        int present = 0;
        long bytes = 0;
        bool partFiles = false;
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    bytes += f.Length;
                    if (f.Name.EndsWith(".part", StringComparison.Ordinal)) partFiles = true;
                }
                present = model.Files.Count(name => File.Exists(Path.Combine(dir, name)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読めなければ「入っていない」と同じに扱う (入れ直せば直る)
        }
        var state = present == model.Files.Count && !partFiles ? ModelState.Installed
            : present > 0 || partFiles || bytes > 0 ? ModelState.Partial
            : ModelState.Missing;
        var users = (plugins ?? []).Where(p => p.Models.Contains(model.Id)).Select(p => p.Name.For("ja")).ToList();
        return new ModelStatus(model, state, bytes, users);
    }

    /// <summary>すべてのモデルの状態 (動いている拡張機能が使うと書いたものも)。</summary>
    public static List<ModelStatus> List(string? folder = null, IEnumerable<PluginManifest>? plugins = null)
    {
        var list = plugins?.ToList() ?? [];
        return All.Select(m => Status(m, folder, list)).ToList();
    }

    /// <summary>モデルの一覧の見本 (入っている・途中まで・入っていない・非商用・拡張機能が使う)。</summary>
    public static IReadOnlyList<ModelStatus> Demo()
    {
        var states = new Dictionary<string, (ModelState State, long Bytes, string[] Users)>
        {
            ["fugumt-en-ja"] = (ModelState.Installed, 312L << 20, []),
            ["ppocrv6-rec-medium"] = (ModelState.Installed, 78L << 20, []),
            ["kotoba-whisper-v2.0"] = (ModelState.Installed, 1516L << 20, []),
            ["reazonspeech-k2-v2"] = (ModelState.Partial, 96L << 20, []),
            ["speaker-campplus"] = (ModelState.Installed, 28L << 20, []),
            ["qwen3-4b-instruct"] = (ModelState.Missing, 0, ["読み取りの要約"]),
        };
        return ModelCatalog.All.Select(m => states.TryGetValue(m.Id, out var s)
            ? new ModelStatus(m, s.State, s.Bytes, s.Users)
            : new ModelStatus(m, ModelState.Missing, 0, [])).ToList();
    }

    /// <summary>ライセンスの注意 (非商用のものは目立たせる)。</summary>
    public static string LicenseNote(ModelInfo model) => model.NonCommercial
        ? $"{model.License} ・ 非商用に限る。仕事で使う前にライセンスを確認してください"
        : model.License;

    /// <summary>1 つだけ入れる (download_models.py --only)。取得済みのファイルは SHA-256 を確かめて、ダウンロードしない。</summary>
    public static ProcessStartInfo DownloadCommand(ModelInfo model, string? folder = null)
    {
        var psi = new ProcessStartInfo(PythonWorker.PythonPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(Path.Combine(PythonWorker.ScriptDir, "download_models.py"));
        psi.ArgumentList.Add(folder ?? Folder);
        psi.ArgumentList.Add("--only");
        psi.ArgumentList.Add(model.Id);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        return psi;
    }

    private static readonly Regex Percent = new(@"^\s*(\d{1,3})% \(", RegexOptions.Compiled);

    /// <summary>download_models.py の進み具合の行 ("  45% (120 / 260 MB)") を 0〜1 に。それ以外は null。</summary>
    public static double? ParseProgress(string line) =>
        Percent.Match(line) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var p) && p <= 100 ? p / 100.0 : null;

    /// <summary>入れる (時間がかかる。利用者が押したときだけ)。失敗したら理由の最後の数行を付けて例外。</summary>
    public static async Task InstallAsync(ModelInfo model, IProgress<double>? progress, CancellationToken ct, string? folder = null)
    {
        if (!RuntimeInstalled) throw new InvalidOperationException("モデルを入れるには、先に「セットアップを実行」で AI の機能の環境を入れてください");
        Directory.CreateDirectory(folder ?? Folder);
        using var process = Process.Start(DownloadCommand(model, folder)) ?? throw new InvalidOperationException("ダウンロードを始められませんでした");
        var tail = new Queue<string>();
        void Keep(string line)
        {
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 6) tail.Dequeue();
            }
        }
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Keep(e.Data); };
        process.BeginErrorReadLine();
        using var kill = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            Keep(line);
            if (ParseProgress(line) is { } p) progress?.Report(p);
        }
        await process.WaitForExitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            string detail;
            lock (tail) detail = string.Join(" / ", tail.Select(s => s.Trim()).Where(s => s.Length > 0).TakeLast(3));
            throw new InvalidOperationException($"「{model.Name}」を入れられませんでした ({PluginLog.Redact(detail)})");
        }
        progress?.Report(1);
    }

    /// <summary>消す (使っている最中で消せなければ、理由を伝える)。</summary>
    public static void Remove(ModelInfo model, string? folder = null)
    {
        var dir = Path.Combine(folder ?? Folder, model.Id);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"「{model.Name}」は使用中のため消せませんでした。GetText を再起動してから、もう一度消してください。", ex);
        }
    }
}

/// <summary>モデルの一覧の文と色 (Windows 版・Mac 版で同じものを出す)。</summary>
public static class ModelTexts
{
    public static string Note(bool runtimeInstalled) => runtimeInstalled
        ? "1 つずつ入れる・消すことができます。配布元 (Hugging Face など) から版を固定してダウンロードし、SHA-256 を確かめます。取得済みのファイルはダウンロードしません。"
        : "モデルを 1 つずつ入れるには、先に上の「セットアップを実行」で AI の機能の環境を入れてください。";

    public static string Meta(ModelStatus s)
    {
        var parts = new List<string> { ModelCatalog.LicenseNote(s.Model) };
        if (s.SizeLabel.Length > 0) parts.Add(s.SizeLabel);
        if (s.UsedByPlugins.Count > 0) parts.Add("使っている拡張機能: " + string.Join("・", s.UsedByPlugins));
        return string.Join(" ・ ", parts);
    }

    public static string InstallLabel(ModelStatus s) => s.State == ModelState.Partial ? "続きを入れる" : "入れる";

    public static string AccessibleName(ModelStatus s) => $"{s.Model.Name}、{s.StateLabel}、{s.Model.Purpose}";

    public static string ConfirmInstall(ModelStatus s) =>
        $"「{s.Model.Name}」を入れます ({s.Model.Purpose})。\n\nライセンス: {ModelCatalog.LicenseNote(s.Model)}\n\n配布元からダウンロードします (取得済みのファイルは省きます)。入れますか？";

    public static string ConfirmRemove(ModelStatus s) =>
        $"「{s.Model.Name}」を消しますか？\n\n{s.Model.Purpose} が使えなくなります。あとで入れ直せます。"
        + (s.UsedByPlugins.Count > 0 ? $"\n\n使っている拡張機能: {string.Join("・", s.UsedByPlugins)}" : "");

    public static uint StateBackground(ModelState state, Palette p) => state switch
    {
        ModelState.Installed => p.SuccessSubtle,
        ModelState.Partial => p.WarningSubtle,
        _ => p.SurfaceSecondary,
    };

    public static uint StateForeground(ModelState state, Palette p) => state switch
    {
        ModelState.Installed => p.Success,
        ModelState.Partial => p.Warning,
        _ => p.TextSecondary,
    };

    /// <summary>非商用のライセンスのものは、注意の色で示す (文字でも「非商用」と書く)。</summary>
    public static uint MetaForeground(ModelInfo m, Palette p) => m.NonCommercial ? p.Warning : p.TextSecondary;
}