using System.Text;

namespace GetText;

/// <summary>
/// 行の中の 1 文字・1 単語の位置 (OCR エンジンが返した位置。座標はキャプチャ元画像のピクセル)。
/// AI OCR (RapidOCR) は文字ごと、Windows OCR は単語ごと。位置を返さないエンジンでは無し (行全体で扱う)。
/// </summary>
public sealed record OcrSpan(string Text, double Left, double Top, double Right, double Bottom, double? Confidence = null)
{
    /// <summary>空白を除いた文字数 (表示の文字と位置を対応させるときの単位)。</summary>
    public int Glyphs => OcrSpans.CountGlyphs(Text);
}

/// <summary>1 行分の OCR 結果。座標はキャプチャ元画像のピクセル。</summary>
/// <param name="Spans">行の中の文字・単語の位置 (空白を除いた文字の順。無ければ null)。</param>
/// <param name="Confidence">行の確からしさ (0〜1。エンジンが返さなければ null)。</param>
public sealed record OcrLineData(IReadOnlyList<string> Words, double Left, double Top, double Right, double Bottom,
    IReadOnlyList<OcrSpan>? Spans = null, double? Confidence = null)
{
    public string Compact => string.Concat(Words);
}

/// <param name="IsHeading">まとまりごとに表示するときの見出し (【1】など。画面の上の文字ではない)。</param>
/// <param name="Spans">行の中の文字・単語の位置 (空白を除いた文字の数が Text と同じときだけ。違えば null)。</param>
public sealed record OcrLineInfo(string Text, double Left, double Top, double Right, double Bottom, bool IsHeading = false,
    IReadOnlyList<OcrSpan>? Spans = null)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

public static class OcrSpans
{
    /// <summary>空白を除いた文字数 (サロゲートペアは 1 文字)。</summary>
    public static int CountGlyphs(string text)
    {
        int n = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            n++;
        }
        return n;
    }

    /// <summary>位置の情報が、この文字列とそろっているときだけ返す (文字の補正などで数が変わったら使わない)。</summary>
    public static IReadOnlyList<OcrSpan>? IfMatching(IReadOnlyList<OcrSpan>? spans, string text) =>
        spans is { Count: > 0 } && spans.Sum(s => s.Glyphs) == CountGlyphs(text) ? spans : null;
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
            var info = new OcrLineInfo(text, line.Left, line.Top, line.Right, line.Bottom, Spans: OcrSpans.IfMatching(line.Spans, text));
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
