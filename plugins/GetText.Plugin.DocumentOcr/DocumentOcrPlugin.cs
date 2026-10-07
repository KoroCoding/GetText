namespace GetText.Plugins.DocumentOcr;

/// <summary>
/// 文書の文字の読み取り: 画像のファイル・PDF (スキャンしたものなど) の文字を読み、TXT・Markdown・JSON に書き出す。
/// いくつかのファイルや、フォルダの中をまとめて読める。読み取りは GetText の方式で、この PC の中で行う。
/// </summary>
public sealed class DocumentOcrPlugin : IGetTextPlugin
{
    private const string FormatKey = "format";
    private const string DpiKey = "dpi";
    private const int MaxImageSize = 4000;

    private IPluginContext? _context;
    private bool _busy;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.AddSettings(new PluginSettingsPage
        {
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "文書の文字の読み取り", ["en"] = "Document OCR" }),
            Items =
            [
                new PluginSetting
                {
                    Key = FormatKey,
                    Label = "書き出す形式",
                    Kind = PluginSettingKind.Choice,
                    Default = "txt",
                    Choices = [("txt", "テキスト (.txt)"), ("md", "Markdown (.md)"), ("json", "JSON (.json。行の位置つき)")],
                },
                new PluginSetting
                {
                    Key = DpiKey,
                    Label = "PDF を読むときの細かさ",
                    Description = "細かいほど小さな文字も読めますが、時間がかかります。",
                    Kind = PluginSettingKind.Choice,
                    Default = "200",
                    Choices = [("150", "普通 (150 dpi)"), ("200", "細かい (200 dpi)"), ("300", "とても細かい (300 dpi)")],
                    Advanced = true,
                },
            ],
        });
        context.AddFeature(new PluginFeature
        {
            Id = "document",
            Name = new LocalizedText(new Dictionary<string, string> { ["ja"] = "文書の文字の読み取り", ["en"] = "Document OCR" }),
            Description = "PDF や画像のファイルの文字を読み、テキストにします",
            Icon = "Document",
            Status = () => _busy ? new PluginFeatureStatus(PluginFeatureState.Active, "読んでいます") : new PluginFeatureStatus(PluginFeatureState.Closed),
            Open = ct => PickFilesAsync(ct),
        });
        context.AddCommand(new PluginCommand
        {
            Id = "files",
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "PDF・画像の文字を読む (ファイルを選ぶ)", ["en"] = "OCR PDF / image files" }),
            Keywords = ["pdf", "ocr", "scan", "image", "batch", "スキャン", "画像", "まとめて"],
            Icon = "Document",
            IsAvailable = () => !_busy,
            Execute = PickFilesAsync,
        });
        context.AddCommand(new PluginCommand
        {
            Id = "folder",
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "フォルダの PDF・画像の文字をまとめて読む", ["en"] = "OCR all PDF / images in a folder" }),
            Keywords = ["pdf", "ocr", "batch", "folder", "フォルダ", "まとめて", "一括"],
            Icon = "Folder",
            IsAvailable = () => !_busy,
            Execute = PickFolderAsync,
        });
    }

    private (IPluginUi Ui, IDocumentService Documents, IOcrService Ocr)? Services()
    {
        var context = _context!;
        var ui = context.GetService<IPluginUi>();
        var documents = context.GetService<IDocumentService>();
        var ocr = context.GetService<IOcrService>();
        if (ui != null && documents != null && ocr != null) return (ui, documents, ocr);
        context.Notify(new PluginNotification("文書を読めません", "この GetText では、ファイルの文字を読む部品が使えません。", PluginNotificationKind.Error));
        return null;
    }

    private async Task PickFilesAsync(CancellationToken ct)
    {
        if (_busy || Services() is not { } s) return;
        var extensions = s.Documents.ImageExtensions.Append("pdf").ToArray();
        var files = await s.Ui.PickFilesAsync("文字を読むファイル", [("PDF・画像", extensions)], multiple: true, ct);
        if (files.Count > 0) await RunAsync(s, files, ct);
    }

    private async Task PickFolderAsync(CancellationToken ct)
    {
        if (_busy || Services() is not { } s) return;
        var folder = await s.Ui.PickFolderAsync("文字を読むファイルのあるフォルダ", ct);
        if (folder == null) return;
        var files = Supported(Directory.EnumerateFiles(folder), s.Documents).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (files.Count == 0)
        {
            _context!.Notify(new PluginNotification("読めるファイルがありません", "このフォルダに PDF・画像のファイルがありません (中のフォルダは見ません)。", PluginNotificationKind.Warning));
            return;
        }
        await RunAsync(s, files, ct);
    }

    public static IEnumerable<string> Supported(IEnumerable<string> files, IDocumentService documents) =>
        files.Where(f => Path.GetExtension(f).TrimStart('.').ToLowerInvariant() is var ext && (ext == "pdf" || documents.ImageExtensions.Contains(ext)));

    private async Task RunAsync((IPluginUi Ui, IDocumentService Documents, IOcrService Ocr) s, IReadOnlyList<string> files, CancellationToken ct)
    {
        var context = _context!;
        var output = await s.Ui.PickFolderAsync("書き出す先のフォルダ", ct);
        if (output == null) return;
        var format = context.Settings.Get(FormatKey) switch { "md" => DocumentFormat.Markdown, "json" => DocumentFormat.Json, _ => DocumentFormat.Text };
        double dpi = double.TryParse(context.Settings.Get(DpiKey), out var d) ? Math.Clamp(d, 72, 400) : 200;

        _busy = true;
        using var progress = s.Ui.BeginProgress(files.Count == 1 ? $"「{Path.GetFileName(files[0])}」の文字を読んでいます" : $"{files.Count} 個のファイルの文字を読んでいます");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, progress.CancellationToken);
        var token = linked.Token;
        var written = new List<string>();
        var failed = new List<string>();
        try
        {
            // 先にページの数を数える (進み具合を出すため)
            var plan = new List<(string File, int Pages)>();
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    plan.Add((file, IsPdf(file) ? await s.Documents.GetPdfPageCountAsync(file, token) : 1));
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
            int total = Math.Max(1, plan.Sum(p => p.Pages)), done = 0;
            foreach (var (file, pageCount) in plan)
            {
                var pages = new List<DocumentPage>();
                try
                {
                    for (int i = 0; i < pageCount; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        progress.Report((double)done / total, pageCount > 1 ? $"{Path.GetFileName(file)} ・ {i + 1} / {pageCount} ページ" : Path.GetFileName(file));
                        var image = IsPdf(file)
                            ? await s.Documents.RenderPdfPageAsync(file, i, dpi, MaxImageSize, token)
                            : await s.Documents.LoadImageAsync(file, MaxImageSize, token);
                        var result = await s.Ocr.RecognizeAsync(image.Bgra, image.Width, image.Height, null, token);
                        pages.Add(new DocumentPage(i + 1, image.Width, image.Height, result));
                        done++;
                    }
                    var name = Path.GetFileName(file);
                    var target = DocumentExport.UniquePath(output, Path.GetFileNameWithoutExtension(file), DocumentExport.Extension(format));
                    await File.WriteAllTextAsync(target, DocumentExport.Write(name, pages, format, DateTimeOffset.Now), new System.Text.UTF8Encoding(false), token);
                    written.Add(target);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
                    done += pageCount - pages.Count;
                }
            }
            progress.Complete(failed.Count == 0 ? $"{written.Count} 個のファイルを書き出しました" : $"{written.Count} 個を書き出し、{failed.Count} 個は読めませんでした", failed.Count > 0 && written.Count == 0);
        }
        catch (OperationCanceledException)
        {
            progress.Complete($"やめました ({written.Count} 個は書き出し済み)");
        }
        finally
        {
            _busy = false;
        }
        context.Log.Info($"文書を読みました ({written.Count} 個・読めなかったもの {failed.Count} 個)"); // (ファイルの名前・中身は書かない)
        if (written.Count > 0)
            context.Notify(new PluginNotification($"{written.Count} 個のファイルの文字を書き出しました",
                failed.Count > 0 ? $"読めなかったもの: {string.Join("、", failed.Take(3))}" : Path.GetFileName(written[0]),
                failed.Count > 0 ? PluginNotificationKind.Warning : PluginNotificationKind.Success,
                ActionLabel: "書き出した場所を開く", Action: () => s.Ui.Reveal(written.Count == 1 ? written[0] : output)));
        else if (failed.Count > 0)
            context.Notify(new PluginNotification("ファイルの文字を読めませんでした", string.Join("\n", failed.Take(3)), PluginNotificationKind.Error));
    }

    private static bool IsPdf(string file) => string.Equals(Path.GetExtension(file), ".pdf", StringComparison.OrdinalIgnoreCase);

    public void Shutdown()
    {
    }
}
