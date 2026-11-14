using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PhotoLibrary.App.ViewModels;
using PhotoLibrary.Core.Geo;

namespace PhotoLibrary.App.Views;

/// <summary>
/// An offline world map (Web Mercator) drawn from the bundled country outlines, with photos as clusters: points
/// that land within one cell on screen merge into a bubble with a count, so the map stays readable at any zoom.
/// Wheel zooms around the cursor, drag pans, click selects a cluster, double-click zooms into it.
/// </summary>
public sealed class MapControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<MapPoint>?> PointsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<MapPoint>?>(nameof(Points));
    public static readonly StyledProperty<MapBounds?> FocusAreaProperty =
        AvaloniaProperty.Register<MapControl, MapBounds?>(nameof(FocusArea));
    public static readonly StyledProperty<ICommand?> ClusterCommandProperty =
        AvaloniaProperty.Register<MapControl, ICommand?>(nameof(ClusterCommand));
    public static readonly StyledProperty<Func<IReadOnlyList<long>, string>?> DescribeProperty =
        AvaloniaProperty.Register<MapControl, Func<IReadOnlyList<long>, string>?>(nameof(Describe));
    public static readonly StyledProperty<IReadOnlySet<long>?> HighlightProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<long>?>(nameof(Highlight));

    public IReadOnlyList<MapPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    /// <summary>Set to fly the view to an area (a place picked in the list).</summary>
    public MapBounds? FocusArea { get => GetValue(FocusAreaProperty); set => SetValue(FocusAreaProperty, value); }
    /// <summary>Executed with the photo ids of a clicked cluster.</summary>
    public ICommand? ClusterCommand { get => GetValue(ClusterCommandProperty); set => SetValue(ClusterCommandProperty, value); }
    /// <summary>Hover text for a cluster ("Paris, France · 12 photos").</summary>
    public Func<IReadOnlyList<long>, string>? Describe { get => GetValue(DescribeProperty); set => SetValue(DescribeProperty, value); }
    /// <summary>Photos currently shown below the map; their clusters are drawn in the accent colour.</summary>
    public IReadOnlySet<long>? Highlight { get => GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }

    private const double CellPx = 56, MaxScale = 120_000;
    private static StreamGeometry? _land;

    private double _scale;          // pixels per world unit (the world is 1 × 1)
    private Point _offset;          // screen position of world (0, 0)
    private bool _fitted;
    private Point? _pressAt;
    private Point _lastDrag;
    private bool _dragged;
    private readonly List<Cluster> _clusters = [];

    private sealed class Cluster
    {
        public double X, Y;
        public readonly List<long> Ids = [];
        public double Radius => Ids.Count == 1 ? 6 : 11 + 4 * Math.Log10(Ids.Count);
    }

    public MapControl()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    // ---- projection

    public static Point Project(double lat, double lon)
    {
        lat = Math.Clamp(lat, -85.0511, 85.0511);
        var r = lat * Math.PI / 180;
        return new Point((lon + 180) / 360, (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2);
    }

    private Point ToScreen(Point world) => new(world.X * _scale + _offset.X, world.Y * _scale + _offset.Y);

    private static StreamGeometry Land()
    {
        if (_land is not null) return _land;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.SetFillRule(FillRule.EvenOdd);
            foreach (var c in GeoData.Countries)
                foreach (var ring in c.Rings)
                {
                    if (ring.Length < 6) continue;
                    ctx.BeginFigure(Project(ring[1], ring[0]), true);
                    for (var i = 2; i < ring.Length; i += 2) ctx.LineTo(Project(ring[i + 1], ring[i]));
                    ctx.EndFigure(true);
                }
        }
        return _land = g;
    }

    // ---- view

    private double MinScale => Math.Max(Bounds.Width, Bounds.Height * 0.9);

    private void Clamp()
    {
        _scale = Math.Clamp(_scale, MinScale, MaxScale);
        var size = _scale; // the world is square
        double ClampAxis(double offset, double view) =>
            size <= view ? (view - size) / 2 : Math.Clamp(offset, view - size, 0);
        _offset = new Point(ClampAxis(_offset.X, Bounds.Width), ClampAxis(_offset.Y, Bounds.Height));
    }

    private void FitTo(MapBounds b)
    {
        if (Bounds.Width < 10 || Bounds.Height < 10) return;
        // Never zoom in tighter than about 3 km across, so a single photo still shows its surroundings.
        var pad = Math.Max(0.03, Math.Max(b.MaxLat - b.MinLat, b.MaxLon - b.MinLon) * 0.15);
        var tl = Project(b.MaxLat + pad, b.MinLon - pad);
        var br = Project(b.MinLat - pad, b.MaxLon + pad);
        var w = Math.Max(1e-9, br.X - tl.X);
        var h = Math.Max(1e-9, br.Y - tl.Y);
        _scale = Math.Min(Bounds.Width / w, Bounds.Height / h);
        _scale = Math.Clamp(_scale, MinScale, MaxScale);
        var centre = new Point((tl.X + br.X) / 2, (tl.Y + br.Y) / 2);
        _offset = new Point(Bounds.Width / 2 - centre.X * _scale, Bounds.Height / 2 - centre.Y * _scale);
        Clamp();
        // No InvalidateVisual here: Render calls this for the first fit, and asking for a redraw from inside a
        // render pass throws. Callers outside Render invalidate themselves.
    }

    private void FitAll()
    {
        var pts = Points;
        if (pts is null || pts.Count == 0)
        {
            _scale = MinScale;
            _offset = default;
            Clamp();
            return;
        }
        FitTo(new MapBounds(pts.Min(p => p.Lat), pts.Min(p => p.Lon), pts.Max(p => p.Lat), pts.Max(p => p.Lon)));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PointsProperty) { _fitted = false; InvalidateVisual(); }
        else if (change.Property == FocusAreaProperty && FocusArea is { } f)
        {
            _fitted = true; // an explicit area wins over the first automatic fit
            FitTo(f);
            InvalidateVisual();
        }
        else if (change.Property == HighlightProperty) InvalidateVisual();
        else if (change.Property == BoundsProperty) { if (_fitted) Clamp(); InvalidateVisual(); }
    }

    // ---- drawing

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    private Color Accent => this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var v) && v is Color c ? c : Colors.SteelBlue;

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 10 || Bounds.Height < 10) return;
        if (!_fitted) { _fitted = true; FitAll(); }
        else Clamp();

        context.FillRectangle(Brush("KsTileBrush", Brushes.LightSteelBlue), new Rect(Bounds.Size));
        using (context.PushTransform(Matrix.CreateScale(_scale, _scale) * Matrix.CreateTranslation(_offset.X, _offset.Y)))
            context.DrawGeometry(Brush("KsCardBrush", Brushes.Beige), new Pen(Brush("KsBorderBrush", Brushes.Gray), 1 / _scale), Land());

        BuildClusters();
        var accent = Accent;
        var normal = new SolidColorBrush(Color.FromArgb(215, accent.R, accent.G, accent.B));
        var highlightBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x00));
        var ring = new Pen(Brushes.White, 1.5);
        var highlight = Highlight;
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        foreach (var c in _clusters.OrderBy(c => c.Ids.Count))
        {
            var lit = highlight is { Count: > 0 } && c.Ids.Any(highlight.Contains);
            context.DrawEllipse(lit ? highlightBrush : normal, ring, new Point(c.X, c.Y), c.Radius, c.Radius);
            if (c.Ids.Count < 2) continue;
            var text = new FormattedText(c.Ids.Count >= 10_000 ? $"{c.Ids.Count / 1000}k" : c.Ids.Count.ToString("N0", CultureInfo.CurrentCulture),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, c.Ids.Count >= 1000 ? 10 : 11, Brushes.White);
            context.DrawText(text, new Point(c.X - text.Width / 2, c.Y - text.Height / 2));
        }

        var credit = new FormattedText("Map: Natural Earth  ·  Places: GeoNames (CC BY 4.0)", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 10, Brush("KsMutedBrush", Brushes.Gray));
        context.DrawText(credit, new Point(Bounds.Width - credit.Width - 8, Bounds.Height - credit.Height - 4));
    }

    /// <summary>Grid clustering in screen space: every point in the same cell joins one bubble at their average position.</summary>
    private void BuildClusters()
    {
        _clusters.Clear();
        if (Points is not { Count: > 0 } pts) return;
        var cells = new Dictionary<(long, long), Cluster>();
        foreach (var p in pts)
        {
            var s = ToScreen(Project(p.Lat, p.Lon));
            if (s.X < -CellPx || s.Y < -CellPx || s.X > Bounds.Width + CellPx || s.Y > Bounds.Height + CellPx) continue;
            var key = ((long)Math.Floor((s.X - _offset.X) / CellPx), (long)Math.Floor((s.Y - _offset.Y) / CellPx));
            if (!cells.TryGetValue(key, out var c)) cells[key] = c = new Cluster();
            c.X += s.X;
            c.Y += s.Y;
            c.Ids.Add(p.Id);
        }
        foreach (var c in cells.Values)
        {
            c.X /= c.Ids.Count;
            c.Y /= c.Ids.Count;
            _clusters.Add(c);
        }
    }

    private Cluster? HitTest(Point at) =>
        _clusters.Where(c => Math.Pow(c.X - at.X, 2) + Math.Pow(c.Y - at.Y, 2) <= Math.Pow(c.Radius + 4, 2))
            .OrderByDescending(c => c.Ids.Count).FirstOrDefault();

    // ---- input

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAround(e.GetPosition(this), Math.Pow(1.3, e.Delta.Y));
        e.Handled = true;
    }

    private void ZoomAround(Point at, double factor)
    {
        var world = new Point((at.X - _offset.X) / _scale, (at.Y - _offset.Y) / _scale);
        _scale = Math.Clamp(_scale * factor, MinScale, MaxScale);
        _offset = new Point(at.X - world.X * _scale, at.Y - world.Y * _scale);
        Clamp();
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var at = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            ZoomAround(HitTest(at) is { } c ? new Point(c.X, c.Y) : at, 2.5);
            _pressAt = null;
            e.Handled = true;
            return;
        }
        _pressAt = _lastDrag = at;
        _dragged = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (_pressAt is { } start)
        {
            if (!_dragged && Math.Abs(at.X - start.X) + Math.Abs(at.Y - start.Y) < 4) return;
            _dragged = true;
            _offset += at - _lastDrag;
            _lastDrag = at;
            Clamp();
            InvalidateVisual();
            return;
        }
        var hit = HitTest(at);
        ToolTip.SetTip(this, hit is null ? null : Describe?.Invoke(hit.Ids) ?? $"{hit.Ids.Count:N0} photos");
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressAt is { } start && !_dragged && HitTest(start) is { } hit && ClusterCommand is { } cmd && cmd.CanExecute(hit.Ids))
            cmd.Execute(hit.Ids.ToList());
        _pressAt = null;
        e.Pointer.Capture(null);
    }
}
