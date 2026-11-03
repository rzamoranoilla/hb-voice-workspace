using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace HB.Documents;

/// <summary>
/// Draws the matches of the find bar over the editor: every match in yellow, the current one in
/// orange. Only the visible lines are drawn, so long documents with many matches stay quick.
/// </summary>
internal sealed class FindHighlighter : Adorner
{
    public static readonly Brush MatchBrush = Frozen(Color.FromArgb(0x60, 0xFF, 0xD5, 0x4F));
    public static readonly Brush CurrentBrush = Frozen(Color.FromArgb(0xA0, 0xFF, 0x8C, 0x00));

    private readonly TextBox _box;
    private IReadOnlyList<int> _matches = [];
    private int _length;
    private int _current = -1;

    public FindHighlighter(TextBox box) : base(box)
    {
        _box = box;
        IsHitTestVisible = false;
        box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => InvalidateVisual()));
        box.SizeChanged += (_, _) => InvalidateVisual();
        box.IsVisibleChanged += (_, _) => InvalidateVisual();
        // The text is laid out after the TextChanged event: draw once that is done.
        box.TextChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, InvalidateVisual);
    }

    /// <summary>Shows the matches, <paramref name="current"/> being the start of the selected one (or -1).</summary>
    public void Show(IReadOnlyList<int> matches, int length, int current)
    {
        _matches = matches;
        _length = length;
        _current = current;
        InvalidateVisual();
    }

    public void Clear() => Show([], 0, -1);

    protected override void OnRender(DrawingContext dc)
    {
        if (_matches.Count == 0 || _length == 0 || !_box.IsVisible) return;
        // The adorner can be drawn while the text box is re-laying out its text: its line
        // information is then stale or empty (LineCount 0, visible lines beyond it) and asking
        // for those lines throws. Such a frame is skipped; the next layout pass redraws.
        int lineCount = _box.LineCount;
        int firstLine = _box.GetFirstVisibleLineIndex();
        int lastLine = Math.Min(_box.GetLastVisibleLineIndex(), lineCount - 1);
        if (lineCount <= 0 || firstLine < 0 || lastLine < firstLine) return;
        int textLength = _box.Text.Length;
        try
        {
            int from = _box.GetCharacterIndexFromLineIndex(firstLine);
            int to = _box.GetCharacterIndexFromLineIndex(lastLine) + _box.GetLineLength(lastLine);

            dc.PushClip(new RectangleGeometry(new Rect(_box.RenderSize)));
            for (int i = FirstMatchFrom(from - _length); i < _matches.Count && _matches[i] < to; i++)
            {
                if (_matches[i] + _length > textLength) break; // matches older than the text
                var brush = _matches[i] == _current ? CurrentBrush : MatchBrush;
                foreach (var rect in BoxesOf(_matches[i]))
                    dc.DrawRoundedRectangle(brush, null, rect, 2, 2);
            }
            dc.Pop();
        }
        catch (ArgumentOutOfRangeException)
        {
            // Stale layout: nothing drawn this frame.
        }
    }

    /// <summary>Index in the (sorted) matches of the first one at or after <paramref name="position"/>.</summary>
    private int FirstMatchFrom(int position)
    {
        int low = 0, high = _matches.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (_matches[mid] < position) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    /// <summary>The rectangles covering a match: one, or one per line when it wraps.</summary>
    private IEnumerable<Rect> BoxesOf(int start)
    {
        var first = _box.GetRectFromCharacterIndex(start);
        var last = _box.GetRectFromCharacterIndex(start + _length - 1, trailingEdge: true);
        if (first.IsEmpty || last.IsEmpty) yield break;
        if (Math.Abs(first.Top - last.Top) < 1)
        {
            yield return Box(first.X, first.Top, last.X, first.Height);
            yield break;
        }
        double left = first.X, top = first.Top, right = first.X, height = first.Height;
        for (int k = 0; k < _length; k++)
        {
            var edge = _box.GetRectFromCharacterIndex(start + k, trailingEdge: true);
            if (edge.IsEmpty) continue;
            if (Math.Abs(edge.Top - top) < 1)
            {
                right = edge.X;
                continue;
            }
            yield return Box(left, top, right, height);
            var lead = _box.GetRectFromCharacterIndex(start + k);
            left = lead.X;
            top = edge.Top;
            right = edge.X;
            height = edge.Height;
        }
        yield return Box(left, top, right, height);
    }

    private static Rect Box(double left, double top, double right, double height) =>
        new(left, top, Math.Max(3, right - left), height);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
