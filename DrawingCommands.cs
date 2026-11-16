using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace RnxCAD;

public partial class MainWindow
{
    private bool awaitingSelection;
    private double? textHeight;
    private const string Ready = "Выберите объект или запустите команду.";

    private void Activate(Tool action)
    {
        tool = action;
        first = second = null;
        chain.Clear();
        textHeight = null;
        awaitingSelection = action is Tool.Move or Tool.Copy && selected.Count == 0;
        if (action is not (Tool.Select or Tool.Pan or Tool.Move or Tool.Copy))
        {
            if (!drawing.CurrentLayer.Visible || drawing.CurrentLayer.Locked)
            {
                tool = Tool.Select;
                ApplyToolMode();
                Log("Для рисования выберите видимый и разблокированный слой.");
                return;
            }
            selected.Clear();
            RefreshProperties();
        }
        if (tool is not (Tool.Select or Tool.Pan)) lastTool = tool;
        ApplyToolMode();
        UpdatePrompt();
        Surface.Focus();
    }

    private void ApplyToolMode()
    {
        Surface.PanningTool = tool == Tool.Pan;
        Surface.Selecting = tool == Tool.Select || awaitingSelection;
        Surface.Cursor = tool == Tool.Pan ? Cursors.Hand : Cursors.None;
        Surface.CursorPoint = null;
        Surface.Snap = null;
        Surface.Preview = [];
        foreach (var (buttonTool, button) in toolButtons)
        {
            button.Background = buttonTool == tool ? (System.Windows.Media.Brush)FindResource("Active") : System.Windows.Media.Brushes.Transparent;
            button.BorderBrush = buttonTool == tool ? CadSurface.BrushFor("#CADAAB") : System.Windows.Media.Brushes.Transparent;
        }
        Surface.InvalidateVisual();
    }

    private IEnumerable<Shape> EditableShapes()
    {
        var layers = drawing.Layers.Where(layer => layer.Visible && !layer.Locked).Select(layer => layer.Name).ToHashSet();
        return drawing.Shapes.Where(shape => layers.Contains(shape.Layer));
    }

    private void CanvasClicked(Vec position, bool additive)
    {
        if (!Surface.Selecting)
        {
            AcceptPoint(SnapTo(position, out _));
            return;
        }
        var tolerance = Surface.UnitsPerPixel * 7;
        var hit = EditableShapes().Reverse().Select(shape => (shape, distance: shape.Distance(position)))
            .Where(candidate => candidate.distance <= tolerance).MinBy(candidate => candidate.distance).shape;
        if (!additive) selected.Clear();
        if (hit is not null && !selected.Add(hit.Id) && additive) selected.Remove(hit.Id);
        SelectionChanged();
    }

    private void SelectBox(Box box, bool crossing, bool additive)
    {
        if (!additive) selected.Clear();
        foreach (var shape in EditableShapes())
            if (crossing ? shape.Crosses(box) : box.Contains(shape.Bounds)) selected.Add(shape.Id);
        SelectionChanged();
    }

    private void SelectAll()
    {
        if (tool != Tool.Select && !awaitingSelection) Activate(Tool.Select);
        selected.UnionWith(EditableShapes().Select(shape => shape.Id));
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        RefreshProperties();
        Surface.InvalidateVisual();
        if (tool == Tool.Select || awaitingSelection) UpdatePrompt();
    }

    private void DeleteSelection()
    {
        if (selected.Count == 0)
        {
            Log("Выделите объекты для удаления.");
            return;
        }
        var count = selected.Count;
        Commit(drawing with { Shapes = drawing.Shapes.Where(shape => !selected.Contains(shape.Id)).ToArray() });
        Activate(Tool.Select);
        Log($"Удалено объектов: {count}.");
    }

    private void FitDrawing()
    {
        Surface.Fit();
        Log("Показан весь чертёж.");
    }

    private Vec SnapTo(Vec position, out SnapPoint? snap)
    {
        snap = null;
        if (Surface.Selecting || tool == Tool.Pan) return position;
        if (SnapButton.IsChecked == true)
        {
            var tolerance = 10 * Surface.UnitsPerPixel;
            var visible = drawing.Layers.Where(layer => layer.Visible).Select(layer => layer.Name).ToHashSet();
            foreach (var shape in drawing.Shapes)
            {
                if (!visible.Contains(shape.Layer)) continue;
                var bounds = shape.Bounds;
                if (!new Box(bounds.Left - tolerance, bounds.Bottom - tolerance, bounds.Right + tolerance, bounds.Top + tolerance).Contains(position)
                    && shape is not Arc && shape is not Polyline) continue;
                foreach (var candidate in shape.Snaps())
                {
                    var distance = (candidate.Point - position).Length;
                    if (distance >= tolerance) continue;
                    tolerance = distance;
                    snap = candidate;
                }
            }
        }
        if (snap is SnapPoint match) return match.Point;
        var origin = tool == Tool.Polyline && chain.Count > 0 ? chain[^1] : first;
        if (OrthoButton.IsChecked == true && origin is Vec anchor && tool is Tool.Line or Tool.Polyline or Tool.Move or Tool.Copy)
        {
            var delta = position - anchor;
            return Math.Abs(delta.X) >= Math.Abs(delta.Y) ? new(position.X, anchor.Y) : new(anchor.X, position.Y);
        }
        return position;
    }

    private void UpdatePointer(Vec world)
    {
        cursor = SnapTo(world, out var snap);
        Surface.CursorPoint = cursor;
        Surface.Snap = snap;
        Surface.Preview = BuildPreview(cursor);
        CoordinateText.Text = $"X {CadFormat.Number(cursor.X)}    Y {CadFormat.Number(cursor.Y)}";
    }

    private Shape[] BuildPreview(Vec position)
    {
        if (tool == Tool.Polyline && chain.Count > 0) return [new Polyline([.. chain, position])];
        if (first is not Vec start || (position - start).Length < 1e-9) return [];
        return tool switch
        {
            Tool.Line => [new Segment(start, position)],
            Tool.Rectangle => [Polyline.Rectangle(start, position)],
            Tool.Circle => [new Circle(start, (position - start).Length)],
            Tool.Arc when second is Vec middle && Arc.Through(start, middle, position) is Arc arc => [arc],
            Tool.Arc => [new Segment(second ?? start, position)],
            Tool.Dimension when second is Vec end => [DimensionAt(start, end, position)],
            Tool.Dimension => [new Segment(start, position)],
            Tool.Move or Tool.Copy => drawing.Shapes.Where(shape => selected.Contains(shape.Id)).Select(shape => shape.Shift(position - start)).ToArray(),
            _ => []
        };
    }

    private Dimension DimensionAt(Vec start, Vec end, Vec position)
    {
        var axis = end - start;
        var offset = (position - start).Dot(axis.Perpendicular) / axis.Length;
        return new Dimension(start, end, offset, Surface.UnitsPerPixel * 10);
    }

    private void AcceptPoint(Vec point)
    {
        if (tool == Tool.Polyline)
        {
            if (chain.Count == 0 || (point - chain[^1]).Length > 1e-9) chain.Add(point);
            else Log("Укажите другую точку.");
            UpdatePrompt();
            return;
        }
        if (tool is Tool.Select or Tool.Pan || awaitingSelection) return;
        if (first is null)
        {
            first = point;
            UpdatePrompt();
            if (tool == Tool.Text) FocusCommand();
            return;
        }
        var start = first.Value;
        if (tool == Tool.Text)
        {
            FocusCommand();
            return;
        }
        if ((tool != Tool.Dimension || second is null) && (point - start).Length < 1e-9)
        {
            Log("Укажите другую точку.");
            return;
        }
        switch (tool)
        {
            case Tool.Line:
                if (CreateShape(new Segment(start, point), false)) first = point;
                break;
            case Tool.Rectangle:
                if (Math.Abs(start.X - point.X) < 1e-9 || Math.Abs(start.Y - point.Y) < 1e-9)
                {
                    Log("У прямоугольника должны быть ненулевые стороны.");
                    return;
                }
                CreateShape(Polyline.Rectangle(start, point));
                break;
            case Tool.Circle:
                CreateShape(new Circle(start, (point - start).Length));
                break;
            case Tool.Arc:
            case Tool.Dimension:
                if (second is null) second = point;
                else if (tool == Tool.Dimension) CreateShape(DimensionAt(start, second.Value, point));
                else if (Arc.Through(start, second.Value, point) is Arc arc) CreateShape(arc);
                else
                {
                    Log("Укажите три разные точки, образующие дугу.");
                    return;
                }
                break;
            case Tool.Move:
            case Tool.Copy:
                TransformSelection(point - start);
                break;
        }
        if (tool != Tool.Select) UpdatePrompt();
        Surface.Preview = [];
        Surface.InvalidateVisual();
    }

    private bool CreateShape(Shape shape, bool finish = true)
    {
        if (!drawing.CurrentLayer.Visible || drawing.CurrentLayer.Locked)
        {
            Log("Активный слой скрыт или заблокирован. Выберите другой слой.");
            return false;
        }
        Commit(drawing.Add(shape with { Layer = drawing.ActiveLayer }));
        if (finish)
        {
            Activate(Tool.Select);
            Log($"Создан объект «{ShapeName(shape)}».");
        }
        return true;
    }

    private void TransformSelection(Vec delta)
    {
        if (selected.Count == 0)
        {
            Activate(tool);
            Log("Выделите объекты и нажмите Enter.");
            return;
        }
        var copy = tool == Tool.Copy;
        if (copy)
        {
            var copies = drawing.Shapes.Where(shape => selected.Contains(shape.Id))
                .Select(shape => shape.Shift(delta) with { Id = Guid.NewGuid() }).ToArray();
            selected.Clear();
            selected.UnionWith(copies.Select(shape => shape.Id));
            Commit(drawing.Add(copies));
        }
        else Commit(drawing with { Shapes = drawing.Shapes.Select(shape => selected.Contains(shape.Id) ? shape.Shift(delta) : shape).ToArray() });
        Activate(Tool.Select);
        Log(copy ? "Выделенные объекты скопированы." : "Выделенные объекты перенесены.");
    }

    private void ConfirmTool()
    {
        if (awaitingSelection)
        {
            if (selected.Count == 0)
            {
                Log("Выделите хотя бы один объект.");
                return;
            }
            awaitingSelection = false;
            ApplyToolMode();
            UpdatePrompt();
            return;
        }
        if (tool == Tool.Polyline && chain.Count >= 2)
        {
            CreateShape(new Polyline([.. chain]));
            return;
        }
        if (tool == Tool.Select) Activate(lastTool);
        else Activate(Tool.Select);
    }

    private void UpdatePrompt()
    {
        var instruction = awaitingSelection ? $"{selected.Count} выбрано. Выберите объекты и нажмите Enter." : tool switch
        {
            Tool.Select => selected.Count == 0 ? Ready : $"Выделено: {selected.Count}. Shift добавляет объекты, Delete удаляет.",
            Tool.Pan => "Перетащите чертёж мышью. Esc завершает панорамирование.",
            Tool.Line => first is null ? "ОТРЕЗОК · Укажите начальную точку." : "ОТРЕЗОК · Следующая точка. Enter завершает построение.",
            Tool.Polyline => chain.Count == 0 ? "ПОЛИЛИНИЯ · Укажите начальную точку." : "ПОЛИЛИНИЯ · Следующая точка. Enter — готово, CLOSE — замкнуть.",
            Tool.Rectangle => first is null ? "ПРЯМОУГОЛЬНИК · Укажите первый угол." : "ПРЯМОУГОЛЬНИК · Укажите противоположный угол.",
            Tool.Circle => first is null ? "КРУГ · Укажите центр." : "КРУГ · Укажите точку на окружности или введите радиус.",
            Tool.Arc => first is null ? "ДУГА · Укажите начало." : second is null ? "ДУГА · Укажите промежуточную точку." : "ДУГА · Укажите конец.",
            Tool.Dimension => first is null ? "РАЗМЕР · Укажите первую точку." : second is null ? "РАЗМЕР · Укажите вторую точку." : "РАЗМЕР · Укажите положение размерной линии.",
            Tool.Text => first is null ? "ТЕКСТ · Укажите точку вставки." : textHeight is null ? "ТЕКСТ · Введите надпись или сначала укажите её высоту." : "ТЕКСТ · Введите надпись.",
            Tool.Move => first is null ? "ПЕРЕНОС · Укажите базовую точку." : "ПЕРЕНОС · Укажите новое положение базовой точки.",
            Tool.Copy => first is null ? "КОПИРОВАНИЕ · Укажите базовую точку." : "КОПИРОВАНИЕ · Укажите новое положение базовой точки.",
            _ => Ready
        };
        PromptText.Text = instruction;
        if (string.IsNullOrEmpty(CoordinateText.Text)) CoordinateText.Text = "X 0    Y 0";
    }

    private void Log(string message)
    {
        messages.Add(message);
        if (messages.Count > 250) messages.RemoveAt(0);
        PromptText.Text = message;
    }

    private void FocusCommand()
    {
        CommandBox.Focus();
        CommandBox.CaretIndex = CommandBox.Text.Length;
    }

    private void Command_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CommandHint is not null)
            CommandHint.Visibility = CommandBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Command_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var input = CommandBox.Text.Trim();
            CommandBox.Clear();
            if (input.Length == 0) ConfirmTool();
            else
            {
                enteredCommands.Add(input);
                if (enteredCommands.Count > 100) enteredCommands.RemoveAt(0);
                commandHistoryPosition = enteredCommands.Count;
                messages.Add($"> {input}");
                ExecuteCommand(input);
            }
            if (tool != Tool.Select && tool != Tool.Pan) FocusCommand();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            commandHistoryPosition = Math.Clamp(commandHistoryPosition + (e.Key == Key.Up ? -1 : 1), 0, enteredCommands.Count);
            CommandBox.Text = commandHistoryPosition < enteredCommands.Count ? enteredCommands[commandHistoryPosition] : "";
            CommandBox.CaretIndex = CommandBox.Text.Length;
            e.Handled = true;
        }
    }

    private void ExecuteCommand(string input)
    {
        if (tool == Tool.Text && first is Vec insertion)
        {
            if (textHeight is null && CadFormat.TryNumber(input, out var size))
            {
                if (size > 0) { textHeight = size; UpdatePrompt(); }
                else Log("Высота текста должна быть больше нуля.");
            }
            else if (input.Any(char.IsControl)) Log("Введите надпись в одну строку.");
            else CreateShape(new Note(insertion, input, textHeight ?? Surface.UnitsPerPixel * 12));
            return;
        }
        var name = input.ToUpperInvariant();
        if (Commands.TryGetValue(name, out var command))
        {
            Activate(command);
            return;
        }
        switch (name)
        {
            case "E": case "ERASE": case "DELETE": case "УДАЛИТЬ": DeleteSelection(); return;
            case "U": case "UNDO": Undo(); return;
            case "REDO": Redo(); return;
            case "Z": case "ZOOM": case "ZOOM EXTENTS": case "ZE": FitDrawing(); return;
            case "GRID": Toggle(GridButton); return;
            case "ORTHO": Toggle(OrthoButton); return;
            case "OSNAP": Toggle(SnapButton); return;
            case "SAVE": case "QSAVE": SaveDrawing(); return;
            case "NEW": NewDrawing(); return;
            case "OPEN": OpenDrawing(); return;
            case "HELP": ShowHelp(); return;
            case "CLOSE" or "ЗАМКНУТЬ" when tool == Tool.Polyline && chain.Count >= 3:
                CreateShape(new Polyline([.. chain], true)); return;
            case "CLOSE" or "ЗАМКНУТЬ" when tool == Tool.Polyline:
                Log("Для замкнутой полилинии нужны как минимум три вершины."); return;
        }
        if (tool == Tool.Circle && first is Vec center && CadFormat.TryNumber(input, out var radius))
        {
            if (radius > 0) CreateShape(new Circle(center, radius));
            else Log("Радиус должен быть больше нуля.");
            return;
        }
        if (tool is not (Tool.Select or Tool.Pan) && !awaitingSelection)
        {
            if (TryPoint(input, out var point)) AcceptPoint(point);
            else Log("Введите координаты как X,Y или @ΔX,ΔY. Дробные числа с запятой разделяйте знаком «;».");
        }
        else Log($"Неизвестная команда «{input}». Список команд — F1.");
    }

    private bool TryPoint(string input, out Vec point)
    {
        point = default;
        var relative = input.StartsWith('@');
        var coordinates = relative ? input[1..].Trim() : input;
        var origin = tool == Tool.Polyline && chain.Count > 0 ? chain[^1] : second ?? first;
        if (relative && origin is null) return false;
        if (relative && coordinates.Contains('<'))
        {
            var polar = coordinates.Split('<');
            if (polar.Length != 2 || !CadFormat.TryNumber(polar[0], out var distance) || !CadFormat.TryNumber(polar[1], out var angle)) return false;
            point = origin!.Value + Vec.Polar(angle * Math.PI / 180, distance);
            return true;
        }
        var parts = coordinates.Split(coordinates.Contains(';') ? ';' : ',');
        if (parts.Length != 2 || !CadFormat.TryNumber(parts[0], out var x) || !CadFormat.TryNumber(parts[1], out var y)) return false;
        point = new Vec(x, y) + (relative ? origin!.Value : default);
        return true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var inText = Keyboard.FocusedElement is TextBoxBase;
        if (e.Key == Key.Escape)
        {
            CommandBox.Clear();
            if (tool == Tool.Select) selected.Clear();
            Activate(Tool.Select);
            RefreshProperties();
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.N: NewDrawing(); break;
                case Key.O: OpenDrawing(); break;
                case Key.S: SaveDrawing(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); break;
                case Key.Z: Undo(); break;
                case Key.Y: Redo(); break;
                case Key.A when !inText: SelectAll(); break;
                case Key.L: FocusCommand(); break;
                default: return;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.F1: ShowHelp(); break;
                case Key.F2: ShowHistory(); break;
                case Key.F3: Toggle(SnapButton); break;
                case Key.F7: Toggle(GridButton); break;
                case Key.F8: Toggle(OrthoButton); break;
                case Key.Home when !inText && Surface.IsKeyboardFocused: FitDrawing(); break;
                case Key.Delete when !inText: DeleteSelection(); break;
                case Key.Enter when Surface.IsKeyboardFocused: ConfirmTool(); break;
                case Key.Space when Surface.IsKeyboardFocused && tool == Tool.Select: Activate(lastTool); break;
                default: return;
            }
        }
        e.Handled = true;
    }

    private void Toggle(ToggleButton button)
    {
        button.IsChecked = button.IsChecked != true;
        UpdateOptions();
    }

    private void UpdateOptions()
    {
        Surface.GridEnabled = GridButton.IsChecked == true;
        Surface.InvalidateVisual();
    }

    private static readonly Dictionary<string, Tool> Commands = new()
    {
        ["L"] = Tool.Line, ["LINE"] = Tool.Line, ["ОТРЕЗОК"] = Tool.Line,
        ["PL"] = Tool.Polyline, ["PLINE"] = Tool.Polyline, ["ПОЛИЛИНИЯ"] = Tool.Polyline,
        ["REC"] = Tool.Rectangle, ["RECTANG"] = Tool.Rectangle, ["RECTANGLE"] = Tool.Rectangle, ["ПРЯМОУГОЛЬНИК"] = Tool.Rectangle,
        ["C"] = Tool.Circle, ["CIRCLE"] = Tool.Circle, ["КРУГ"] = Tool.Circle,
        ["A"] = Tool.Arc, ["ARC"] = Tool.Arc, ["ДУГА"] = Tool.Arc,
        ["DIM"] = Tool.Dimension, ["РАЗМЕР"] = Tool.Dimension,
        ["T"] = Tool.Text, ["TEXT"] = Tool.Text, ["ТЕКСТ"] = Tool.Text,
        ["M"] = Tool.Move, ["MOVE"] = Tool.Move, ["ПЕРЕНЕСТИ"] = Tool.Move,
        ["CO"] = Tool.Copy, ["COPY"] = Tool.Copy, ["КОПИРОВАТЬ"] = Tool.Copy,
        ["PAN"] = Tool.Pan, ["SELECT"] = Tool.Select, ["ВЫБОР"] = Tool.Select
    };
}
