using System.Text;

namespace GetText;

/// <summary>1 行分の OCR 結果。座標はキャプチャ元画像のピクセル。</summary>
public sealed record OcrLineData(IReadOnlyList<string> Words, double Left, double Top, double Right, double Bottom)
{
    public string Compact => string.Concat(Words);
}

/// <param name="IsHeading">まとまりごとに表示するときの見出し (【1】など。画面の上の文字ではない)。</param>
public sealed record OcrLineInfo(string Text, double Left, double Top, double Right, double Bottom, bool IsHeading = false)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

/// <summary>OCR 結果を段落 → 行 の構造で持つ。</summary>
public sealed class OcrDocument
{
    public List<List<OcrLineInfo>> Paragraphs { get; } = [];

    public static OcrDocument From(IEnumerable<OcrLineData> lines, bool joinCjk, bool correct)
    {
        var doc = new OcrDocument();
        List<OcrLineInfo>? paragraph = null;
        OcrLineInfo? prev = null;

        foreach (var line in lines)
        {
            var text = JoinWords(line.Words, joinCjk);
            if (correct) text = JapaneseCorrector.Correct(text);
            var info = new OcrLineInfo(text, line.Left, line.Top, line.Right, line.Bottom);
            if (info.Text.Length == 0) continue;

            // 行間が大きく空いていたら新しい段落
            if (paragraph == null || prev == null ||
                info.Top - prev.Bottom > Math.Max(prev.Height, info.Height) * 0.9)
            {
                paragraph = [];
                doc.Paragraphs.Add(paragraph);
            }
            paragraph.Add(info);
            prev = info;
        }
        return doc;
    }

    public bool IsEmpty => Paragraphs.Count == 0;

    /// <summary>行の並びにする。段落の区切りは null。</summary>
    public List<OcrLineInfo?> Flatten()
    {
        var items = new List<OcrLineInfo?>();
        foreach (var paragraph in Paragraphs)
        {
            if (items.Count > 0) items.Add(null);
            items.AddRange(paragraph);
        }
        return items;
    }

    /// <summary><see cref="Flatten"/> の逆。</summary>
    public static OcrDocument FromFlat(IEnumerable<OcrLineInfo?> items)
    {
        var doc = new OcrDocument();
        List<OcrLineInfo>? paragraph = null;
        foreach (var item in items)
        {
            if (item == null)
            {
                paragraph = null;
                continue;
            }
            if (paragraph == null)
            {
                paragraph = [];
                doc.Paragraphs.Add(paragraph);
            }
            paragraph.Add(item);
        }
        return doc;
    }

    /// <summary>表示する順の行 (<see cref="ToDisplayText"/> の空でない行と 1 対 1)。</summary>
    public IEnumerable<OcrLineInfo> Lines => Paragraphs.SelectMany(p => p);

    /// <summary>画面どおりの改行で並べた表示用テキスト。</summary>
    public string ToDisplayText() =>
        string.Join("\r\n\r\n", Paragraphs.Select(p => string.Join("\r\n", p.Select(l => l.Text))));

    /// <summary>
    /// 翻訳用に、途中で折り返された行を 1 文につなげた単位を返す(段落ごと)。
    /// メニューや箇条書きのような短い行はつなげない。
    /// </summary>
    public List<List<string>> ToTranslationUnits()
    {
        var result = new List<List<string>>();
        foreach (var paragraph in Paragraphs)
        {
            double maxWidth = paragraph.Max(l => l.Width);
            var units = new List<string>();
            for (int i = 0; i < paragraph.Count; i++)
            {
                if (i > 0 && Continues(paragraph[i - 1], maxWidth))
                    units[^1] = TextScript.Join(units[^1], paragraph[i].Text);
                else
                    units.Add(paragraph[i].Text);
            }
            result.Add(units);
        }
        return result;
    }

    private static bool Continues(OcrLineInfo line, double maxWidth)
    {
        // 段落内で短い行や、メニュー項目のような数文字だけの行は文の途中ではない
        if (line.Width < maxWidth * 0.75 || line.Width < line.Height * 8) return false;
        char last = line.Text[^1];
        return last is not ('.' or '!' or '?' or ':' or ';' or '。' or '！' or '？' or '：');
    }

    public bool HasKana => Paragraphs.Sum(p => p.Sum(l => TextScript.CountKana(l.Text))) >= 3;

    // Windows OCR は日本語を 1 文字ずつ空白区切りで返すため、和文どうしの間の空白を詰める
    private static string JoinWords(IReadOnlyList<string> words, bool joinCjk)
    {
        var sb = new StringBuilder();
        foreach (var text in words)
        {
            if (text.Length == 0) continue;
            if (sb.Length > 0 && !(joinCjk && (TextScript.IsCjk(sb[^1]) || TextScript.IsCjk(text[0]))))
                sb.Append(' ');
            sb.Append(text);
        }
        return sb.ToString();
    }
}
