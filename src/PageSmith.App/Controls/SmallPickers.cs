using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PageSmith.Core.Pdf;

namespace PageSmith.App.Controls;

/// <summary>Colour swatches plus a hex box. <see cref="Color"/> is "#RRGGBB".</summary>
public sealed class ColorPicker : StackPanel
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(ColorPicker),
        new FrameworkPropertyMetadata("#000000", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorPicker)d).Sync()));

    private static readonly string[] Swatches = ["#000000", "#5F6368", "#D32F2F", "#F57C00", "#FBC02D", "#388E3C", "#1976D2", "#7B1FA2", "#FFFFFF"];
    private readonly TextBox _hex = new() { Width = 96, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly List<ToggleButton> _buttons = [];
    private bool _syncing;

    public ColorPicker()
    {
        var row = new WrapPanel();
        foreach (var hex in Swatches)
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));
            var button = new ToggleButton
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 6),
                Tag = hex,
                ToolTip = hex,
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = SwatchTemplate(brush),
            };
            button.Click += (_, _) => Color = hex;
            _buttons.Add(button);
            row.Children.Add(button);
        }
        Children.Add(row);
        _hex.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            var text = _hex.Text.Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            if (text.Length == 7 && int.TryParse(text[1..], System.Globalization.NumberStyles.HexNumber, null, out _)) Color = text.ToUpperInvariant();
        };
        Children.Add(_hex);
        Sync();
    }

    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    private void Sync()
    {
        _syncing = true;
        if (!string.Equals(_hex.Text, Color, StringComparison.OrdinalIgnoreCase)) _hex.Text = Color;
        foreach (var b in _buttons) b.IsChecked = string.Equals((string)b.Tag, Color, StringComparison.OrdinalIgnoreCase);
        _syncing = false;
    }

    private static ControlTemplate SwatchTemplate(Brush fill)
    {
        var template = new ControlTemplate(typeof(ToggleButton));
        var outer = new FrameworkElementFactory(typeof(Border));
        outer.Name = "Ring";
        outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        outer.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        outer.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        outer.SetValue(Border.PaddingProperty, new Thickness(2));
        var inner = new FrameworkElementFactory(typeof(Border));
        inner.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        inner.SetValue(Border.BackgroundProperty, fill);
        inner.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        inner.SetValue(Border.BorderBrushProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 0, 0, 0)));
        outer.AppendChild(inner);
        template.VisualTree = outer;
        var trigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        trigger.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentFillColorDefaultBrush"), "Ring"));
        template.Triggers.Add(trigger);
        return template;
    }
}

/// <summary>A 3×3 grid for choosing where something goes on the page.</summary>
public sealed class PositionPicker : UniformGrid
{
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(PagePosition), typeof(PositionPicker),
        new FrameworkPropertyMetadata(PagePosition.BottomCenter, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((PositionPicker)d).Sync()));

    public static readonly DependencyProperty IsDisabledLookProperty = DependencyProperty.Register(
        nameof(IsDisabledLook), typeof(bool), typeof(PositionPicker), new PropertyMetadata(false, (d, e) => ((PositionPicker)d).Opacity = (bool)e.NewValue ? 0.4 : 1));

    private readonly ToggleButton[] _cells = new ToggleButton[9];

    public PositionPicker()
    {
        Rows = 3;
        Columns = 3;
        Width = 132;
        Height = 160;
        HorizontalAlignment = HorizontalAlignment.Left;
        for (var i = 0; i < 9; i++)
        {
            var position = (PagePosition)i;
            var cell = new ToggleButton
            {
                Margin = new Thickness(2),
                ToolTip = Spaced(position.ToString()),
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = CellTemplate(),
            };
            cell.Click += (_, _) => Position = position;
            _cells[i] = cell;
            Children.Add(cell);
        }
        Sync();
    }

    public PagePosition Position
    {
        get => (PagePosition)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public bool IsDisabledLook
    {
        get => (bool)GetValue(IsDisabledLookProperty);
        set => SetValue(IsDisabledLookProperty, value);
    }

    private void Sync()
    {
        for (var i = 0; i < 9; i++) _cells[i].IsChecked = (int)Position == i;
    }

    private static string Spaced(string name) => System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();

    private static ControlTemplate CellTemplate()
    {
        var template = new ControlTemplate(typeof(ToggleButton));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Cell";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        border.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        var dot = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        dot.Name = "Dot";
        dot.SetValue(WidthProperty, 10.0);
        dot.SetValue(HeightProperty, 10.0);
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ControlStrongStrokeColorDefaultBrush");
        border.AppendChild(dot);
        template.VisualTree = border;
        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(System.Windows.Shapes.Shape.FillProperty, new DynamicResourceExtension("AccentFillColorDefaultBrush"), "Dot"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentFillColorDefaultBrush"), "Cell"));
        template.Triggers.Add(checkedTrigger);
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("ControlFillColorSecondaryBrush"), "Cell"));
        template.Triggers.Add(hover);
        return template;
    }
}
