using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RnxCAD;

public sealed class CadSurface : FrameworkElement
{
    private static readonly Brush Background = BrushFor("#121D2A");
    private static readonly Brush LabelBrush = BrushFor("#AFC2D0");
    private static readonly Brush Highlight = BrushFor("#DDF58F");
    private static readonly Pen GridFine = FrozenPen(BrushFor("#1A2839"), 1);
    private static readonly Pen GridStrong = FrozenPen(BrushFor("#23364A"), 1);
    private static readonly Pen WorldAxis = FrozenPen(BrushFor("#33485E"), 1);
    private static readonly Pen Crosshair = FrozenPen(BrushFor("#849EB4"), 1);
    private static readonly Pen SnapPen = FrozenPen(Highlight, 1.35);
    private static readonly Pen DraftPen = FrozenPen(Highlight, 1.25, DashStyles.Dash);
    private static readonly Pen BoxPen = FrozenPen(BrushFor("#415771"), 1);
    private static readonly Brush Overlay = BrushFor("#192736");
    private static readonly Typeface Mono = new(new FontFamily("IBM Plex Mono, Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly Dictionary<(string, double, bool), Pen> pens = new();

    private Drawing drawing = Drawing.Empty();
    private Dictionary<string, Layer> layers = new();
    private Vec center = new(0, 0);
    private double scale = 0.16;
    private Point? lastPan;
    private Point? selectionStart;
    private Point pointer;
    private bool pointerVisible;
    private bool fitPending = true;
    private bool autoFit = true;

    internal bool GridEnabled { get; set; } = true;
    internal bool PanningTool { get; set; }
    internal bool Selecting { get; set; } = true;
    internal Vec? CursorPoint { get; set; }
    internal SnapPoint? Snap { get; set; }
    internal IReadOnlyList<Shape> Preview { get; set; } = [];
    internal HashSet<Guid> Selected { get; set; } = [];
    internal double UnitsPerPixel => 1 / scale;
    internal event Action<Vec>? PointerMoved;
    internal event Action<Vec, bool>? PointClicked;
    internal event Action<Box, bool, bool>? BoxSelected;
    internal event Action? ToolConfirmed;

    public CadSurface()
    {
        Cursor = Cursors.None;
        AutomationPropertiesName();
        SizeChanged += (_, _) =>
        {
            if (fitPending || autoFit) Fit();
            else InvalidateVisual();
        };
    }

    private void AutomationPropertiesName() => System.Windows.Automation.AutomationProperties.SetName(this, "Поле чертежа");

    internal void SetDrawing(Drawing value, bool resetView)
    {
        if (!ReferenceEquals(drawing.Layers, value.Layers))
        {
            layers = value.Layers.ToDictionary(layer => layer.Name);
            pens.Clear();
        }
        drawing = value;
        if (resetView) Fit();
        else InvalidateVisual();
    }

    internal void Fit()
    {
        if (ActualWidth < 100 || ActualHeight < 100)
        {
            fitPending = true;
            return;
        }
        fitPending = false;
        autoFit = true;
        var bounds = drawing.Shapes.Where(shape => layers[shape.Layer].Visible).Select(shape => shape.Bounds).ToArray();
        if (bounds.Length == 0)
        {
            center = new(0, 0);
            scale = 0.16;
        }
        else
        {
            var box = bounds.Aggregate((first, second) => first.Union(second));
            center = box.Center;
            scale = Math.Clamp(Math.Min((ActualWidth - 122) / Math.Max(box.Width, 1),
                (ActualHeight - 86) / Math.Max(box.Height, 1)), 1e-13, 10000);
        }
        InvalidateVisual();
    }

    internal void Zoom(double factor) => Zoom(factor, new Point(ActualWidth / 2, ActualHeight / 2));

    private void Zoom(double factor, Point at)
    {
        autoFit = false;
        var anchor = ToWorld(at);
        scale = Math.Clamp(scale * factor, 1e-13, 10000);
        center += anchor - ToWorld(at);
        InvalidateVisual();
    }

    private Point Pixel(Vec point) => new((point.X - center.X) * scale + ActualWidth / 2, (center.Y - point.Y) * scale + ActualHeight / 2);
    private Vec ToWorld(Point point) => new((point.X - ActualWidth / 2) / scale + center.X, (ActualHeight / 2 - point.Y) / scale + center.Y);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        pointer = e.GetPosition(this);
        pointerVisible = IsMouseOver;
        if (lastPan is Point last)
        {
            autoFit = false;
            center += new Vec((last.X - pointer.X) / scale, (pointer.Y - last.Y) / scale);
            lastPan = pointer;
        }
        PointerMoved?.Invoke(ToWorld(pointer));
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        pointerVisible = false;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        pointer = e.GetPosition(this);
        Zoom(Math.Pow(1.2, e.Delta / 120d), pointer);
        PointerMoved?.Invoke(ToWorld(pointer));
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        pointer = e.GetPosition(this);
        pointerVisible = true;
        if (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left && PanningTool)
        {
            if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Middle)
            {
                Fit();
            }
            else
            {
                lastPan = pointer;
                Cursor = Cursors.Hand;
                CaptureMouse();
            }
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            if (Selecting)
            {
                selectionStart = pointer;
                CaptureMouse();
            }
            else PointClicked?.Invoke(ToWorld(pointer), false);
        }
        else if (e.ChangedButton == MouseButton.Right) ToolConfirmed?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        pointer = e.GetPosition(this);
        if (lastPan is not null && (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left))
        {
            lastPan = null;
            ReleaseMouseCapture();
            Cursor = PanningTool ? Cursors.Hand : Cursors.None;
        }
        if (e.ChangedButton == MouseButton.Left && selectionStart is Point start)
        {
            selectionStart = null;
            ReleaseMouseCapture();
            var additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if ((pointer - start).Length < 5) PointClicked?.Invoke(ToWorld(pointer), additive);
            else BoxSelected?.Invoke(Box.Between(ToWorld(start), ToWorld(pointer)), pointer.X < start.X, additive);
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        lastPan = null;
        selectionStart = null;
        Cursor = PanningTool ? Cursors.Hand : Cursors.None;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        var area = new Rect(0, 0, width, height);
        dc.DrawRectangle(Background, null, area);
        dc.PushClip(new RectangleGeometry(area));
        var extents = Box.Between(ToWorld(new Point(0, height)), ToWorld(new Point(width, 0)));
        if (GridEnabled) DrawGrid(dc, extents);
        foreach (var shape in drawing.Shapes)
        {
            if (!layers.TryGetValue(shape.Layer, out var layer) || !layer.Visible) continue;
            var selected = Selected.Contains(shape.Id);
            var key = (shape.Color ?? layer.Color, layer.Weight, selected);
            if (!pens.TryGetValue(key, out var pen))
            {
                pen = FrozenPen(selected ? Highlight : BrushFor(key.Item1), layer.Weight + (selected ? 0.6 : 0));
                pens[key] = pen;
            }
            DrawShape(dc, shape, pen);
            if (selected)
                foreach (var grip in shape.Snaps().Where(snap => snap.Kind != SnapKind.Mid).Select(snap => snap.Point).Distinct())
                {
                    var pixel = Pixel(grip);
                    dc.DrawRectangle(Background, SnapPen, new Rect(pixel.X - 2.5, pixel.Y - 2.5, 5, 5));
                }
        }
        if (pointerVisible && lastPan is null)
        {
            foreach (var item in Preview) DrawShape(dc, item, DraftPen);
            if (selectionStart is Point start && (pointer - start).Length >= 5)
            {
                var crossing = pointer.X < start.X;
                var color = crossing ? "#26799765" : "#324B88B0";
                var pen = FrozenPen(crossing ? Highlight : BrushFor("#92BFE4"), 1, crossing ? DashStyles.Dash : null);
                dc.DrawRectangle(BrushFor(color), pen, new Rect(start, pointer));
            }
            if (!PanningTool) DrawCursor(dc);
        }
        DrawViewMarks(dc);
        dc.Pop();
    }

    private void DrawGrid(DrawingContext dc, Box view)
    {
        var spacing = NiceStep(14 / scale);
        var firstX = (long)Math.Ceiling(view.Left / spacing);
        var lastX = (long)Math.Floor(view.Right / spacing);
        var firstY = (long)Math.Ceiling(view.Bottom / spacing);
        var lastY = (long)Math.Floor(view.Top / spacing);
        for (var index = firstX; index <= lastX; index++)
        {
            var x = Math.Floor(Pixel(new Vec(index * spacing, 0)).X) + 0.5;
            dc.DrawLine(GridPen(index), new Point(x, 0), new Point(x, ActualHeight));
        }
        for (var index = firstY; index <= lastY; index++)
        {
            var y = Math.Floor(Pixel(new Vec(0, index * spacing)).Y) + 0.5;
            dc.DrawLine(GridPen(index), new Point(0, y), new Point(ActualWidth, y));
        }
    }

    private static Pen GridPen(long index) => index == 0 ? WorldAxis : index % 5 == 0 ? GridStrong : GridFine;

    private void DrawShape(DrawingContext dc, Shape shape, Pen pen)
    {
        switch (shape)
        {
            case Segment line:
                dc.DrawLine(pen, Pixel(line.A), Pixel(line.B));
                break;
            case Circle circle:
                dc.DrawEllipse(null, pen, Pixel(circle.Center), circle.Radius * scale, circle.Radius * scale);
                break;
            case Arc arc:
                var path = new StreamGeometry();
                using (var context = path.Open())
                {
                    context.BeginFigure(Pixel(arc.At(arc.Start)), false, false);
                    AddArc(context, arc);
                }
                dc.DrawGeometry(null, pen, path);
                break;
            case Polyline { Vertices.Length: > 0 } polyline:
                var chain = new StreamGeometry();
                using (var context = chain.Open())
                {
                    context.BeginFigure(Pixel(polyline.Vertices[0]), false, polyline.Closed);
                    if (polyline.Bulges is null)
                    {
                        context.PolyLineTo(polyline.Vertices.Skip(1).Select(Pixel).ToArray(), true, false);
                    }
                    else
                    {
                        foreach (var edge in polyline.Edges())
                        {
                            if (edge is Arc part) AddArc(context, part);
                            else context.LineTo(Pixel(((Segment)edge).B), true, false);
                        }
                    }
                }
                dc.DrawGeometry(null, pen, chain);
                break;
            case Note note:
                var height = note.Height * scale;
                if (height is < 1 or > 100000) break;
                var point = Pixel(note.Position);
                var text = Format(note.Value, height * 1.22, pen.Brush, Mono);
                var offset = note.Anchor switch { TextAnchor.Center => text.Width / 2, TextAnchor.Right => text.Width, _ => 0 };
                dc.PushTransform(new RotateTransform(-note.Angle * 180 / Math.PI, point.X, point.Y));
                dc.DrawText(text, new Point(point.X - offset, point.Y - text.Baseline));
                dc.Pop();
                break;
            case Dimension dimension:
                foreach (var part in dimension.Graphics()) DrawShape(dc, part, pen);
                break;
        }
    }

    private void AddArc(StreamGeometryContext context, Arc arc)
    {
        var radius = arc.Radius * scale;
        context.ArcTo(Pixel(arc.At(arc.Start + arc.Sweep)), new Size(radius, radius), 0,
            Math.Abs(arc.Sweep) > Math.PI, arc.Sweep > 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise, true, false);
    }

    private void DrawCursor(DrawingContext dc)
    {
        var p = Selecting ? pointer : CursorPoint is Vec position ? Pixel(position) : pointer;
        const double arm = 15;
        const double gap = 3;
        dc.DrawLine(Crosshair, new Point(p.X - arm, p.Y), new Point(p.X - gap, p.Y));
        dc.DrawLine(Crosshair, new Point(p.X + gap, p.Y), new Point(p.X + arm, p.Y));
        dc.DrawLine(Crosshair, new Point(p.X, p.Y - arm), new Point(p.X, p.Y - gap));
        dc.DrawLine(Crosshair, new Point(p.X, p.Y + gap), new Point(p.X, p.Y + arm));
        dc.DrawRectangle(null, Crosshair, new Rect(p.X - gap, p.Y - gap, gap * 2, gap * 2));
        if (Selecting || Snap is not SnapPoint snap) return;
        switch (snap.Kind)
        {
            case SnapKind.End:
                dc.DrawRectangle(null, SnapPen, new Rect(p.X - 5, p.Y - 5, 10, 10));
                break;
            case SnapKind.Mid:
                dc.DrawLine(SnapPen, new Point(p.X, p.Y - 6), new Point(p.X - 5, p.Y + 4));
                dc.DrawLine(SnapPen, new Point(p.X - 5, p.Y + 4), new Point(p.X + 5, p.Y + 4));
                dc.DrawLine(SnapPen, new Point(p.X + 5, p.Y + 4), new Point(p.X, p.Y - 6));
                break;
            case SnapKind.Center:
                dc.DrawEllipse(null, SnapPen, p, 5, 5);
                break;
        }
    }

    private void DrawViewMarks(DrawingContext dc)
    {
        Badge(dc, "ВИД СВЕРХУ", 15, 14, true);
        Badge(dc, "2D-КАРКАС", 115, 14, false);

        var x = ActualWidth - 50;
        const double y = 49;
        dc.DrawEllipse(Overlay, BoxPen, new Point(x, y), 23, 23);
        dc.DrawLine(BoxPen, new Point(x - 30, y), new Point(x - 15, y));
        dc.DrawLine(BoxPen, new Point(x + 15, y), new Point(x + 30, y));
        dc.DrawLine(BoxPen, new Point(x, y + 15), new Point(x, y + 30));
        dc.DrawLine(SnapPen, new Point(x, y + 3), new Point(x, y - 18));
        dc.DrawLine(SnapPen, new Point(x, y - 18), new Point(x - 3, y - 12));
        dc.DrawLine(SnapPen, new Point(x, y - 18), new Point(x + 3, y - 12));
        dc.DrawEllipse(Highlight, null, new Point(x, y + 3), 1.7, 1.7);
        dc.DrawText(Format("Y", 10, Highlight, Mono), new Point(x - 3, y - 40));

        var bottom = ActualHeight - 24;
        var red = BrushFor("#E3918D");
        var green = BrushFor("#A0C997");
        dc.DrawLine(FrozenPen(red, 1), new Point(28, bottom), new Point(58, bottom));
        dc.DrawLine(FrozenPen(green, 1), new Point(28, bottom), new Point(28, bottom - 29));
        dc.DrawRectangle(Overlay, Crosshair, new Rect(25, bottom - 3, 6, 6));
        dc.DrawText(Format("X", 10, red, Mono), new Point(63, bottom - 7));
        dc.DrawText(Format("Y", 10, green, Mono), new Point(25, bottom - 43));

        var world = NiceStep(64 / scale);
        var length = world * scale;
        var right = ActualWidth - 22;
        var left = right - length;
        var ruler = FrozenPen(LabelBrush, 1);
        dc.DrawLine(ruler, new Point(left, bottom), new Point(right, bottom));
        foreach (var tick in new[] { left, (left + right) / 2, right })
            dc.DrawLine(ruler, new Point(tick, bottom - 4), new Point(tick, bottom + 4));
        var caption = Format($"{(world < 0.01 ? world.ToString("G4", CultureInfo.InvariantCulture) : CadFormat.Number(world))} {CadFormat.Units(drawing.Units)}", 10, LabelBrush, Mono);
        dc.DrawText(caption, new Point(right - caption.Width, bottom - 19));
    }

    private void Badge(DrawingContext dc, string value, double x, double y, bool primary)
    {
        var text = Format(value, 10, primary ? Highlight : LabelBrush, Mono);
        dc.DrawRoundedRectangle(Overlay, BoxPen, new Rect(x, y, text.Width + 20, 24), 4, 4);
        dc.DrawText(text, new Point(x + 10, y + 5));
    }

    private FormattedText Format(string value, double size, Brush brush, Typeface typeface) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static double NiceStep(double minimum)
    {
        var power = Math.Pow(10, Math.Floor(Math.Log10(minimum)));
        var ratio = minimum / power;
        return power * (ratio <= 1 ? 1 : ratio <= 2 ? 2 : ratio <= 5 ? 5 : 10);
    }

    internal static Brush BrushFor(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness, DashStyle? dash = null)
    {
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
        if (dash is not null) pen.DashStyle = dash;
        pen.Freeze();
        return pen;
    }
}
