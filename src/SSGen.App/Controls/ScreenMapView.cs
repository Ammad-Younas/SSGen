using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SSGen.App.ViewModels;

namespace SSGen.App.Controls;

/// <summary>
/// Draws the screen graph: columns by distance from the start screen (breadth-first over the links), screens as
/// rounded boxes, links as arrows (curved when they go back or sideways). Scales down to fit its space. Colours
/// come from theme brushes so it follows light/dark mode; the start screen uses the app's red.
/// </summary>
public sealed class ScreenMapView : FrameworkElement
{
    private const double BoxWidth = 150;
    private const double BoxHeight = 34;
    private const double ColumnGap = 56;
    private const double RowGap = 18;
    private const double Pad = 12;

    public static readonly DependencyProperty NodesProperty = DependencyProperty.Register(
        nameof(Nodes), typeof(IReadOnlyList<MapNode>), typeof(ScreenMapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty EdgesProperty = DependencyProperty.Register(
        nameof(Edges), typeof(IReadOnlyList<MapEdge>), typeof(ScreenMapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    // Theme brushes (set with SetResourceReference, so a theme change re-renders).
    public static readonly DependencyProperty BoxBrushProperty = Brush(nameof(BoxBrush));
    public static readonly DependencyProperty BorderBrushProperty = Brush(nameof(BorderBrush));
    public static readonly DependencyProperty TextBrushProperty = Brush(nameof(TextBrush));
    public static readonly DependencyProperty LineBrushProperty = Brush(nameof(LineBrush));
    public static readonly DependencyProperty AccentBrushProperty = Brush(nameof(AccentBrush));

    public ScreenMapView()
    {
        SetResourceReference(BoxBrushProperty, "ControlFillColorDefaultBrush");
        SetResourceReference(BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        SetResourceReference(TextBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(LineBrushProperty, "TextFillColorTertiaryBrush");
        SetResourceReference(AccentBrushProperty, "NavSelectedBrush");
        ClipToBounds = true;
    }

    public IReadOnlyList<MapNode>? Nodes
    {
        get => (IReadOnlyList<MapNode>?)GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    public IReadOnlyList<MapEdge>? Edges
    {
        get => (IReadOnlyList<MapEdge>?)GetValue(EdgesProperty);
        set => SetValue(EdgesProperty, value);
    }

    public Brush? BoxBrush { get => (Brush?)GetValue(BoxBrushProperty); set => SetValue(BoxBrushProperty, value); }

    public Brush? BorderBrush { get => (Brush?)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }

    public Brush? TextBrush { get => (Brush?)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    public Brush? LineBrush { get => (Brush?)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    private static DependencyProperty Brush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(ScreenMapView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Box positions: column = breadth-first distance from the start screen(s), row = order within the column.</summary>
    internal static Dictionary<int, Point> Layout(IReadOnlyList<MapNode> nodes, IReadOnlyList<MapEdge> edges)
    {
        var level = new Dictionary<int, int>();
        var queue = new Queue<int>();
        foreach (var root in nodes.Where(n => n.IsRoot).Select(n => n.Id).DefaultIfEmpty(nodes.Count > 0 ? nodes.Min(n => n.Id) : 0))
        {
            if (nodes.Any(n => n.Id == root) && level.TryAdd(root, 0))
            {
                queue.Enqueue(root);
            }
        }

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var e in edges.Where(e => e.From == id))
            {
                if (level.TryAdd(e.To, level[id] + 1))
                {
                    queue.Enqueue(e.To);
                }
            }
        }

        var unreached = level.Count == 0 ? 0 : level.Values.Max() + 1;
        foreach (var n in nodes.Where(n => !level.ContainsKey(n.Id)))
        {
            level[n.Id] = unreached; // found by relaunching or after a takeover
        }

        var positions = new Dictionary<int, Point>();
        foreach (var column in nodes.GroupBy(n => level[n.Id]).OrderBy(g => g.Key))
        {
            var row = 0;
            foreach (var n in column.OrderBy(n => n.Id))
            {
                positions[n.Id] = new Point(Pad + (column.Key * (BoxWidth + ColumnGap)), Pad + (row++ * (BoxHeight + RowGap)));
            }
        }

        return positions;
    }

    private const double MinScale = 0.65;

    /// <summary>Fits the width down to a readable minimum; beyond that the diagram scrolls (host it in a ScrollViewer).</summary>
    private double ScaleFor(double width, double availableWidth) =>
        double.IsInfinity(availableWidth) || availableWidth <= 0 ? 1 : Math.Clamp(availableWidth / width, MinScale, 1);

    protected override Size MeasureOverride(Size availableSize)
    {
        var nodes = Nodes ?? [];
        if (nodes.Count == 0)
        {
            return new Size(0, 60);
        }

        var pos = Layout(nodes, Edges ?? []);
        var width = pos.Values.Max(p => p.X) + BoxWidth + Pad;
        var height = pos.Values.Max(p => p.Y) + BoxHeight + Pad + RowGap;
        var scale = ScaleFor(width, availableSize.Width);
        return new Size(width * scale, height * scale);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var dc = drawingContext;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit-testable for tooltips/scrolling
        var nodes = Nodes ?? [];
        var edges = Edges ?? [];
        if (nodes.Count == 0)
        {
            DrawText(dc, "The map of screens and how they connect appears here while exploring.", new Point(Pad, Pad), RenderSize.Width - (2 * Pad), 12, TextBrush, center: false);
            return;
        }

        var pos = Layout(nodes, edges);
        var width = pos.Values.Max(p => p.X) + BoxWidth + Pad;
        var height = pos.Values.Max(p => p.Y) + BoxHeight + Pad;
        var scale = Math.Clamp(RenderSize.Width / width, MinScale, 1); // matches MeasureOverride
        dc.PushTransform(new ScaleTransform(scale, scale));

        var line = new Pen(LineBrush ?? Brushes.Gray, 1.2);
        line.Freeze();
        foreach (var e in edges)
        {
            if (!pos.TryGetValue(e.From, out var a) || !pos.TryGetValue(e.To, out var b))
            {
                continue;
            }

            Point start, end;
            Geometry geometry;
            if (b.X > a.X)
            {
                // Forward link: right edge of the source to the left edge of the target.
                start = new Point(a.X + BoxWidth, a.Y + (BoxHeight / 2));
                end = new Point(b.X, b.Y + (BoxHeight / 2));
                var mid = (start.X + end.X) / 2;
                geometry = Bezier(start, new Point(mid, start.Y), new Point(mid, end.Y), end);
            }
            else
            {
                // Back or same-column link: loop under the boxes so it doesn't cross them.
                start = new Point(a.X + (BoxWidth * 0.35), a.Y + BoxHeight);
                end = new Point(b.X + (BoxWidth * 0.65), b.Y + BoxHeight);
                var dip = Math.Max(start.Y, end.Y) + RowGap * 0.9;
                geometry = Bezier(start, new Point(start.X, dip), new Point(end.X, dip), end);
            }

            dc.DrawGeometry(null, line, geometry);
            DrawArrowHead(dc, end, b.X > a.X ? new Vector(1, 0) : new Vector(0, -1), LineBrush ?? Brushes.Gray);
        }

        foreach (var n in nodes)
        {
            var p = pos[n.Id];
            var rect = new Rect(p, new Size(BoxWidth, BoxHeight));
            var fill = n.IsRoot ? AccentBrush : BoxBrush;
            dc.DrawRoundedRectangle(fill, new Pen(n.IsRoot ? AccentBrush : BorderBrush, 1), rect, 6, 6);
            DrawText(dc, n.Label, new Point(rect.X + 8, rect.Y + ((BoxHeight - 16) / 2)), BoxWidth - 16, 12, n.IsRoot ? Brushes.White : TextBrush, center: true);
        }

        dc.Pop();
    }

    private static PathGeometry Bezier(Point start, Point c1, Point c2, Point end)
    {
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new BezierSegment(c1, c2, end, true));
        var g = new PathGeometry([figure]);
        g.Freeze();
        return g;
    }

    private static void DrawArrowHead(DrawingContext dc, Point tip, Vector direction, Brush brush)
    {
        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        var back = tip - (direction * 7);
        var figure = new PathFigure { StartPoint = tip, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(back + (normal * 3.5), true));
        figure.Segments.Add(new LineSegment(back - (normal * 3.5), true));
        var g = new PathGeometry([figure]);
        g.Freeze();
        dc.DrawGeometry(brush, null, g);
    }

    private void DrawText(DrawingContext dc, string text, Point origin, double maxWidth, double size, Brush? brush, bool center)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush ?? Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = center ? 1 : 3,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
        };
        dc.DrawText(formatted, origin);
    }
}
