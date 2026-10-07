using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GetText.Plugins.DocumentOcr;

/// <summary>読んだ 1 ページ (画像なら 1 枚)。</summary>
public sealed record DocumentPage(int Number, int Width, int Height, OcrResult Result);

/// <summary>書き出しの形式。</summary>
public enum DocumentFormat
{
    Text,
    Markdown,
    Json,
}

/// <summary>読んだ文書を書き出す (TXT・Markdown・JSON)。文字は UTF-8 (BOM なし)。</summary>
public static class DocumentExport
{
    public static string Extension(DocumentFormat format) => format switch
    {
        DocumentFormat.Markdown => "md",
        DocumentFormat.Json => "json",
        _ => "txt",
    };

    public static string Write(string sourceName, IReadOnlyList<DocumentPage> pages, DocumentFormat format, DateTimeOffset created) => format switch
    {
        DocumentFormat.Markdown => Markdown(sourceName, pages),
        DocumentFormat.Json => Json(sourceName, pages, created),
        _ => Text(pages),
    };

    /// <summary>ページが 2 つ以上なら「--- 1 ページ ---」で区切る。</summary>
    public static string Text(IReadOnlyList<DocumentPage> pages)
    {
        var sb = new StringBuilder();
        foreach (var page in pages)
        {
            if (pages.Count > 1)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append($"--- {page.Number} ページ ---\n");
            }
            foreach (var line in page.Result.Lines) sb.Append(line.Text).Append('\n');
        }
        return sb.ToString();
    }

    public static string Markdown(string sourceName, IReadOnlyList<DocumentPage> pages)
    {
        var sb = new StringBuilder($"# {EscapeMarkdown(sourceName)}\n");
        foreach (var page in pages)
        {
            sb.Append('\n');
            if (pages.Count > 1) sb.Append($"## {page.Number} ページ\n\n");
            if (page.Result.Lines.Count == 0)
            {
                sb.Append("(文字は見つかりませんでした)\n");
                continue;
            }
            // 1 行ずつ改行する (行末の 2 つの空白は Markdown の改行)
            foreach (var line in page.Result.Lines) sb.Append(EscapeMarkdown(line.Text)).Append("  \n");
        }
        return sb.ToString();
    }

    public static string Json(string sourceName, IReadOnlyList<DocumentPage> pages, DateTimeOffset created)
    {
        var doc = new
        {
            source = sourceName,
            created = created.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            generator = "GetText",
            pages = pages.Select(p => new
            {
                page = p.Number,
                width = p.Width,
                height = p.Height,
                provider = p.Result.ProviderId,
                text = string.Join("\n", p.Result.Lines.Select(l => l.Text)),
                lines = p.Result.Lines.Select(l => new
                {
                    text = l.Text,
                    box = new[] { Math.Round(l.Left, 1), Math.Round(l.Top, 1), Math.Round(l.Right, 1), Math.Round(l.Bottom, 1) },
                    confidence = l.Confidence,
                }),
            }),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        }) + "\n";
    }

    /// <summary>行の頭の #・>・- など、Markdown の書式になってしまう文字の前に \ を付ける。</summary>
    public static string EscapeMarkdown(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '|') sb.Append('\\');
            sb.Append(c);
        }
        var s = sb.ToString();
        // 行の頭の「- 」「1. 」は箇条書きになるので崩す
        if (s.StartsWith("- ", StringComparison.Ordinal) || s.StartsWith("+ ", StringComparison.Ordinal)) s = "\\" + s;
        int dot = s.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0 && dot <= 9 && s[..dot].All(char.IsAsciiDigit)) s = s[..dot] + "\\" + s[dot..];
        return s;
    }

    /// <summary>同じ名前のファイルがあれば「名前 (2).txt」のようにする。</summary>
    public static string UniquePath(string folder, string baseName, string extension)
    {
        var path = Path.Combine(folder, $"{baseName}.{extension}");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{baseName} ({n}).{extension}");
        return path;
    }
}
