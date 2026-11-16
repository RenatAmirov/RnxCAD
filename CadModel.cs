using System.Globalization;
using System.Text.Json.Serialization;

namespace RnxCAD;

internal readonly record struct Vec(double X, double Y)
{
    [JsonIgnore] public double Length => Math.Sqrt(X * X + Y * Y);
    [JsonIgnore] public Vec Perpendicular => new(-Y, X);
    public double Dot(Vec other) => X * other.X + Y * other.Y;
    public double Cross(Vec other) => X * other.Y - Y * other.X;
    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec operator *(Vec a, double scale) => new(a.X * scale, a.Y * scale);
    public static Vec operator /(Vec a, double scale) => new(a.X / scale, a.Y / scale);
    public static Vec Polar(double angle, double radius = 1) => new(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
}

internal readonly record struct Box(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
    public Vec Center => new((Left + Right) / 2, (Bottom + Top) / 2);
    public bool Contains(Vec p) => p.X >= Left && p.X <= Right && p.Y >= Bottom && p.Y <= Top;
    public bool Contains(Box b) => Contains(new Vec(b.Left, b.Bottom)) && Contains(new Vec(b.Right, b.Top));
    public bool Intersects(Box b) => b.Left <= Right && b.Right >= Left && b.Bottom <= Top && b.Top >= Bottom;
    public Box Union(Box b) => new(Math.Min(Left, b.Left), Math.Min(Bottom, b.Bottom), Math.Max(Right, b.Right), Math.Max(Top, b.Top));
    public static Box Between(Vec a, Vec b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    public static Box Around(IEnumerable<Vec> points)
    {
        using var enumerator = points.GetEnumerator();
        if (!enumerator.MoveNext()) return default;
        var bounds = Between(enumerator.Current, enumerator.Current);
        while (enumerator.MoveNext()) bounds = bounds.Union(Between(enumerator.Current, enumerator.Current));
        return bounds;
    }
}

internal enum SnapKind { End, Mid, Center }
internal readonly record struct SnapPoint(Vec Point, SnapKind Kind);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Segment), "line")]
[JsonDerivedType(typeof(Polyline), "polyline")]
[JsonDerivedType(typeof(Circle), "circle")]
[JsonDerivedType(typeof(Arc), "arc")]
[JsonDerivedType(typeof(Note), "text")]
[JsonDerivedType(typeof(Dimension), "dimension")]
internal abstract record Shape
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Layer { get; init; } = "0";
    public string? Color { get; init; }
    [JsonIgnore] public abstract Box Bounds { get; }
    public abstract Shape Shift(Vec delta);
    public abstract double Distance(Vec position);
    public abstract IEnumerable<SnapPoint> Snaps();
    public virtual bool Crosses(Box box) => box.Intersects(Bounds);
}

internal sealed record Segment(Vec A, Vec B) : Shape
{
    public override Box Bounds => Box.Between(A, B);
    public override Shape Shift(Vec delta) => this with { A = A + delta, B = B + delta };
    public override double Distance(Vec position)
    {
        var direction = B - A;
        var squared = direction.Dot(direction);
        if (squared == 0) return (position - A).Length;
        var t = Math.Clamp((position - A).Dot(direction) / squared, 0, 1);
        return (position - (A + direction * t)).Length;
    }
    public override IEnumerable<SnapPoint> Snaps()
    {
        yield return new(A, SnapKind.End);
        yield return new(B, SnapKind.End);
        yield return new((A + B) / 2, SnapKind.Mid);
    }
    public override bool Crosses(Box box)
    {
        if (!box.Intersects(Bounds)) return false;
        if (box.Contains(A) || box.Contains(B)) return true;
        var direction = B - A;
        if (direction.X != 0 && new[] { box.Left, box.Right }.Any(x =>
            box.Contains(new Vec(x, A.Y + (x - A.X) / direction.X * direction.Y)))) return true;
        return direction.Y != 0 && new[] { box.Bottom, box.Top }.Any(y =>
            box.Contains(new Vec(A.X + (y - A.Y) / direction.Y * direction.X, y)));
    }
}

internal sealed record Polyline(Vec[] Vertices, bool Closed = false, double[]? Bulges = null) : Shape
{
    public IEnumerable<Shape> Edges()
    {
        var count = Closed ? Vertices.Length : Vertices.Length - 1;
        for (var index = 0; index < count; index++)
        {
            var a = Vertices[index];
            var b = Vertices[(index + 1) % Vertices.Length];
            var bulge = Bulges is not null && index < Bulges.Length ? Bulges[index] : 0;
            var chord = b - a;
            if (Math.Abs(bulge) < 1e-9 || chord.Length < 1e-9)
            {
                yield return new Segment(a, b);
                continue;
            }
            var center = (a + b) / 2 + chord.Perpendicular * ((1 - bulge * bulge) / (4 * bulge));
            var start = a - center;
            yield return new Arc(center, start.Length, Math.Atan2(start.Y, start.X), 4 * Math.Atan(bulge));
        }
    }
    public override Box Bounds => Edges().Select(edge => edge.Bounds).Aggregate(Box.Around(Vertices), (box, edge) => box.Union(edge));
    public override Shape Shift(Vec delta) => this with { Vertices = Vertices.Select(point => point + delta).ToArray() };
    public override double Distance(Vec position) => Edges().Select(edge => edge.Distance(position)).DefaultIfEmpty(double.PositiveInfinity).Min();
    public override IEnumerable<SnapPoint> Snaps() => Edges().SelectMany(edge => edge.Snaps());
    public override bool Crosses(Box box) => Edges().Any(edge => edge.Crosses(box));
    public static Polyline Rectangle(Vec a, Vec b) => new([a, new(b.X, a.Y), b, new(a.X, b.Y)], true);
}

internal sealed record Circle(Vec Center, double Radius) : Shape
{
    public override Box Bounds => new(Center.X - Radius, Center.Y - Radius, Center.X + Radius, Center.Y + Radius);
    public override Shape Shift(Vec delta) => this with { Center = Center + delta };
    public override double Distance(Vec position) => Math.Abs((position - Center).Length - Radius);
    public override IEnumerable<SnapPoint> Snaps()
    {
        yield return new(Center, SnapKind.Center);
        for (var i = 0; i < 4; i++) yield return new(Center + Vec.Polar(i * Math.PI / 2, Radius), SnapKind.End);
    }
    public override bool Crosses(Box box)
    {
        var nearest = new Vec(Math.Clamp(Center.X, box.Left, box.Right), Math.Clamp(Center.Y, box.Bottom, box.Top));
        var farthest = new Vec(Math.Max(Math.Abs(box.Left - Center.X), Math.Abs(box.Right - Center.X)),
                               Math.Max(Math.Abs(box.Bottom - Center.Y), Math.Abs(box.Top - Center.Y)));
        return (nearest - Center).Length <= Radius && farthest.Length >= Radius;
    }
}

internal sealed record Arc(Vec Center, double Radius, double Start, double Sweep) : Shape
{
    public Vec At(double angle) => Center + Vec.Polar(angle, Radius);
    public bool Includes(double angle) => Normalize(Sweep >= 0 ? angle - Start : Start - angle) <= Math.Abs(Sweep) + 1e-10;
    public override Box Bounds => Box.Around(new[] { At(Start), At(Start + Sweep) }
        .Concat(Enumerable.Range(0, 4).Select(i => i * Math.PI / 2).Where(Includes).Select(At)));
    public override Shape Shift(Vec delta) => this with { Center = Center + delta };
    public override double Distance(Vec position)
    {
        var vector = position - Center;
        return Includes(Math.Atan2(vector.Y, vector.X)) ? Math.Abs(vector.Length - Radius)
            : Math.Min((position - At(Start)).Length, (position - At(Start + Sweep)).Length);
    }
    public override IEnumerable<SnapPoint> Snaps()
    {
        yield return new(Center, SnapKind.Center);
        yield return new(At(Start), SnapKind.End);
        yield return new(At(Start + Sweep), SnapKind.End);
        yield return new(At(Start + Sweep / 2), SnapKind.Mid);
    }
    public override bool Crosses(Box box)
    {
        if (!Bounds.Intersects(box)) return false;
        if (box.Contains(At(Start)) || box.Contains(At(Start + Sweep))) return true;
        foreach (var x in new[] { box.Left, box.Right })
        {
            var ratio = (x - Center.X) / Radius;
            if (Math.Abs(ratio) > 1) continue;
            var angle = Math.Acos(ratio);
            if (Includes(angle) && box.Contains(At(angle)) || Includes(-angle) && box.Contains(At(-angle))) return true;
        }
        foreach (var y in new[] { box.Bottom, box.Top })
        {
            var ratio = (y - Center.Y) / Radius;
            if (Math.Abs(ratio) > 1) continue;
            var angle = Math.Asin(ratio);
            if (Includes(angle) && box.Contains(At(angle)) || Includes(Math.PI - angle) && box.Contains(At(Math.PI - angle))) return true;
        }
        return false;
    }
    public static double Normalize(double angle) => (angle % Math.Tau + Math.Tau) % Math.Tau;
    public static Arc? Through(Vec a, Vec b, Vec c)
    {
        var ab = b - a;
        var ac = c - a;
        var determinant = 2 * ab.Cross(ac);
        if (Math.Abs(determinant) < 1e-10 * ab.Length * ac.Length || determinant == 0) return null;
        var relative = (ab.Perpendicular * ac.Dot(ac) - ac.Perpendicular * ab.Dot(ab)) / determinant;
        var center = a + relative;
        var start = Math.Atan2(-relative.Y, -relative.X);
        var middle = Normalize(Math.Atan2(b.Y - center.Y, b.X - center.X) - start);
        var end = Normalize(Math.Atan2(c.Y - center.Y, c.X - center.X) - start);
        return new(center, relative.Length, start, middle <= end ? end : end - Math.Tau);
    }
}

internal enum TextAnchor { Left, Center, Right }

internal sealed record Note(Vec Position, string Value, double Height, double Angle = 0, TextAnchor Anchor = TextAnchor.Left) : Shape
{
    public double ApproximateWidth() => Value.Length * Height * 0.85;
    private double Offset(double width) => Anchor switch { TextAnchor.Center => -width / 2, TextAnchor.Right => -width, _ => 0 };
    public override Box Bounds
    {
        get
        {
            var width = ApproximateWidth();
            var offset = Offset(width);
            var axis = Vec.Polar(Angle);
            var left = Position + axis * offset;
            var right = left + axis * width;
            return Box.Around([left, right, right + axis.Perpendicular * Height, left + axis.Perpendicular * Height]);
        }
    }
    public override Shape Shift(Vec delta) => this with { Position = Position + delta };
    public override double Distance(Vec position)
    {
        var axis = Vec.Polar(Angle);
        var relative = position - Position;
        var x = relative.Dot(axis);
        var y = relative.Dot(axis.Perpendicular);
        var width = ApproximateWidth();
        var left = Offset(width);
        return new Vec(x - Math.Clamp(x, left, left + width), y - Math.Clamp(y, 0, Height)).Length;
    }
    public override IEnumerable<SnapPoint> Snaps() { yield return new(Position, SnapKind.End); }
}

internal sealed record Dimension(Vec A, Vec B, double Offset, double TextHeight) : Shape
{
    public IEnumerable<Shape> Graphics()
    {
        var delta = B - A;
        if (delta.Length < 1e-9) yield break;
        var tangent = delta / delta.Length;
        var normal = tangent.Perpendicular;
        var a = A + normal * Offset;
        var b = B + normal * Offset;
        var outside = normal * (Offset >= 0 ? 1 : -1);
        var overhang = TextHeight * 0.5;
        yield return new Segment(A + outside * Math.Min(overhang, Math.Abs(Offset)), a + outside * overhang);
        yield return new Segment(B + outside * Math.Min(overhang, Math.Abs(Offset)), b + outside * overhang);
        yield return new Segment(a - tangent * overhang, b + tangent * overhang);
        var tick = (tangent + normal) * (TextHeight * 0.33);
        yield return new Segment(a - tick, a + tick);
        yield return new Segment(b - tick, b + tick);
        var textAxis = tangent.X < 0 || Math.Abs(tangent.X) < 1e-9 && tangent.Y < 0 ? tangent * -1 : tangent;
        yield return new Note((a + b) / 2 + textAxis.Perpendicular * (TextHeight * 0.4),
            CadFormat.Number(delta.Length), TextHeight, Math.Atan2(textAxis.Y, textAxis.X), TextAnchor.Center);
    }
    public override Box Bounds => Graphics().Select(shape => shape.Bounds).Aggregate(Box.Between(A, B), (box, item) => box.Union(item));
    public override Shape Shift(Vec delta) => this with { A = A + delta, B = B + delta };
    public override double Distance(Vec position) => Graphics().Select(shape => shape.Distance(position)).DefaultIfEmpty(double.PositiveInfinity).Min();
    public override IEnumerable<SnapPoint> Snaps() => [new(A, SnapKind.End), new(B, SnapKind.End)];
    public override bool Crosses(Box box) => Graphics().Any(shape => shape.Crosses(box));
}

internal sealed record Layer(string Name, string Color, bool Visible = true, bool Locked = false, double Weight = 1);

internal sealed record Drawing(string Title, Layer[] Layers, Shape[] Shapes, string ActiveLayer = "0", int Units = 4)
{
    public static Drawing Empty(string title = "Чертёж 1") => new(title, [new("0", "#D7E2EC")], []);
    public Layer CurrentLayer => Layers.First(layer => layer.Name == ActiveLayer);
    public Drawing Add(params Shape[] shapes) => this with { Shapes = [.. Shapes, .. shapes] };
}

internal static class CadFormat
{
    private static readonly NumberFormatInfo DisplayNumbers = new()
    {
        NumberGroupSeparator = " ", NumberDecimalSeparator = "."
    };
    public static string Number(double value) => value.ToString("#,0.##", DisplayNumbers);
    public static bool TryNumber(string input, out double result) =>
        double.TryParse(input.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out result)
        && double.IsFinite(result) && Math.Abs(result) <= 1e12;
    public static string Units(int units) => units switch
    {
        1 => "дюйм", 2 => "фут", 4 => "мм", 5 => "см", 6 => "м", 7 => "км", _ => "ед."
    };
}
