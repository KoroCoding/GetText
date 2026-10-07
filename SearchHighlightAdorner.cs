using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GetText;

/// <summary>
/// 原文の欄の上に、検索で見つかった所の印を描く。今の一致は濃い色と太い枠、ほかの一致は薄い色と細い枠
/// (色だけでなく枠の太さでも区別する)。文字の選択は使わない (選択中は読み取りの更新を止める決まりのため)。
/// </summary>
public sealed class SearchHighlightAdorner : Adorner
{
    private readonly TextBox _box;
    private IReadOnlyList<TextMatch> _matches = [];
    private int _active = -1;

    public SearchHighlightAdorner(TextBox box) : base(box)
    {
        _box = box;
        IsHitTestVisible = false;
    }

    public void SetMatches(IReadOnlyList<TextMatch> matches, int active)
    {
        _matches = matches;
        _active = active;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_matches.Count == 0 || _box.Text.Length == 0) return;
        var p = Theme.Colors;
        // 印は文字の上に重なるので、塗りは薄くして文字を読めるようにし、枠ではっきり示す
        var normalFill = new SolidColorBrush(Theme.ToColor(p.SearchHighlight)) { Opacity = 0.35 };
        var activeFill = new SolidColorBrush(Theme.ToColor(p.SearchHighlightActive)) { Opacity = 0.35 };
        var normalPen = new Pen(new SolidColorBrush(Theme.ToColor(p.SearchHighlightBorder)), 1);
        var activePen = new Pen(new SolidColorBrush(Theme.ToColor(p.SearchHighlightActiveBorder)), 2);
        // 欄の見えている範囲だけに描く (スクロールして外に出た所は描かない)
        var host = FindHost(_box);
        Rect clip = host != null
            ? host.TransformToAncestor(_box).TransformBounds(new Rect(0, 0, host.ActualWidth, host.ActualHeight))
            : new Rect(0, 0, _box.ActualWidth, _box.ActualHeight);
        dc.PushClip(new RectangleGeometry(clip));
        int drawn = 0;
        for (int m = 0; m < _matches.Count && drawn < 400; m++)
        {
            bool active = m == _active;
            foreach (var rect in Rects(_matches[m]))
            {
                if (!rect.IntersectsWith(clip)) continue;
                var r = rect;
                r.Inflate(1, 0);
                dc.DrawRoundedRectangle(active ? activeFill : normalFill, active ? activePen : normalPen, r, 2, 2);
                drawn++;
            }
        }
        dc.Pop();
    }

    // 一致した文字の範囲の四角 (折り返して複数の行にまたがるときは行ごと)
    private IEnumerable<Rect> Rects(TextMatch match)
    {
        int end = Math.Min(match.Start + match.Length, _box.Text.Length);
        Rect? current = null;
        for (int i = match.Start; i < end; i++)
        {
            Rect leading = _box.GetRectFromCharacterIndex(i);
            Rect trailing = _box.GetRectFromCharacterIndex(i, trailingEdge: true);
            if (leading.IsEmpty || trailing.IsEmpty) continue;
            var rect = new Rect(new Point(Math.Min(leading.Left, trailing.Left), leading.Top), new Point(Math.Max(leading.Left, trailing.Left), leading.Bottom));
            if (current is { } c && Math.Abs(c.Top - rect.Top) < 1)
                current = Rect.Union(c, rect);
            else
            {
                if (current is { } done) yield return done;
                current = rect;
            }
        }
        if (current is { } last) yield return last;
    }

    private static FrameworkElement? FindHost(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindHost(child) is { } found) return found;
        }
        return null;
    }
}
