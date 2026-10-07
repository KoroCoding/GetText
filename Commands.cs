using System.Globalization;
using System.Text;

namespace GetText;

/// <summary>今どの画面を使っているか (コマンドの一覧で、関係のあるものを上に出す)。</summary>
public enum CommandContext
{
    Any,
    Home,
    ScreenOcr,
    Meeting,
    Recording,
    Settings,
}

/// <summary>コマンドの種類 (コマンドの一覧の見出し)。</summary>
public enum CommandCategory
{
    Feature,
    Action,
    Recent,
    Settings,
    Extension,
}

/// <summary>
/// コマンドの一覧 (Ctrl+K・⌘K) から実行できる操作。GetText の機能も拡張機能 (プラグイン) も同じ形で登録する
/// (表示は GetText が行うので、拡張機能が増えても見た目はそろう)。
/// </summary>
public sealed class AppCommand
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>一覧で名前の右に小さく出す説明 (短く)。</summary>
    public string? Subtitle { get; init; }
    /// <summary>検索に使う別の言い方 (英語の名前など)。</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public CommandCategory Category { get; init; } = CommandCategory.Action;
    public IconSource Icon { get; init; } = AppIcon.Command;
    /// <summary>キーの表示 (「Ctrl+F」など。無ければ null)。</summary>
    public string? Shortcut { get; init; }
    /// <summary>この画面を使っているときは上に出す。</summary>
    public IReadOnlyList<CommandContext> Contexts { get; init; } = [];
    /// <summary>今使えるか (使えないものは一覧に出さない)。</summary>
    public Func<bool> IsAvailable { get; init; } = () => true;
    public required Action Execute { get; init; }
    /// <summary>登録した拡張機能の名前 (GetText の機能なら null)。</summary>
    public string? Publisher { get; init; }
}

/// <summary>コマンドの登録先。拡張機能は Register で加え、Unregister で外す。</summary>
public sealed class CommandRegistry
{
    private readonly List<AppCommand> _commands = [];
    private readonly Dictionary<string, DateTime> _used = [];

    public event Action? Changed;

    public IReadOnlyList<AppCommand> All => _commands;

    /// <summary>コマンドを加える (同じ Id があれば置き換える)。名前に絵文字は使えない (アイコンで示す)。</summary>
    public void Register(AppCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Id) || string.IsNullOrWhiteSpace(command.Title))
            throw new ArgumentException("コマンドには Id と名前が必要です");
        if (IconValidator.HasPictograph(command.Title))
            throw new ArgumentException($"コマンドの名前に絵文字は使えません: {command.Title}");
        _commands.RemoveAll(c => c.Id == command.Id);
        _commands.Add(command);
        Changed?.Invoke();
    }

    public void Unregister(string id)
    {
        if (_commands.RemoveAll(c => c.Id == id) > 0) Changed?.Invoke();
    }

    /// <summary>実行した (最近使ったものとして上に出す)。</summary>
    public void MarkUsed(string id, DateTime? at = null) => _used[id] = at ?? DateTime.Now;

    public bool TryRun(string id)
    {
        var command = _commands.FirstOrDefault(c => c.Id == id);
        if (command == null || !command.IsAvailable()) return false;
        MarkUsed(id);
        command.Execute();
        return true;
    }

    /// <summary>検索した結果を、合う順に返す (query が空なら、今の画面に関係するもの・最近使ったものを上に)。</summary>
    public List<AppCommand> Search(string query, CommandContext context, int max = 50, DateTime? now = null) =>
        CommandSearch.Rank(_commands.Where(c => c.IsAvailable()), query, context, _used, now ?? DateTime.Now)
            .Take(max)
            .ToList();
}

/// <summary>コマンドの検索と並べ替え (ひらがな・カタカナ、全角・半角、大文字・小文字の違いは無視する)。</summary>
public static class CommandSearch
{
    public static IEnumerable<AppCommand> Rank(IEnumerable<AppCommand> commands, string query, CommandContext context,
        IReadOnlyDictionary<string, DateTime> used, DateTime now)
    {
        var tokens = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scored = new List<(AppCommand Command, double Score, int Order)>();
        int order = 0;
        foreach (var command in commands)
        {
            order++;
            double score = tokens.Length == 0 ? 0 : Score(command, tokens);
            if (tokens.Length > 0 && score <= 0) continue;
            if (context != CommandContext.Any && command.Contexts.Contains(context)) score += tokens.Length == 0 ? 30 : 12;
            if (used.TryGetValue(command.Id, out var at))
            {
                double hours = Math.Max(0, (now - at).TotalHours);
                score += (tokens.Length == 0 ? 20 : 8) / (1 + hours / 24);
            }
            scored.Add((command, score, order));
        }
        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => tokens.Length == 0 ? (int)x.Command.Category : 0)
            .ThenBy(x => x.Order)
            .Select(x => x.Command);
    }

    /// <summary>すべての語が名前・別名・説明のどれかに合えば、その合い方の点数の合計。どれかの語が合わなければ 0。</summary>
    public static double Score(AppCommand command, string[] tokens)
    {
        var title = Normalize(command.Title);
        var keywords = command.Keywords.Select(Normalize).ToList();
        var subtitle = Normalize(command.Subtitle ?? "");
        var category = Normalize(CategoryName(command.Category));
        double total = 0;
        foreach (var token in tokens)
        {
            double best = Field(title, token) * 1.0;
            foreach (var k in keywords) best = Math.Max(best, Field(k, token) * 0.85);
            best = Math.Max(best, Field(subtitle, token) * 0.5);
            best = Math.Max(best, Field(category, token) * 0.4);
            if (best <= 0) return 0;
            total += best;
        }
        return total;
    }

    private static double Field(string text, string token)
    {
        if (text.Length == 0) return 0;
        if (text == token) return 100;
        if (text.StartsWith(token, StringComparison.Ordinal)) return 85;
        int at = text.IndexOf(token, StringComparison.Ordinal);
        if (at > 0) return text[at - 1] is ' ' or '-' or '・' or '/' ? 70 : 55;
        // 飛び飛びに合う (「sco」→「Screen OCR」)。まとまって合うほど高い
        int ti = 0, runs = 0;
        bool inRun = false;
        foreach (char c in text)
        {
            if (ti < token.Length && c == token[ti])
            {
                if (!inRun) runs++;
                inRun = true;
                ti++;
            }
            else inRun = false;
        }
        if (ti < token.Length || token.Length < 2) return 0;
        return Math.Max(10, 40 - runs * 6);
    }

    public static string CategoryName(CommandCategory category) => category switch
    {
        CommandCategory.Feature => "機能",
        CommandCategory.Recent => "最近",
        CommandCategory.Settings => "設定",
        CommandCategory.Extension => "拡張機能",
        _ => "操作",
    };

    /// <summary>比べるための形にする (小文字・半角・カタカナをひらがなに)。</summary>
    public static string Normalize(string text)
    {
        var folded = text.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        var sb = new StringBuilder(folded.Length);
        foreach (char c in folded)
            sb.Append(c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : char.IsWhiteSpace(c) ? ' ' : c);
        return sb.ToString().Trim();
    }
}

/// <summary>
/// ホームに並べる機能 (GetText の機能も拡張機能も同じ形)。GetText がこの情報から行を描く (拡張機能は独自の画面を描かない)。
/// </summary>
public sealed class FeatureInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>1 行の説明。</summary>
    public required string Description { get; init; }
    public IconSource Icon { get; init; } = AppIcon.Extensions;
    /// <summary>使える状態か (入れていない機能は「追加」を出す)。</summary>
    public Func<bool> IsInstalled { get; init; } = () => true;
    /// <summary>入れていないときに出す大きさ (「約 4 GB」など)。</summary>
    public string? DownloadSize { get; init; }
    /// <summary>今の状態 (「記録中」など。開いていなければ null)。</summary>
    public Func<FeatureStatus> Status { get; init; } = () => FeatureStatus.Closed;
    public required Action Open { get; init; }
    /// <summary>閉じる (開いているときにホームの「×」で)。null なら閉じるボタンを出さない。</summary>
    public Action? Close { get; init; }
    /// <summary>入れる (セットアップを開く)。null なら追加できない。</summary>
    public Action? Install { get; init; }
    /// <summary>PC の中だけで動く (送信しない) か。</summary>
    public bool Local { get; init; } = true;
    public string? Publisher { get; init; }
}

public enum FeatureState
{
    Closed,
    Open,
    Active,
    NotInstalled,
}

/// <summary>機能の状態と、その説明 (「開いています」「録画中」など。色だけでなく文字でも伝える)。</summary>
public sealed record FeatureStatus(FeatureState State, string? Label = null)
{
    public static readonly FeatureStatus Closed = new(FeatureState.Closed);
}
