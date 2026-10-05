using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace GetText;

/// <summary>"#RRGGBB" をブラシにする。</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex && Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Gray;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 話者の色を、話者の名前の文字に使える色にする ([0] 話者の色 "#RRGGBB"、[1] テーマ)。テーマが変わると色も選び直される。
/// </summary>
public sealed class SpeakerTextBrushConverter : IMultiValueConverter
{
    public static readonly SpeakerTextBrushConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 0 || values[0] is not string hex || !Color.TryParse(hex, out var color)) return Brushes.Gray;
        bool dark = values.Count > 1 && values[1] is ThemeVariant v && v == ThemeVariant.Dark;
        return new SolidColorBrush(SpeakerColors.ForText(color, dark));
    }
}

/// <summary>割合 (0〜1) と欄の幅から、棒の長さを出す。</summary>
public sealed class ShareWidthConverter : IMultiValueConverter
{
    public static readonly ShareWidthConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count == 2 && values[0] is double share && values[1] is double width ? Math.Max(0, Math.Min(1, share)) * width : 0.0;
}

/// <summary>話者の色の見やすさの調整 (Windows 版と同じ計算)。</summary>
public static class SpeakerColors
{
    private static readonly Color LightBackground = Color.FromRgb(0xEA, 0xEA, 0xEA);
    private static readonly Color DarkBackground = Color.FromRgb(0x2D, 0x2D, 0x2D);

    /// <summary>背景との明るさの比が 4.5 以上になるまで、色合いはそのままでライトでは暗く・ダークでは明るくする。</summary>
    public static Color ForText(Color color, bool dark)
    {
        var background = dark ? DarkBackground : LightBackground;
        var hsl = color.ToHsl();
        double l = hsl.L;
        var result = color;
        for (int i = 0; i < 100 && Contrast(result, background) < 4.5; i++)
        {
            l = Math.Clamp(l + (dark ? 0.01 : -0.01), 0, 1);
            result = HslColor.ToRgb(hsl.H, hsl.S, l, 1);
        }
        return result;
    }

    public static double ContrastOnTheme(Color color, bool dark) => Contrast(color, dark ? DarkBackground : LightBackground);

    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static double Luminance(Color c)
    {
        static double Lin(byte v)
        {
            double x = v / 255.0;
            return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }
}

/// <summary>最初の案内の手順。</summary>
public sealed record GuideStep(int Number, string Title, string Detail);

/// <summary>準備中の表示の 1 段階 (pending → active → done、失敗したら failed)。</summary>
public sealed class LoadStep(string key, string title) : Observable
{
    private string _state = "pending";
    private DateTime? _started;
    private TimeSpan? _took;

    public string Key { get; } = key;
    public string Title { get; } = title;

    public string State
    {
        get => _state;
        private set
        {
            Set(ref _state, value);
            OnChanged(nameof(Icon));
            OnChanged(nameof(TimeText));
            OnChanged(nameof(IsActive));
            OnChanged(nameof(IsDone));
            OnChanged(nameof(IsFailed));
        }
    }

    public bool IsActive => _state == "active";
    public bool IsDone => _state == "done";
    public bool IsFailed => _state == "failed";

    public string Icon => _state switch
    {
        "active" => "◐",
        "done" => "✓",
        "failed" => "!",
        _ => "○",
    };

    /// <summary>かかった時間 (読み込み中は経過時間)。</summary>
    public string TimeText => _took is { } t ? $"{t.TotalSeconds:0.0} 秒"
        : _state == "active" && _started is { } s ? $"{(DateTime.Now - s).TotalSeconds:0} 秒" : "";

    public void Start()
    {
        if (_state != "pending") return;
        _started = DateTime.Now;
        State = "active";
    }

    public void Finish()
    {
        if (_state is "done" or "failed") return;
        _took = _started is { } s ? DateTime.Now - s : null;
        State = "done";
    }

    public void Fail()
    {
        _took = null;
        State = "failed";
    }

    public void Tick()
    {
        if (_state == "active") OnChanged(nameof(TimeText));
    }

    internal void SetDemo(string state, double seconds)
    {
        _started = DateTime.Now - TimeSpan.FromSeconds(seconds);
        _took = state == "done" ? TimeSpan.FromSeconds(seconds) : null;
        State = state;
    }
}

/// <summary>
/// 元の一覧 (source) のうち、条件 (filter) に合うものだけを同じ順で持つ一覧 (WPF の CollectionView の絞り込みの代わり)。
/// 元の一覧が変わったとき・項目の liveProperties が変わったときは自動で、それ以外は <see cref="Refresh"/> で並べ直す。
/// </summary>
public sealed class FilteredCollection<T> : ObservableCollection<T> where T : class, INotifyPropertyChanged
{
    private readonly ObservableCollection<T> _source;
    private readonly HashSet<string> _live;
    private readonly HashSet<T> _hooked = [];
    private Func<T, bool> _filter;

    public FilteredCollection(ObservableCollection<T> source, Func<T, bool> filter, params string[] liveProperties)
    {
        _source = source;
        _filter = filter;
        _live = [.. liveProperties];
        _source.CollectionChanged += OnSourceChanged;
        Refresh();
    }

    public Func<T, bool> Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            Refresh();
        }
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != null && _live.Contains(e.PropertyName)) Refresh();
    }

    /// <summary>条件に合うものを元の順で並べ直す (変わった所だけ足したり消したりする)。</summary>
    public void Refresh()
    {
        // 元の一覧から消えた項目の通知を外し、新しい項目に付ける
        var current = new HashSet<T>(_source);
        foreach (var gone in _hooked.Where(h => !current.Contains(h)).ToList())
        {
            gone.PropertyChanged -= OnItemChanged;
            _hooked.Remove(gone);
        }
        foreach (var item in _source)
            if (_hooked.Add(item)) item.PropertyChanged += OnItemChanged;

        var wanted = _source.Where(_filter).ToList();
        var wantedSet = new HashSet<T>(wanted);
        for (int i = Count - 1; i >= 0; i--)
            if (!wantedSet.Contains(this[i])) RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < Count && ReferenceEquals(this[i], wanted[i])) continue;
            int existing = IndexOf(wanted[i]);
            if (existing >= 0) Move(existing, i);
            else Insert(i, wanted[i]);
        }
        while (Count > wanted.Count) RemoveAt(Count - 1);
    }
}
