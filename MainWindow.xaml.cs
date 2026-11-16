using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace RnxCAD;

public partial class MainWindow : Window
{
    private enum Tool { Select, Pan, Line, Polyline, Rectangle, Circle, Arc, Dimension, Text, Move, Copy }

    private readonly Stack<Drawing> undo = new();
    private readonly Stack<Drawing> redo = new();
    private readonly HashSet<Guid> selected = [];
    private readonly Dictionary<Tool, Button> toolButtons = new();
    private readonly List<string> messages = [];
    private readonly List<string> enteredCommands = [];
    private Drawing drawing = Drawing.Empty();
    private Drawing? savedDrawing;
    private string? filePath;
    private Tool tool;
    private Tool lastTool = Tool.Line;
    private Vec? first;
    private Vec? second;
    private readonly List<Vec> chain = [];
    private Vec cursor;
    private int commandHistoryPosition;

    public MainWindow(string? startupFile = null)
    {
        InitializeComponent();
        LoadInterface();
        Surface.Selected = selected;
        Surface.PointerMoved += UpdatePointer;
        Surface.PointClicked += CanvasClicked;
        Surface.BoxSelected += SelectBox;
        Surface.ToolConfirmed += ConfirmTool;
        PreviewKeyDown += Window_KeyDown;
        Closing += CheckClosing;
        StateChanged += (_, _) => SetIcon(MaxButton, WindowState == WindowState.Maximized ? "restore" : "maximize",
            WindowState == WindowState.Maximized ? "Восстановить окно" : "Развернуть окно", 13);
        ReplaceDrawing(DemoDrawing.Create());
        if (!string.IsNullOrWhiteSpace(startupFile))
            Loaded += (_, _) => ReadDrawing(startupFile);
    }

    private void LoadInterface()
    {
        TitleIcon.Content = BuildIcon("file", 14);
        LayersIcon.Content = BuildIcon("layers", 17);
        PropertiesIcon.Content = BuildIcon("properties", 17);
        ModelIcon.Content = BuildIcon("model", 13);
        SetIcon(QuickOpen, "open", "Открыть чертёж (Ctrl+O)", 17);
        SetIcon(QuickSave, "save", "Сохранить чертёж (Ctrl+S)", 17);
        SetIcon(QuickUndo, "undo", "Отменить (Ctrl+Z)", 17);
        SetIcon(QuickRedo, "redo", "Повторить (Ctrl+Y)", 17);
        ExportButton.Content = IconLabel("export", "Экспорт", 15);
        ExportButton.FontWeight = FontWeights.SemiBold;
        SetIcon(MinButton, "minus", "Свернуть окно", 13);
        SetIcon(MaxButton, "maximize", "Развернуть окно", 13);
        SetIcon(CloseButton, "close", "Закрыть окно", 13);
        SetIcon(HelpButton, "help", "Команды и сочетания клавиш (F1)", 19);
        SetIcon(AddLayerButton, "plus", "Создать слой", 16);
        SetIcon(EditLayerButton, "edit", "Изменить активный слой", 15);
        SetIcon(HistoryButton, "history", "История команд (F2)", 13);
        SetIcon(CommandFocus, "terminal", "Командная строка (Ctrl+L)", 19);

        AddRibbon(Tool.Line, "line", "Отрезок", "LINE / L");
        AddRibbon(Tool.Polyline, "polyline", "Полилиния", "PLINE / PL");
        AddRibbon(Tool.Rectangle, "rectangle", "Прямоуг.", "RECTANG / REC");
        AddRibbon(Tool.Circle, "circle", "Круг", "CIRCLE / C");
        AddRibbon(Tool.Arc, "arc", "Дуга", "ARC / A");
        AddRibbon(Tool.Dimension, "dimension", "Размер", "DIM");
        AddRibbon(Tool.Text, "text", "Текст", "TEXT / T");
        AddRibbon(Tool.Move, "move", "Перенести", "MOVE / M", EditTools);
        AddRibbon(Tool.Copy, "copy", "Копировать", "COPY / CO", EditTools);
        RibbonButton(EditTools, "delete", "Удалить", "Удалить выделенное (Delete)", DeleteSelection);

        toolButtons[Tool.Select] = AddRail("select", "Выбор (Esc)", () => Activate(Tool.Select));
        toolButtons[Tool.Pan] = AddRail("hand", "Панорама (средняя кнопка мыши)", () => Activate(Tool.Pan));
        RailTools.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Border"), Margin = new Thickness(5, 7, 5, 7) });
        AddRail("fit", "Показать весь чертёж (Home)", FitDrawing);
        AddRail("zoomIn", "Увеличить масштаб", () => Surface.Zoom(1.25));
        AddRail("zoomOut", "Уменьшить масштаб", () => Surface.Zoom(0.8));
    }

    private void AddRibbon(Tool action, string icon, string title, string shortcut, StackPanel? panel = null) =>
        toolButtons[action] = RibbonButton(panel ?? DrawTools, icon, title, $"{title} ({shortcut})", () => Activate(action));

    private Button RibbonButton(Panel panel, string icon, string title, string tooltip, Action action)
    {
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(BuildIcon(icon, 21));
        content.Children.Add(new TextBlock { Text = title, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        var button = new Button { Width = 75, Height = 48, Content = content, ToolTip = tooltip, Padding = new Thickness(2), Margin = new Thickness(1, 0, 1, 0) };
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        panel.Children.Add(button);
        return button;
    }

    private Button AddRail(string icon, string label, Action action)
    {
        var button = new Button { Style = (Style)FindResource("RailButton") };
        SetIcon(button, icon, label, 19);
        button.Click += (_, _) => action();
        RailTools.Children.Add(button);
        return button;
    }

    private void ReplaceDrawing(Drawing value, string? path = null)
    {
        filePath = path;
        drawing = value;
        savedDrawing = value;
        undo.Clear();
        redo.Clear();
        selected.Clear();
        messages.Clear();
        enteredCommands.Clear();
        commandHistoryPosition = 0;
        Surface.SetDrawing(drawing, true);
        Activate(Tool.Select);
        RefreshDocument();
    }

    private void Commit(Drawing value)
    {
        if (drawing == value) return;
        undo.Push(drawing);
        redo.Clear();
        var layersChanged = !ReferenceEquals(drawing.Layers, value.Layers) || drawing.ActiveLayer != value.ActiveLayer;
        drawing = value;
        RemoveInvalidSelection();
        Surface.SetDrawing(drawing, false);
        RefreshDocument(layersChanged);
    }

    private bool IsDirty => savedDrawing is not null && (drawing.Title != savedDrawing.Title || drawing.Units != savedDrawing.Units
        || !ReferenceEquals(drawing.Layers, savedDrawing.Layers) || !ReferenceEquals(drawing.Shapes, savedDrawing.Shapes));

    private void RefreshDocument(bool refreshLayers = true)
    {
        DrawingTitle.Text = filePath is null ? drawing.Title : System.IO.Path.GetFileNameWithoutExtension(filePath);
        DirtyMark.Visibility = IsDirty ? Visibility.Visible : Visibility.Collapsed;
        Title = $"{DrawingTitle.Text}{(IsDirty ? " *" : "")} — RnxCAD";
        QuickUndo.IsEnabled = undo.Count > 0;
        QuickRedo.IsEnabled = redo.Count > 0;
        UnitText.Text = $"Ед.: {CadFormat.Units(drawing.Units)}";
        if (refreshLayers) RefreshLayers();
        RefreshProperties();
    }

    private void RemoveInvalidSelection()
    {
        if (selected.Count == 0) return;
        var valid = EditableShapes().Select(shape => shape.Id).ToHashSet();
        selected.IntersectWith(valid);
    }

    private void Undo() => StepHistory(undo, redo, "Действие отменено.");
    private void Redo() => StepHistory(redo, undo, "Действие повторено.");

    private void StepHistory(Stack<Drawing> source, Stack<Drawing> destination, string message)
    {
        if (!source.TryPop(out var previous)) return;
        destination.Push(drawing);
        drawing = previous;
        RemoveInvalidSelection();
        Surface.SetDrawing(drawing, false);
        Activate(Tool.Select);
        RefreshDocument();
        Log(message);
    }

    private void RefreshLayers()
    {
        LayerRows.Children.Clear();
        foreach (var layer in drawing.Layers)
        {
            var active = layer.Name == drawing.ActiveLayer;
            var row = new Grid { Height = 32, Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
            var name = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = active ? (Brush)FindResource("Active") : Brushes.Transparent,
                Padding = new Thickness(7, 2, 4, 2), ToolTip = $"Сделать активным: {layer.Name}"
            };
            AutomationProperties.SetName(name, $"Слой {layer.Name}");
            var label = new DockPanel { LastChildFill = true };
            var swatch = new Border
            {
                Width = 12, Height = 12, Background = CadSurface.BrushFor(layer.Color), BorderBrush = CadSurface.BrushFor("#5471796A"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            label.Children.Add(swatch);
            label.Children.Add(new TextBlock
            {
                Text = layer.Name, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12
            });
            name.Content = label;
            name.Click += (_, _) => SetActiveLayer(layer.Name);
            row.Children.Add(name);
            AddLayerToggle(row, 1, layer.Visible ? "eye" : "eyeOff", layer.Visible ? "Скрыть" : "Показать", layer.Name,
                () => ReplaceLayer(layer, layer with { Visible = !layer.Visible }));
            AddLayerToggle(row, 2, layer.Locked ? "lock" : "unlock", layer.Locked ? "Разблокировать" : "Заблокировать", layer.Name,
                () => ReplaceLayer(layer, layer with { Locked = !layer.Locked }));
            LayerRows.Children.Add(row);
        }
        LayerCount.Text = drawing.Layers.Length.ToString();
        ActiveLayerText.Text = $"Активный · {drawing.ActiveLayer}";
    }

    private void AddLayerToggle(Grid row, int column, string icon, string label, string layer, Action action)
    {
        var button = new Button { Style = (Style)FindResource("SmallButton"), Width = 26 };
        SetIcon(button, icon, $"{label} слой «{layer}»", 15);
        Grid.SetColumn(button, column);
        button.Click += (_, _) => action();
        row.Children.Add(button);
    }

    private void SetActiveLayer(string name)
    {
        if (drawing.ActiveLayer == name) return;
        drawing = drawing with { ActiveLayer = name };
        Surface.SetDrawing(drawing, false);
        RefreshLayers();
        RefreshProperties();
    }

    private void ReplaceLayer(Layer oldLayer, Layer newLayer)
    {
        var renamed = oldLayer.Name != newLayer.Name;
        Commit(drawing with
        {
            Layers = drawing.Layers.Select(layer => layer == oldLayer ? newLayer : layer).ToArray(),
            Shapes = renamed ? drawing.Shapes.Select(shape => shape.Layer == oldLayer.Name ? shape with { Layer = newLayer.Name } : shape).ToArray() : drawing.Shapes,
            ActiveLayer = drawing.ActiveLayer == oldLayer.Name ? newLayer.Name : drawing.ActiveLayer
        });
    }

    private void RefreshProperties()
    {
        PropertyRows.Children.Clear();
        var objects = drawing.Shapes.Where(shape => selected.Contains(shape.Id)).ToArray();
        SelectionIcon.Content = BuildIcon(objects.Length == 0 ? "select" : "properties", 15);
        if (objects.Length == 0)
        {
            ObjectType.Text = "Нет выделения";
            Property("Объектов в чертеже", drawing.Shapes.Length.ToString());
            Property("Единицы модели", CadFormat.Units(drawing.Units));
            var hint = new TextBlock
            {
                Text = "Выберите объект в поле чертежа, чтобы посмотреть его параметры.",
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted"), FontSize = 11, Margin = new Thickness(0, 10, 0, 0)
            };
            PropertyRows.Children.Add(hint);
            return;
        }
        var shape = objects[0];
        ObjectType.Text = objects.Length == 1 ? ShapeName(shape) : $"Выделено объектов: {objects.Length}";
        var layerNames = objects.Select(item => item.Layer).Distinct().ToArray();
        var layerBox = new ComboBox
        {
            FontSize = 11, MinHeight = 26, ItemsSource = drawing.Layers.Where(layer => layer.Visible && !layer.Locked).Select(layer => layer.Name).ToArray(),
            SelectedItem = layerNames.Length == 1 ? layerNames[0] : null,
            Margin = new Thickness(0, 0, 0, 6)
        };
        AutomationProperties.SetName(layerBox, "Слой выбранных объектов");
        layerBox.SelectionChanged += (_, _) =>
        {
            if (layerBox.SelectedItem is string name && (layerNames.Length != 1 || layerNames[0] != name))
                Commit(drawing with { Shapes = drawing.Shapes.Select(item => selected.Contains(item.Id) ? item with { Layer = name } : item).ToArray() });
        };
        PropertyRows.Children.Add(new TextBlock { Text = "Слой", Foreground = (Brush)FindResource("Muted"), FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });
        PropertyRows.Children.Add(layerBox);
        if (objects.Length != 1) return;
        string N(double number) => CadFormat.Number(number);
        void P(string label, Vec point) => Property(label, $"{N(point.X)}, {N(point.Y)}");
        switch (shape)
        {
            case Segment line:
                P("Начало", line.A); P("Конец", line.B); Property("Длина", N((line.B - line.A).Length));
                break;
            case Circle circle:
                P("Центр", circle.Center); Property("Радиус", N(circle.Radius)); Property("Диаметр", N(circle.Radius * 2));
                break;
            case Arc arc:
                P("Центр", arc.Center); Property("Радиус", N(arc.Radius)); Property("Угол", N(Math.Abs(arc.Sweep) * 180 / Math.PI) + "°");
                break;
            case Polyline polyline:
                Property("Вершин", polyline.Vertices.Length.ToString()); Property("Замкнута", polyline.Closed ? "Да" : "Нет");
                break;
            case Note note:
                P("Координаты", note.Position); Property("Высота", N(note.Height)); Property("Текст", note.Value);
                break;
            case Dimension dim:
                Property("Длина", N((dim.B - dim.A).Length)); Property("Смещение", N(dim.Offset));
                break;
        }
    }

    private void Property(string label, string value)
    {
        var panel = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.Children.Add(new TextBlock { Text = label, Foreground = (Brush)FindResource("Muted"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        var val = new TextBlock
        {
            Text = value, FontSize = 11, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = (FontFamily)FindResource("MonoFont"), ToolTip = value, HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(val, 1);
        panel.Children.Add(val);
        PropertyRows.Children.Add(panel);
    }

    private static string ShapeName(Shape shape) => shape switch
    {
        Segment => "Отрезок", Polyline => "Полилиния", Circle => "Круг", Arc => "Дуга", Note => "Текст", Dimension => "Размер", _ => "Объект"
    };

    private void ShowMenu(Button button, params (string Label, string Key, Action? Action)[] items)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, FontFamily = FontFamily, FontSize = 13 };
        foreach (var item in items)
        {
            if (item.Action is null)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var entry = new MenuItem { Header = item.Label, InputGestureText = item.Key };
            entry.Click += (_, _) => item.Action();
            menu.Items.Add(entry);
        }
        menu.IsOpen = true;
    }

    private void FileMenu_Click(object sender, RoutedEventArgs e) => ShowMenu(FileMenu,
        ("Новый чертёж", "Ctrl+N", NewDrawing), ("Открыть…", "Ctrl+O", OpenDrawing), ("Сохранить", "Ctrl+S", () => SaveDrawing()),
        ("Сохранить как…", "Ctrl+Shift+S", () => SaveDrawing(true)), ("Экспорт…", "", ExportDrawing), ("", "", null), ("Демонстрационный план", "", OpenDemo));
    private void EditMenu_Click(object sender, RoutedEventArgs e) => ShowMenu(EditMenu,
        ("Отменить", "Ctrl+Z", Undo), ("Повторить", "Ctrl+Y", Redo), ("", "", null),
        ("Выделить все", "Ctrl+A", SelectAll), ("Удалить выделенное", "Delete", DeleteSelection));
    private void ViewMenu_Click(object sender, RoutedEventArgs e) => ShowMenu(ViewMenu,
        ("Показать весь чертёж", "Home", FitDrawing), ("Панорама", "", () => Activate(Tool.Pan)),
        ("Увеличить", "", () => Surface.Zoom(1.25)), ("Уменьшить", "", () => Surface.Zoom(0.8)), ("", "", null),
        ("Сетка", "F7", () => Toggle(GridButton)), ("Орто", "F8", () => Toggle(OrthoButton)), ("Привязка", "F3", () => Toggle(SnapButton)));

    private void Open_Click(object sender, RoutedEventArgs e) => OpenDrawing();
    private void Save_Click(object sender, RoutedEventArgs e) => SaveDrawing();
    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();
    private void Export_Click(object sender, RoutedEventArgs e) => ExportDrawing();
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void CheckClosing(object? sender, CancelEventArgs e) => e.Cancel = !ConfirmDrawingChange();
    private void Help_Click(object sender, RoutedEventArgs e) => ShowHelp();
    private void History_Click(object sender, RoutedEventArgs e) => ShowHistory();
    private void CommandFocus_Click(object sender, RoutedEventArgs e) => FocusCommand();
    private void Options_Click(object sender, RoutedEventArgs e) => UpdateOptions();
    private void AddLayer_Click(object sender, RoutedEventArgs e) => EditLayer(null);
    private void EditLayer_Click(object sender, RoutedEventArgs e) => EditLayer(drawing.CurrentLayer);

    private static void SetIcon(Button button, string icon, string label, double size)
    {
        button.Content = BuildIcon(icon, size);
        button.ToolTip = label;
        AutomationProperties.SetName(button, label);
    }

    private static StackPanel IconLabel(string icon, string label, double size)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(BuildIcon(icon, size));
        stack.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) });
        return stack;
    }

    private static readonly Brush IconInk = CadSurface.BrushFor("#354644");
    private static Viewbox BuildIcon(string icon, double size)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new ShapePath
        {
            Data = Geometry.Parse(IconPaths[icon]), Stroke = IconInk, StrokeThickness = 1.55,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
        });
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    private static readonly Dictionary<string, string> IconPaths = new()
    {
        ["file"] = "M5,3 H14 L19,8 V21 H5 Z M14,3 V8 H19 M8,12 H16 M8,16 H15",
        ["open"] = "M3,19 V5 H9 L11,8 H21 L18,19 Z M3,19 L7,10 H22",
        ["save"] = "M4,4 H17 L20,7 V20 H4 Z M8,4 V10 H16 V4 M8,20 V14 H16 V20",
        ["undo"] = "M9,5 L4,10 L9,15 M4,10 H14 A5,5 0 0 1 14,20 H11",
        ["redo"] = "M15,5 L20,10 L15,15 M20,10 H10 A5,5 0 0 0 10,20 H13",
        ["export"] = "M12,3 V15 M8,11 L12,15 L16,11 M4,15 V20 H20 V15",
        ["plus"] = "M5,12 H19 M12,5 V19", ["minus"] = "M4,14 H20",
        ["maximize"] = "M5,5 H19 V19 H5 Z", ["restore"] = "M8,6 V3 H21 V16 H18 M4,8 H17 V21 H4 Z",
        ["close"] = "M5,5 L19,19 M19,5 L5,19",
        ["line"] = "M5,19 L19,5 M2.8,17 H7.2 V21.4 H2.8 Z M16.8,2.8 H21.2 V7.2 H16.8 Z",
        ["polyline"] = "M3,18 L9,10 L14,14 L21,5 M2,18 H4 M8,10 H10 M13,14 H15 M20,5 H22",
        ["rectangle"] = "M3,5 H21 V19 H3 Z M3,3 V7 M21,17 V21",
        ["circle"] = "M12,3 A9,9 0 1 1 11.99,3 Z M9,12 H15 M12,9 V15",
        ["arc"] = "M4,17 A9,9 0 0 1 20,8 M2,17 H6 M18,8 H22 M11,11 H13",
        ["dimension"] = "M3,6 V20 M21,6 V20 M3,13 H21 M1,15 L5,11 M19,15 L23,11 M8,6 H16",
        ["text"] = "M5,7 V4 H19 V7 M12,4 V20 M8,20 H16",
        ["move"] = "M12,2 V22 M2,12 H22 M9,5 L12,2 L15,5 M9,19 L12,22 L15,19 M5,9 L2,12 L5,15 M19,9 L22,12 L19,15",
        ["copy"] = "M8,8 H20 V20 H8 Z M16,5 V4 H4 V16 H5",
        ["delete"] = "M4,6 H20 M9,6 V3 H15 V6 M6,6 L7,21 H17 L18,6 M10,10 V17 M14,10 V17",
        ["select"] = "M5,3 L19,12 L12,13.5 L9.5,20 Z",
        ["hand"] = "M8,12 V5 A1.4,1.4 0 0 1 11,5 V10 V3.5 A1.4,1.4 0 0 1 14,3.5 V10 V5 A1.4,1.4 0 0 1 17,5 V11 V7 A1.4,1.4 0 0 1 20,7 V14 Q20,21 14,21 H11 Q9,21 7,18 L4,13 A1.5,1.5 0 0 1 6,11 L8,13 Z",
        ["fit"] = "M8,4 H4 V8 M16,4 H20 V8 M20,16 V20 H16 M8,20 H4 V16 M9,9 H15 V15 H9 Z",
        ["zoomIn"] = "M10,3 A7,7 0 1 1 9.99,3 Z M15,15 L21,21 M7,10 H13 M10,7 V13",
        ["zoomOut"] = "M10,3 A7,7 0 1 1 9.99,3 Z M15,15 L21,21 M7,10 H13",
        ["layers"] = "M2,8 L12,3 L22,8 L12,13 Z M2,12 L12,17 L22,12 M2,16 L12,21 L22,16",
        ["properties"] = "M3,6 H21 M3,12 H21 M3,18 H21 M8,4 V8 M15,10 V14 M10,16 V20",
        ["model"] = "M3,4 H21 V20 H3 Z M6,15 L10,11 L13,13 L18,8",
        ["help"] = "M12,2 A10,10 0 1 1 11.99,2 Z M9,8 A3,3 0 1 1 13,11 Q12,12 12,14 M12,17 V18",
        ["terminal"] = "M2,5 H22 V19 H2 Z M6,9 L9,12 L6,15 M12,15 H18",
        ["history"] = "M3,11 A9,9 0 1 1 6,19 M3,5 V11 H9 M12,6 V12 L16,14",
        ["edit"] = "M4,16 L16,4 L20,8 L8,20 H4 Z M13,7 L17,11",
        ["eye"] = "M2,12 Q12,0 22,12 Q12,24 2,12 Z M12,9 A3,3 0 1 1 11.99,9 Z",
        ["eyeOff"] = "M2,12 Q12,0 22,12 Q12,24 2,12 Z M3,3 L21,21",
        ["lock"] = "M6,10 H18 V21 H6 Z M8,10 V7 A4,4 0 0 1 16,7 V10 M12,14 V17",
        ["unlock"] = "M6,10 H18 V21 H6 Z M8,10 V7 A4,4 0 0 1 16,7 M12,14 V17"
    };
}
