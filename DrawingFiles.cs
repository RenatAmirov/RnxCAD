using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace RnxCAD;

internal static partial class DrawingFiles
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
    };
    private sealed record NativeFile(int Version, Drawing Drawing);

    public static Drawing ReadNative(string path)
    {
        using var stream = File.OpenRead(path);
        var file = JsonSerializer.Deserialize<NativeFile>(stream, JsonOptions);
        if (file is not { Version: 1, Drawing: { Layers.Length: > 0, Shapes: not null } drawing })
            throw new InvalidDataException("Неподдерживаемый формат файла RnxCAD.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();
        if (string.IsNullOrWhiteSpace(drawing.Title) || drawing.Units is < 0 or > 24 || drawing.Layers.Any(layer => layer is null
                || !ValidName(layer.Name) || !names.Add(layer.Name) || !ValidColor(layer.Color) || layer.Weight is < 0.3 or > 5)
            || !drawing.Layers.Any(layer => layer.Name == drawing.ActiveLayer) || !drawing.Layers.Any(layer => layer.Name == "0"))
            throw new InvalidDataException("Файл содержит некорректные слои или единицы.");
        var exactNames = drawing.Layers.Select(layer => layer.Name).ToHashSet();
        if (drawing.Shapes.Any(shape => shape is null || shape.Id == Guid.Empty || !ids.Add(shape.Id)
            || !exactNames.Contains(shape.Layer) || shape.Color is not null && !ValidColor(shape.Color) || !ValidShape(shape)))
            throw new InvalidDataException("Файл содержит некорректную геометрию.");
        return drawing;
    }

    public static void WriteNative(string path, Drawing drawing) => AtomicWrite(path, stream =>
        JsonSerializer.Serialize(stream, new NativeFile(1, drawing), JsonOptions));

    private static bool ValidShape(Shape shape)
    {
        static bool Scalar(double value) => double.IsFinite(value) && Math.Abs(value) <= 1e16;
        static bool Point(Vec value) => Scalar(value.X) && Scalar(value.Y);
        return shape switch
        {
            Segment line => Point(line.A) && Point(line.B),
            Polyline { Vertices.Length: >= 2 } chain => chain.Vertices.All(Point) &&
                (chain.Bulges is null || chain.Bulges.Length == chain.Vertices.Length && chain.Bulges.All(bulge => Scalar(bulge) && Math.Abs(bulge) <= 1e8)),
            Circle circle => Point(circle.Center) && Scalar(circle.Radius) && circle.Radius > 0,
            Arc arc => Point(arc.Center) && Scalar(arc.Radius) && arc.Radius > 0 && Scalar(arc.Start)
                && Scalar(arc.Sweep) && Math.Abs(arc.Sweep) is > 0 and < Math.Tau,
            Note note => Point(note.Position) && !string.IsNullOrWhiteSpace(note.Value) && !note.Value.Any(char.IsControl)
                && Scalar(note.Height) && note.Height > 0 && Scalar(note.Angle) && Enum.IsDefined(note.Anchor),
            Dimension dim => Point(dim.A) && Point(dim.B) && (dim.A - dim.B).Length > 0 && Scalar(dim.Offset) && Scalar(dim.TextHeight) && dim.TextHeight > 0,
            _ => false
        };
    }

    public static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100
        && value == value.Trim() && value.IndexOfAny(['<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`']) == -1 && !value.Any(char.IsControl);

    public static bool ValidColor(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    public static (Drawing Drawing, int Skipped) ReadDxf(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith("AutoCAD Binary DXF"u8)) throw new InvalidDataException("Требуется текстовый DXF.");
        string contents;
        try { contents = Utf8.GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException)
        {
            var raw = Encoding.Latin1.GetString(bytes);
            var match = CodePage().Match(raw);
            contents = Encoding.GetEncoding(match.Success ? int.Parse(match.Groups[1].Value, Invariant) : 1252).GetString(bytes);
        }
        var records = DxfRecords(contents).ToArray();
        if (!records.Any(record => record.Name == "EOF") || !records.Any(record => record.Name == "SECTION" && record.Value(2) == "ENTITIES"))
            throw new InvalidDataException("DXF не содержит секции ENTITIES или конца файла.");
        var section = "";
        var active = "0";
        var units = 0;
        var layers = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
        var shapes = new List<Shape>();
        var skipped = 0;
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Name == "SECTION")
            {
                section = record.Value(2);
                if (section == "HEADER")
                {
                    active = FromDxfString(record.Variable("$CLAYER", 8));
                    if (int.TryParse(record.Variable("$INSUNITS", 70), out var parsed) && parsed is >= 0 and <= 24) units = parsed;
                }
                continue;
            }
            if (record.Name == "ENDSEC") { section = ""; continue; }
            if (section == "TABLES" && record.Name == "LAYER")
            {
                var name = FromDxfString(record.Value(2));
                if (!ValidName(name)) continue;
                var flags = record.Int(70);
                var aci = record.Int(62, 7);
                var weight = record.Int(370, 25);
                layers.TryAdd(name, new Layer(name, DxfColor(record, Math.Abs(aci)), aci >= 0 && (flags & 1) == 0,
                    (flags & 4) != 0, weight > 0 ? Math.Clamp(weight / 25d, 0.4, 4) : 1));
            }
            if (section != "ENTITIES") continue;
            if (record.Int(67) == 1 || record.Int(60) == 1) { skipped++; continue; }
            Shape? shape;
            if (record.Name == "POLYLINE")
            {
                var vertices = new List<Vec>();
                var bulges = new List<double>();
                while (i + 1 < records.Length && records[i + 1].Name == "VERTEX")
                {
                    var vertex = records[++i];
                    vertices.Add(vertex.Point(10));
                    bulges.Add(vertex.Number(42));
                }
                if (i + 1 < records.Length && records[i + 1].Name == "SEQEND") i++;
                shape = (record.Int(70) & 126) != 0 || !record.Flat ? null :
                    new Polyline([.. vertices], (record.Int(70) & 1) != 0, bulges.Any(value => value != 0) ? [.. bulges] : null);
            }
            else shape = ReadDxfShape(record);
            if (shape is null || !ValidShape(shape)) { skipped++; continue; }
            var layerName = FromDxfString(record.Value(8));
            if (!ValidName(layerName)) layerName = "0";
            if (!layers.TryGetValue(layerName, out var layer)) layers.Add(layerName, layer = new Layer(layerName, "#DAE4EF"));
            var color = record.Int(62, 256);
            shapes.Add(shape with { Layer = layer.Name, Color = record.Has(420) || color is > 0 and < 256 ? DxfColor(record, color) : null });
        }
        layers.TryAdd("0", new Layer("0", "#DAE4EF"));
        active = layers.TryGetValue(active, out var current) ? current.Name : "0";
        var title = Path.GetFileNameWithoutExtension(path);
        return (new Drawing(title, [.. layers.Values.OrderBy(layer => layer.Name == "0" ? 0 : 1)], [.. shapes], active, units), skipped);
    }

    private static Shape? ReadDxfShape(DxfRecord record)
    {
        if (!record.Flat) return null;
        Shape? ReadText()
        {
            var horizontal = record.Int(72);
            var vertical = record.Int(73);
            if (horizontal is < 0 or > 2 || vertical != 0 || record.Int(71) != 0 || record.Number(51) != 0) return null;
            return new Note(record.Point(horizontal > 0 ? 11 : 10), FromDxfString(record.Value(1)), record.Number(40, double.NaN),
                record.Number(50) * Math.PI / 180, (TextAnchor)horizontal);
        }
        return record.Name switch
        {
            "LINE" => new Segment(record.Point(10), record.Point(11)),
            "CIRCLE" => new Circle(record.Point(10), record.Number(40, double.NaN)),
            "ARC" => new Arc(record.Point(10), record.Number(40, double.NaN), record.Number(50) * Math.PI / 180,
                Arc.Normalize((record.Number(51) - record.Number(50)) * Math.PI / 180)),
            "LWPOLYLINE" => ReadLightPolyline(record),
            "TEXT" => ReadText(),
            _ => null
        };
    }

    private static Polyline ReadLightPolyline(DxfRecord record)
    {
        var vertices = new List<Vec>();
        var bulges = new List<double>();
        foreach (var (code, value) in record.Groups)
        {
            if (code == 10)
            {
                vertices.Add(new Vec(ParseDxfNumber(value, double.NaN), double.NaN));
                bulges.Add(0);
            }
            else if (code == 20 && vertices.Count > 0) vertices[^1] = vertices[^1] with { Y = ParseDxfNumber(value, double.NaN) };
            else if (code == 42 && vertices.Count > 0) bulges[^1] = ParseDxfNumber(value, double.NaN);
        }
        return new Polyline([.. vertices], (record.Int(70) & 1) != 0, bulges.Any(value => value != 0) ? [.. bulges] : null);
    }

    private static double ParseDxfNumber(string value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, Invariant, out var number) ? number : fallback;

    private sealed record DxfRecord(string Name, List<(int Code, string Value)> Groups)
    {
        public bool Has(int code) => Groups.Any(group => group.Code == code);
        public string Value(int code) => Groups.FirstOrDefault(group => group.Code == code).Value ?? "";
        public double Number(int code, double fallback = 0) => ParseDxfNumber(Value(code), fallback);
        public int Int(int code, int fallback = 0) => int.TryParse(Value(code), NumberStyles.Integer, Invariant, out var number) ? number : fallback;
        public Vec Point(int code) => new(Number(code, double.NaN), Number(code + 10, double.NaN));
        public bool Flat => Number(210) == 0 && Number(220) == 0 && Number(230, 1) == 1 && Number(30) == 0 && Number(31) == 0 && Number(38) == 0;
        public string Variable(string name, int code)
        {
            var start = Groups.FindIndex(group => group.Code == 9 && group.Value == name);
            return start >= 0 ? Groups.Skip(start + 1).TakeWhile(group => group.Code != 9).FirstOrDefault(group => group.Code == code).Value ?? "" : "";
        }
    }

    private static IEnumerable<DxfRecord> DxfRecords(string contents)
    {
        using var reader = new StringReader(contents);
        DxfRecord? record = null;
        while (reader.ReadLine() is string key)
        {
            if (key.Trim().Length == 0 && reader.Peek() == -1) break;
            if (!int.TryParse(key, NumberStyles.Integer, Invariant, out var code) || reader.ReadLine() is not string value)
                throw new InvalidDataException("Некорректная пара кода и значения DXF.");
            if (code == 0)
            {
                if (record is not null) yield return record;
                record = new DxfRecord(value.Trim().ToUpperInvariant(), []);
            }
            else record?.Groups.Add((code, code == 1 ? value : value.Trim()));
        }
        if (record is not null) yield return record;
    }

    private static string DxfColor(DxfRecord record, int aci) => record.Has(420) ? $"#{record.Int(420) & 0xFFFFFF:X6}" : aci switch
    {
        1 => "#F56B63", 2 => "#F5DB64", 3 => "#80DA95", 4 => "#6DD4D6", 5 => "#829EFF", 6 => "#DA86D9",
        8 => "#808080", 9 => "#C0C0C0", >= 10 and <= 249 => IndexedColor(aci),
        >= 250 and <= 255 => new[] { "#333333", "#505050", "#696969", "#828282", "#BEBEBE", "#FFFFFF" }[aci - 250], _ => "#DAE4EF"
    };

    private static string IndexedColor(int aci)
    {
        var hue = (aci / 10 - 1) / 4d;
        var shade = aci % 10;
        var max = new[] { 255, 165, 127, 76, 38 }[shade / 2];
        var min = shade % 2 == 0 ? 0 : max / 2;
        var swing = (int)((max - min) * (1 - Math.Abs(hue % 2 - 1)));
        var (r, g, b) = (int)hue switch
        {
            0 => (max, min + swing, min), 1 => (min + swing, max, min), 2 => (min, max, min + swing),
            3 => (min, min + swing, max), 4 => (min + swing, min, max), _ => (max, min, min + swing)
        };
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static string FromDxfString(string input) => DxfUnicode().Replace(input, match =>
        ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, Invariant)).ToString());

    [GeneratedRegex(@"\\U\+([0-9A-Fa-f]{4})")]
    private static partial Regex DxfUnicode();
    [GeneratedRegex(@"\$DWGCODEPAGE\s+3\s+ANSI_(\d{3,5})")]
    private static partial Regex CodePage();

    public static void WriteDxf(string path, Drawing drawing) => AtomicWrite(path, stream =>
    {
        using var writer = new StreamWriter(stream, Utf8, leaveOpen: true) { NewLine = "\r\n" };
        var handle = 0x40;
        string Next() => (++handle).ToString("X", Invariant);
        void Pair(int code, object value) { writer.WriteLine(code.ToString(Invariant)); writer.WriteLine(Convert.ToString(value, Invariant)); }
        void Section(string name) { Pair(0, "SECTION"); Pair(2, name); }
        void EndSection() => Pair(0, "ENDSEC");
        void Point(int code, Vec position) { Pair(code, position.X); Pair(code + 10, position.Y); Pair(code + 20, 0); }
        void Subclass(string name) => Pair(100, name);
        void Table(string name, string id, int count)
        {
            Pair(0, "TABLE"); Pair(2, name); Pair(5, id); Pair(330, "0"); Subclass("AcDbSymbolTable"); Pair(70, count);
        }
        void TableRecord(string kind, string id, string owner, string subclass, string name)
        {
            Pair(0, kind); Pair(5, id); Pair(330, owner); Subclass("AcDbSymbolTableRecord"); Subclass(subclass); Pair(2, name);
        }
        void Block(string name, string id)
        {
            Pair(0, "BLOCK"); Pair(5, Next()); Pair(330, id); Subclass("AcDbEntity"); Pair(8, "0"); Subclass("AcDbBlockBegin");
            Pair(2, name); Pair(70, 0); Point(10, default); Pair(3, name); Pair(1, "");
            Pair(0, "ENDBLK"); Pair(5, Next()); Pair(330, id); Subclass("AcDbEntity"); Pair(8, "0"); Subclass("AcDbBlockEnd");
        }
        void Entity(string name, string layer, string? color)
        {
            Pair(0, name); Pair(5, Next()); Pair(330, "10"); Subclass("AcDbEntity"); Pair(8, layer);
            if (color is not null) Pair(420, int.Parse(color[1..], NumberStyles.HexNumber, Invariant));
        }
        double Degrees(double angle) => Arc.Normalize(angle) * 180 / Math.PI;
        void WriteShape(Shape shape, string layer, string? color)
        {
            switch (shape)
            {
                case Segment line:
                    Entity("LINE", layer, color); Subclass("AcDbLine"); Point(10, line.A); Point(11, line.B); break;
                case Polyline polyline:
                    Entity("LWPOLYLINE", layer, color); Subclass("AcDbPolyline"); Pair(90, polyline.Vertices.Length); Pair(70, polyline.Closed ? 1 : 0);
                    for (var index = 0; index < polyline.Vertices.Length; index++)
                    {
                        Pair(10, polyline.Vertices[index].X); Pair(20, polyline.Vertices[index].Y);
                        if (polyline.Bulges is not null && polyline.Bulges[index] != 0) Pair(42, polyline.Bulges[index]);
                    }
                    break;
                case Circle circle:
                    Entity("CIRCLE", layer, color); Subclass("AcDbCircle"); Point(10, circle.Center); Pair(40, circle.Radius); break;
                case Arc arc:
                    Entity("ARC", layer, color); Subclass("AcDbCircle"); Point(10, arc.Center); Pair(40, arc.Radius); Subclass("AcDbArc");
                    Pair(50, Degrees(arc.Sweep > 0 ? arc.Start : arc.Start + arc.Sweep));
                    Pair(51, Degrees(arc.Sweep > 0 ? arc.Start + arc.Sweep : arc.Start));
                    break;
                case Note note:
                    Entity("TEXT", layer, color); Subclass("AcDbText"); Point(10, note.Position); Pair(40, note.Height); Pair(1, note.Value);
                    Pair(50, Degrees(note.Angle)); Pair(7, "Standard"); Pair(72, (int)note.Anchor);
                    if (note.Anchor != TextAnchor.Left) Point(11, note.Position);
                    Subclass("AcDbText"); Pair(73, 0); break;
                case Dimension dim:
                    foreach (var item in dim.Graphics()) WriteShape(item, layer, color);
                    break;
            }
        }

        Section("HEADER");
        Pair(9, "$ACADVER"); Pair(1, "AC1021"); Pair(9, "$DWGCODEPAGE"); Pair(3, "ANSI_1252");
        Pair(9, "$INSUNITS"); Pair(70, drawing.Units); Pair(9, "$CLAYER"); Pair(8, drawing.ActiveLayer);
        Pair(9, "$HANDSEED"); Pair(5, (0x50L + drawing.Layers.Length + drawing.Shapes.Length * 6L).ToString("X", Invariant));
        EndSection();

        Section("TABLES");
        Table("LTYPE", "2", 3);
        foreach (var name in new[] { "ByBlock", "ByLayer", "Continuous" })
        {
            TableRecord("LTYPE", Next(), "2", "AcDbLinetypeTableRecord", name); Pair(70, 0); Pair(3, ""); Pair(72, 65); Pair(73, 0); Pair(40, 0);
        }
        Pair(0, "ENDTAB");
        Table("LAYER", "3", drawing.Layers.Length);
        foreach (var layer in drawing.Layers)
        {
            TableRecord("LAYER", Next(), "3", "AcDbLayerTableRecord", layer.Name);
            Pair(70, layer.Locked ? 4 : 0); Pair(62, layer.Visible ? 7 : -7);
            Pair(420, int.Parse(layer.Color[1..], NumberStyles.HexNumber, Invariant)); Pair(6, "Continuous");
            Pair(370, NearestLineweight(layer.Weight * 25));
        }
        Pair(0, "ENDTAB");
        Table("STYLE", "4", 1);
        TableRecord("STYLE", Next(), "4", "AcDbTextStyleTableRecord", "Standard");
        Pair(70, 0); Pair(40, 0); Pair(41, 1); Pair(50, 0); Pair(71, 0); Pair(42, 2.5); Pair(3, "arial.ttf"); Pair(4, ""); Pair(0, "ENDTAB");
        Table("BLOCK_RECORD", "5", 2);
        TableRecord("BLOCK_RECORD", "10", "5", "AcDbBlockTableRecord", "*Model_Space"); Pair(70, 0);
        TableRecord("BLOCK_RECORD", "11", "5", "AcDbBlockTableRecord", "*Paper_Space"); Pair(70, 0);
        Pair(0, "ENDTAB"); EndSection();
        Section("BLOCKS"); Block("*Model_Space", "10"); Block("*Paper_Space", "11"); EndSection();
        Section("ENTITIES"); foreach (var shape in drawing.Shapes) WriteShape(shape, shape.Layer, shape.Color); EndSection();
        Pair(0, "EOF");
    });

    private static int NearestLineweight(double weight) => new[] { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120 }
        .MinBy(value => Math.Abs(value - weight));

    public static void WriteSvg(string path, Drawing drawing) => AtomicWrite(path, stream =>
    {
        const string ns = "http://www.w3.org/2000/svg";
        var layers = drawing.Layers.Where(layer => layer.Visible).ToDictionary(layer => layer.Name);
        var visible = drawing.Shapes.Where(shape => layers.ContainsKey(shape.Layer)).ToArray();
        var bounds = visible.Length == 0 ? new Box(0, 0, 1000, 1000) : visible.Select(shape => shape.Bounds).Aggregate((a, b) => a.Union(b));
        var pad = Math.Max(Math.Max(bounds.Width, bounds.Height) * 0.04, 1e-9);
        var width = bounds.Width + pad * 2;
        var height = bounds.Height + pad * 2;
        string N(double number) => number.ToString("G17", Invariant);
        string XY(Vec point) => $"{N(point.X)} {N(-point.Y)}";
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = Utf8, Indent = true });
        void Attr(string key, string value) => xml.WriteAttributeString(key, value);
        void Num(string key, double value) => Attr(key, N(value));
        void PathShape(string data, string color, double weight)
        {
            xml.WriteStartElement("path", ns); Attr("d", data); Attr("stroke", color); Num("stroke-width", weight); xml.WriteEndElement();
        }
        string ArcCommand(Arc arc) => $"A {N(arc.Radius)} {N(arc.Radius)} 0 {(Math.Abs(arc.Sweep) > Math.PI ? 1 : 0)} {(arc.Sweep < 0 ? 1 : 0)} {XY(arc.At(arc.Start + arc.Sweep))}";
        void WriteShape(Shape shape, string color, double weight)
        {
            switch (shape)
            {
                case Segment line:
                    PathShape($"M {XY(line.A)} L {XY(line.B)}", color, weight); break;
                case Polyline chain:
                    var builder = new StringBuilder($"M {XY(chain.Vertices[0])}");
                    foreach (var edge in chain.Edges()) builder.Append(edge is Arc arc ? $" {ArcCommand(arc)}" : $" L {XY(((Segment)edge).B)}");
                    if (chain.Closed) builder.Append(" Z");
                    PathShape(builder.ToString(), color, weight); break;
                case Circle circle:
                    xml.WriteStartElement("circle", ns); Num("cx", circle.Center.X); Num("cy", -circle.Center.Y); Num("r", circle.Radius);
                    Attr("stroke", color); Num("stroke-width", weight); xml.WriteEndElement(); break;
                case Arc arc:
                    PathShape($"M {XY(arc.At(arc.Start))} {ArcCommand(arc)}", color, weight); break;
                case Note note:
                    xml.WriteStartElement("text", ns); Num("x", note.Position.X); Num("y", -note.Position.Y); Num("font-size", note.Height * 1.22);
                    Attr("fill", color); Attr("font-family", "monospace");
                    if (note.Angle != 0) Attr("transform", $"rotate({N(-note.Angle * 180 / Math.PI)} {XY(note.Position)})");
                    if (note.Anchor != TextAnchor.Left) Attr("text-anchor", note.Anchor == TextAnchor.Center ? "middle" : "end");
                    xml.WriteString(note.Value); xml.WriteEndElement(); break;
                case Dimension dim:
                    foreach (var part in dim.Graphics()) WriteShape(part, color, weight);
                    break;
            }
        }
        xml.WriteStartElement("svg", ns);
        Attr("viewBox", $"{N(bounds.Left - pad)} {N(-bounds.Top - pad)} {N(width)} {N(height)}");
        Num("width", 1400); Num("height", Math.Max(1, 1400 * height / width)); Attr("fill", "none"); Attr("stroke-linejoin", "round");
        xml.WriteElementString("title", ns, drawing.Title);
        xml.WriteStartElement("rect", ns); Num("x", bounds.Left - pad); Num("y", -bounds.Top - pad); Num("width", width); Num("height", height);
        Attr("fill", "#121D2A"); xml.WriteEndElement();
        foreach (var shape in visible)
        {
            var layer = layers[shape.Layer];
            WriteShape(shape, shape.Color ?? layer.Color, layer.Weight * width / 1000);
        }
        xml.WriteEndElement();
    });

    private static void AtomicWrite(string path, Action<Stream> write)
    {
        var finalPath = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(finalPath)!, $".rnxcad-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write(stream);
            File.Move(temporary, finalPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
