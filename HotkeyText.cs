using System.Windows.Input;

namespace GetText;

/// <summary>どのアプリを使っていても効くキー (グローバルホットキー) の種類・既定のキー・文字との変換。</summary>
public static class HotkeyText
{
    /// <summary>(設定の名前, 説明, 既定のキー)。</summary>
    public static readonly (string Id, string Action, string Default)[] Actions =
    [
        ("copy-ocr", "原文をコピー", "Ctrl+Alt+C"),
        ("copy-translation", "日本語訳をコピー", "Ctrl+Alt+T"),
        ("refresh", "今すぐ読み取る", "Ctrl+Alt+R"),
        ("pause", "一時停止 / 再開", "Ctrl+Alt+P"),
        ("quick-ocr", "範囲を選んで文字をコピー (Quick OCR)", "Ctrl+Alt+Q"),
    ];

    /// <summary>設定のキー (無い・読めなければ既定)。</summary>
    public static string For(AppSettings settings, string id)
    {
        var fallback = Actions.First(a => a.Id == id).Default;
        return settings.Hotkeys is { } map && map.TryGetValue(id, out var keys) && TryParse(keys, out _, out _) ? keys! : fallback;
    }

    /// <summary>「Ctrl+Alt+C」のような文字を、修飾キーとキーにする。Ctrl か Alt を含まなければ false (普段の入力とぶつかるため)。</summary>
    public static bool TryParse(string? text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (key != Key.None || !Enum.TryParse(part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : part, true, out key)) return false;
                    break;
            }
        }
        return key != Key.None && (modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Alt)) && !IsReserved(modifiers, key);
    }

    /// <summary>
    /// Windows やどのアプリでも決まった働きのあるキー (Alt+F4 で閉じる・Ctrl+C でコピー など)。登録するとすべてのアプリでその働きを奪うので使わない。
    /// F1〜F24 以外は、修飾キーを 2 つ以上 (Ctrl+Alt・Ctrl+Shift など) にする (Ctrl+英字・Alt+英字はアプリのコピー・メニューに使われる)。
    /// </summary>
    public static bool IsReserved(ModifierKeys modifiers, Key key) =>
        (Modifiers(modifiers) < 2 && key is not (>= Key.F1 and <= Key.F24))
        || (modifiers.HasFlag(ModifierKeys.Alt) && key is Key.F4 or Key.Space or Key.Tab or Key.Escape)
        || (modifiers.HasFlag(ModifierKeys.Control) && key == Key.Escape)
        || (modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Alt) && key == Key.Delete);

    private static int Modifiers(ModifierKeys m) =>
        (m.HasFlag(ModifierKeys.Control) ? 1 : 0) + (m.HasFlag(ModifierKeys.Alt) ? 1 : 0) + (m.HasFlag(ModifierKeys.Shift) ? 1 : 0) + (m.HasFlag(ModifierKeys.Windows) ? 1 : 0);

    /// <summary>修飾キーとキーを「Ctrl+Alt+C」のような文字にする。</summary>
    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var name = key.ToString();
        parts.Add(name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]) ? name[1..] : name);
        return string.Join("+", parts);
    }

    /// <summary>画面に出すときの形 (「Ctrl + Alt + C」)。</summary>
    public static string Display(string keys) => keys.Replace("+", " + ");
}
