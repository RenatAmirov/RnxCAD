using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace RnxCAD;

public partial class MainWindow
{
    private const string NativeFilter = "Чертёж RnxCAD (*.rnxcad)|*.rnxcad";

    private void NewDrawing()
    {
        if (!ConfirmDrawingChange()) return;
        ReplaceDrawing(Drawing.Empty());
    }

    private void OpenDemo()
    {
        if (!ConfirmDrawingChange()) return;
        ReplaceDrawing(DemoDrawing.Create());
    }

    private void OpenDrawing()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Открыть чертёж", Filter = $"Чертежи RnxCAD и DXF (*.rnxcad;*.dxf)|*.rnxcad;*.dxf|{NativeFilter}|Текстовый DXF (*.dxf)|*.dxf"
        };
        if (dialog.ShowDialog(this) == true && ConfirmDrawingChange()) ReadDrawing(dialog.FileName);
    }

    private void ReadDrawing(string path)
    {
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".rnxcad":
                    ReplaceDrawing(DrawingFiles.ReadNative(path), Path.GetFullPath(path));
                    Log("Чертёж открыт.");
                    break;
                case ".dxf":
                    var (imported, skipped) = DrawingFiles.ReadDxf(path);
                    ReplaceDrawing(imported);
                    Log(skipped == 0 ? $"DXF открыт. Объектов: {imported.Shapes.Length}."
                        : $"Импорт DXF: {imported.Shapes.Length} объектов. Пропущено записей: {skipped}.");
                    if (skipped > 0)
                        MessageBox.Show(this, $"Импортировано объектов: {imported.Shapes.Length}. Пропущено неподдерживаемых записей: {skipped}.",
                            "Импорт DXF", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                default:
                    MessageBox.Show(this, "Выберите файл RnxCAD (.rnxcad) или текстовый DXF (.dxf).", "Открыть чертёж", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
            }
        }
        catch (Exception ex) when (HandleFileError(ex)) { ReportFileError("Открытие чертежа", ex); }
    }

    private bool SaveDrawing(bool copy = false)
    {
        var path = filePath;
        if (copy || path is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Сохранить чертёж RnxCAD", Filter = NativeFilter, DefaultExt = ".rnxcad",
                FileName = FileStem(), AddExtension = true
            };
            if (dialog.ShowDialog(this) != true) return false;
            path = EnsureExtension(dialog.FileName, ".rnxcad");
            if (path != dialog.FileName && !ConfirmOverwrite(path)) return false;
        }
        try
        {
            DrawingFiles.WriteNative(path, drawing);
            filePath = path;
            savedDrawing = drawing;
            RefreshDocument(false);
            Log($"Сохранено: {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception ex) when (HandleFileError(ex))
        {
            ReportFileError("Сохранение чертежа", ex);
            return false;
        }
    }

    private void ExportDrawing()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт чертежа", Filter = "Текстовый DXF (*.dxf)|*.dxf|Векторное изображение SVG (*.svg)|*.svg",
            FileName = FileStem(), AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var extension = dialog.FilterIndex == 2 ? ".svg" : ".dxf";
        var path = EnsureExtension(dialog.FileName, extension);
        if (path != dialog.FileName && !ConfirmOverwrite(path)) return;
        try
        {
            if (extension == ".svg") DrawingFiles.WriteSvg(path, drawing);
            else DrawingFiles.WriteDxf(path, drawing);
            Log($"Экспортировано: {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (HandleFileError(ex)) { ReportFileError("Экспорт чертежа", ex); }
    }

    private static string EnsureExtension(string path, string extension) => Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)
        ? path : path + extension;

    private bool ConfirmOverwrite(string path) => !File.Exists(path) || MessageBox.Show(this, $"Заменить файл «{Path.GetFileName(path)}»?",
        "Сохранение файла", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private string FileStem()
    {
        if (filePath is string path) return Path.GetFileNameWithoutExtension(path);
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(drawing.Title.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }

    private bool ConfirmDrawingChange()
    {
        if (!IsDirty) return true;
        var decision = MessageBox.Show(this, $"Сохранить изменения в чертеже «{FileStem()}»?", "RnxCAD",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        return decision == MessageBoxResult.No || decision == MessageBoxResult.Yes && SaveDrawing();
    }

    private static bool HandleFileError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or ArgumentException or JsonException or NotSupportedException or OverflowException;

    private void ReportFileError(string title, Exception error) => MessageBox.Show(this, error.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private void EditLayer(Layer? original)
    {
        var isNew = original is null;
        var dialog = CreateDialog(isNew ? "Создать слой" : "Свойства слоя", 390);
        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        dialog.Content = panel;
        var number = drawing.Layers.Length;
        while (drawing.Layers.Any(layer => layer.Name.Equals($"Слой {number}", StringComparison.OrdinalIgnoreCase))) number++;
        var name = Field(panel, "Имя слоя", original?.Name ?? $"Слой {number}");
        name.IsReadOnly = original?.Name == "0";
        var color = Field(panel, "Цвет в формате #RRGGBB", original?.Color ?? "#A8D7A7");
        var swatches = new WrapPanel { Margin = new Thickness(0, 5, 0, 10) };
        foreach (var hex in new[] { "#D9E3EF", "#F0BF85", "#70B9BA", "#7E98B1", "#A8D7A7", "#D9A6D6", "#F68B85", "#E3E887" })
        {
            var sample = new Button { Background = CadSurface.BrushFor(hex), Width = 28, Height = 24, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0), ToolTip = hex };
            AutomationProperties.SetName(sample, hex);
            sample.Click += (_, _) => color.Text = hex;
            swatches.Children.Add(sample);
        }
        panel.Children.Add(swatches);
        var weight = Field(panel, "Толщина линии на экране, 0.3–5", (original?.Weight ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var error = new TextBlock { Foreground = CadSurface.BrushFor("#9C3630"), Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 13, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(0, 0, 6, 0) };
        var apply = new Button { Content = isNew ? "Создать" : "Сохранить", IsDefault = true, Background = (Brush)FindResource("Accent"), Padding = new Thickness(12, 5, 12, 5) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        panel.Children.Add(buttons);
        apply.Click += (_, _) =>
        {
            var newName = name.Text.Trim();
            var newColor = color.Text.Trim().ToUpperInvariant();
            if (!DrawingFiles.ValidName(newName)) { error.Text = "Введите допустимое имя слоя."; return; }
            if (drawing.Layers.Any(layer => layer != original && layer.Name.Equals(newName, StringComparison.OrdinalIgnoreCase))) { error.Text = "Слой с таким именем уже существует."; return; }
            if (!DrawingFiles.ValidColor(newColor)) { error.Text = "Введите цвет в формате #RRGGBB."; return; }
            if (!CadFormat.TryNumber(weight.Text, out var lineWeight) || lineWeight is < 0.3 or > 5) { error.Text = "Укажите толщину от 0.3 до 5."; return; }
            if (original is null) Commit(drawing with { Layers = [.. drawing.Layers, new Layer(newName, newColor, Weight: lineWeight)], ActiveLayer = newName });
            else
            {
                var updated = original with { Name = newName, Color = newColor, Weight = lineWeight };
                if (updated != original) ReplaceLayer(original, updated);
            }
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        dialog.ShowDialog();
    }

    private TextBox Field(Panel panel, string label, string value)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 4), FontSize = 12 });
        var field = new TextBox { Text = value, Padding = new Thickness(7, 4, 7, 4), BorderBrush = (Brush)FindResource("Border"), Margin = new Thickness(0, 0, 0, 7) };
        AutomationProperties.SetName(field, label);
        panel.Children.Add(field);
        return field;
    }

    private Window CreateDialog(string title, double width) => new()
    {
        Owner = this, Title = title, Width = width, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = (Brush)FindResource("Paper"), FontFamily = FontFamily, FontSize = 13
    };

    private void ShowHelp() => ShowTextDialog("Команды и сочетания клавиш", """
        РИСОВАНИЕ
        L  Отрезок         PL   Полилиния      REC  Прямоугольник
        C  Круг            A    Дуга           DIM  Линейный размер
        T  Текст           CLOSE  Замкнуть полилинию

        РЕДАКТИРОВАНИЕ
        M  Перенести       CO  Копировать      E  Удалить
        Ctrl+Z  Отменить   Ctrl+Y  Повторить   Ctrl+A  Выделить всё
        Shift  Добавить к выделению. Рамка слева направо выбирает
        объекты целиком, справа налево — также пересечённые.

        КООРДИНАТЫ
        1200,800  Абсолютная точка
        @500,0    Смещение от предыдущей точки
        @500<45   Расстояние 500 под углом 45°
        12,5;8,2  Десятичная запятая с разделителем «;»
        Enter     Выполнить ввод / закончить построение
        Esc       Завершить команду или снять выделение

        НАВИГАЦИЯ И ФАЙЛЫ
        Колесо  Масштаб    Средняя кнопка + перетаскивание  Панорама
        Home  Весь чертёж  F3  Привязка  F7  Сетка  F8  Орто
        Ctrl+L  Команды    F2  История   ↑ / ↓  Прошлые команды
        Ctrl+N  Новый      Ctrl+O  Открыть      Ctrl+S  Сохранить
        """, 536);

    private void ShowHistory() => ShowTextDialog("История команд", messages.Count == 0 ? "История пуста." : string.Join(Environment.NewLine, messages.TakeLast(200)), 536);

    private void ShowTextDialog(string title, string contents, double width)
    {
        var dialog = CreateDialog(title, width);
        var layout = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        var close = new Button { Content = "Закрыть", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Background = (Brush)FindResource("Accent") };
        close.Click += (_, _) => dialog.Close();
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(close);
        layout.Children.Add(new TextBox
        {
            Text = contents, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 450, FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 12, Padding = new Thickness(7), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = (Brush)FindResource("Shell"), BorderBrush = (Brush)FindResource("Border")
        });
        dialog.Content = layout;
        dialog.ShowDialog();
    }
}
