namespace RnxCAD;

internal static class DemoDrawing
{
    public static Drawing Create()
    {
        const string walls = "Стены";
        const string openings = "Проёмы";
        const string furniture = "Мебель";
        const string dimensions = "Размеры";
        const string notes = "Аннотации";
        Layer[] layers =
        [
            new("0", "#DAE4EF"), new(walls, "#D9E3EF", Weight: 1.7), new(openings, "#F0BF85", Weight: 1.2),
            new(furniture, "#70B9BA"), new(dimensions, "#7E98B1", Weight: 0.8), new(notes, "#B4C9DB")
        ];
        var shapes = new List<Shape>();

        void Line(string layer, double x1, double y1, double x2, double y2) =>
            shapes.Add(new Segment(new(x1, y1), new(x2, y2)) { Layer = layer });
        void Rect(string layer, double x1, double y1, double x2, double y2) =>
            shapes.Add(Polyline.Rectangle(new(x1, y1), new(x2, y2)) with { Layer = layer });
        void Path(string layer, params Vec[] points) => shapes.Add(new Polyline(points) { Layer = layer });
        void CircleAt(string layer, double x, double y, double r) => shapes.Add(new Circle(new(x, y), r) { Layer = layer });
        void ArcAt(string layer, double x, double y, double r, double start, double sweep) =>
            shapes.Add(new Arc(new(x, y), r, start * Math.PI / 180, sweep * Math.PI / 180) { Layer = layer });
        void Dim(double x1, double y1, double x2, double y2, double offset) =>
            shapes.Add(new Dimension(new(x1, y1), new(x2, y2), offset, 190) { Layer = dimensions });
        void Label(double x, double y, string name, string area)
        {
            shapes.Add(new Note(new(x, y), name, 210, Anchor: TextAnchor.Center) { Layer = notes });
            shapes.Add(new Note(new(x, y - 300), area, 178, Anchor: TextAnchor.Center) { Layer = notes, Color = "#91A9BD" });
        }

        // External walls wrap continuously around the corners and stop at the openings.
        Path(walls, new(0, 1400), new(0, 0), new(4450, 0), new(4450, 240), new(240, 240), new(240, 1400), new(0, 1400));
        Path(walls, new(5750, 0), new(12400, 0), new(12400, 1100), new(12160, 1100), new(12160, 240), new(5750, 240), new(5750, 0));
        Path(walls, new(0, 6150), new(0, 7800), new(1020, 7800), new(1020, 7560), new(240, 7560), new(240, 6150), new(0, 6150));
        Path(walls, new(12400, 6300), new(12400, 7800), new(11250, 7800), new(11250, 7560), new(12160, 7560), new(12160, 6300), new(12400, 6300));
        Rect(walls, 0, 3300, 240, 4350);
        Rect(walls, 12160, 3000, 12400, 4500);
        Rect(walls, 3450, 7560, 4300, 7800);
        Rect(walls, 8080, 7560, 8870, 7800);

        void Window(double x1, double y1, double x2, double y2, bool horizontal)
        {
            Rect(openings, x1, y1, x2, y2);
            if (horizontal) Line(openings, x1, (y1 + y2) / 2, x2, (y1 + y2) / 2);
            else Line(openings, (x1 + x2) / 2, y1, (x1 + x2) / 2, y2);
        }
        foreach (var (a, b) in new[] { (1020, 3450), (4300, 8080), (8870, 11250) })
        {
            Window(a, 7560, b, 7800, true);
            var middle = (a + b) / 2d;
            Line(openings, middle, 7560, middle, 7800);
        }
        Window(0, 1400, 240, 3300, false);
        Window(0, 4350, 240, 6150, false);
        Window(12160, 1100, 12400, 3000, false);
        Window(12160, 4500, 12400, 6300, false);

        Rect(walls, 3880, 4920, 4040, 7560);
        Rect(walls, 240, 4760, 840, 4920);
        Rect(walls, 1740, 4760, 4040, 4920);
        Rect(walls, 8220, 4920, 8380, 7560);
        Rect(walls, 8220, 4760, 10190, 4920);
        Rect(walls, 11090, 4760, 12160, 4920);
        Rect(walls, 7990, 240, 8150, 920);
        Rect(walls, 7990, 1860, 8150, 2950);
        Rect(walls, 8150, 2790, 12160, 2950);
        Rect(walls, 10060, 240, 10220, 2790);

        Line(openings, 840, 4920, 840, 5820);
        ArcAt(openings, 840, 4920, 900, 0, 90);
        Line(openings, 11090, 4920, 11090, 5820);
        ArcAt(openings, 11090, 4920, 900, 90, 90);
        Line(openings, 8150, 920, 9090, 920);
        ArcAt(openings, 8150, 920, 940, 0, 90);
        Line(openings, 4450, 240, 4450, 890);
        ArcAt(openings, 4450, 240, 650, 0, 90);
        Line(openings, 5750, 240, 5750, 890);
        ArcAt(openings, 5750, 240, 650, 90, 90);
        Line(openings, 4450, 55, 5750, 55);

        Rect(furniture, 1160, 6020, 3050, 7070);
        foreach (var x in new[] { 1560, 2660 })
        {
            CircleAt(furniture, x, 7320, 177);
            CircleAt(furniture, x, 5770, 177);
        }
        CircleAt(furniture, 900, 6545, 175);
        CircleAt(furniture, 3310, 6545, 175);
        Rect(furniture, 4850, 5860, 7370, 7000);
        Rect(furniture, 4945, 5955, 7275, 6905);
        Line(furniture, 6110, 5955, 6110, 6905);
        foreach (var x in new[] { 5390, 6840 })
        {
            CircleAt(furniture, x, 7275, 160);
            CircleAt(furniture, x, 5585, 160);
        }
        Rect(furniture, 8650, 6675, 11775, 7310);
        foreach (var x in new[] { 9450, 10050, 10650 }) Line(furniture, x, 6675, x, 7310);
        Rect(furniture, 11000, 6770, 11585, 7200);
        CircleAt(furniture, 11292, 6985, 100);
        Rect(furniture, 8800, 5500, 10150, 6040);
        foreach (var x in new[] { 9070, 9750 }) CircleAt(furniture, x, 6290, 160);

        Rect(furniture, 650, 1400, 1390, 3690);
        Rect(furniture, 750, 1550, 1270, 3540);
        Line(furniture, 750, 2545, 1270, 2545);
        Rect(furniture, 1700, 3380, 3190, 4080);
        Rect(furniture, 1820, 3490, 3070, 3880);
        Line(furniture, 2450, 3490, 2450, 3880);
        CircleAt(furniture, 2390, 2450, 465);
        CircleAt(furniture, 2390, 2450, 380);
        Rect(furniture, 3270, 1975, 3810, 2800);
        Rect(furniture, 9440, 1180, 9890, 1630);
        CircleAt(furniture, 9670, 1405, 128);
        Rect(furniture, 10220, 2070, 12160, 2790);
        foreach (var x in new[] { 10860, 11510 }) Line(furniture, x, 2070, x, 2790);
        Rect(furniture, 11460, 420, 11950, 930);
        CircleAt(furniture, 11705, 675, 138);

        Label(2660, 5310, "ПЕРЕГОВОРНАЯ", "01 / 9.7 м²");
        Label(6120, 5310, "СТУДИЯ", "02 / 14.4 м²");
        Label(9670, 5310, "КУХНЯ", "03 / 10.0 м²");
        Label(5700, 2820, "ЛАУНЖ", "04 / 30.6 м²");
        Label(11150, 1380, "СКЛАД", "05 / 4.9 м²");

        Dim(0, 7800, 12400, 7800, 1310);
        Dim(0, 7800, 3960, 7800, 640);
        Dim(3960, 7800, 8300, 7800, 640);
        Dim(8300, 7800, 12400, 7800, 640);
        Dim(0, 0, 0, 7800, 1200);
        Dim(0, 0, 0, 4840, 610);
        Dim(0, 4840, 0, 7800, 610);
        Dim(0, 0, 4450, 0, -640);
        Dim(4450, 0, 5750, 0, -640);
        Dim(5750, 0, 12400, 0, -640);
        Path(dimensions, new(5100, -1220), new(5250, -1460), new(4950, -1460), new(5100, -1220));
        shapes.Add(new Note(new(5100, -1850), "ВХОД", 196, Anchor: TextAnchor.Center) { Layer = dimensions });
        return new("Павильон А–01", layers, [.. shapes], walls);
    }
}
