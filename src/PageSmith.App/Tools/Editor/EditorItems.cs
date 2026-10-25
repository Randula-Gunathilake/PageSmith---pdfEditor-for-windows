using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PageSmith.Core.Imaging;
using PageSmith.Core.Pdf;
using PageSmith.Core.Tools.Edit;
using TextAlign = PageSmith.Core.Pdf.TextAlign;

namespace PageSmith.App.Tools.Editor;

/// <summary>
/// Something placed on a page in the page editor. <see cref="Bounds"/> is in display-space points
/// (top-left origin, page rotation applied), the same space the PDF engines use.
/// </summary>
public abstract partial class EditorItem : ObservableObject
{
    public int PageIndex { get; set; }

    /// <summary>Holds the style for new items only; never placed on a page.</summary>
    public bool IsStyleOnly { get; init; }

    [ObservableProperty] public partial Rect Bounds { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial double Opacity { get; set; } = 1;

    public virtual bool CanMove => true;
    public virtual bool CanResize => true;
    public virtual bool CanDelete => true;
    public virtual bool KeepAspectRatio => false;
    public virtual double MinWidth => 6;
    public virtual double MinHeight => 6;

    /// <summary>Short name for lists and tooltips ("Text", "Signature"…).</summary>
    public abstract string Kind { get; }

    /// <summary>Moves or resizes the item. Items with inner geometry (ink) transform it too.</summary>
    public virtual void SetBounds(Rect bounds) => Bounds = bounds;
}

/// <summary>Text box: added text, edited existing text, signer name, date.</summary>
public sealed partial class TextItem : EditorItem
{
    private string _kind = "Text";

    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial string FontName { get; set; } = "Arial";
    [ObservableProperty] public partial double FontSize { get; set; } = 14;
    [ObservableProperty] public partial bool Bold { get; set; }
    [ObservableProperty] public partial bool Italic { get; set; }
    [ObservableProperty] public partial bool Underline { get; set; }
    [ObservableProperty] public partial string Color { get; set; } = "#000000";
    [ObservableProperty] public partial TextAlign Align { get; set; } = TextAlign.Left;

    /// <summary>Box fill, e.g. white to cover the text being replaced; null for none.</summary>
    [ObservableProperty] public partial string? Background { get; set; }

    [ObservableProperty] public partial bool IsEditing { get; set; }

    public override string Kind => _kind;
    public TextItem WithKind(string kind)
    {
        _kind = kind;
        return this;
    }

    /// <summary>Fonts PDFsharp can embed (installed Windows fonts).</summary>
    public static IReadOnlyList<string> AvailableFonts { get; } = Core.Common.WindowsFontResolver.InstalledFamilies();

    public bool HasBackground
    {
        get => Background != null;
        set => Background = value ? Background ?? "#FFFFFF" : null;
    }

    partial void OnBackgroundChanged(string? value) => OnPropertyChanged(nameof(HasBackground));

    public FontWeight Weight => Bold ? FontWeights.Bold : FontWeights.Normal;
    public FontStyle Style => Italic ? FontStyles.Italic : FontStyles.Normal;
    public TextDecorationCollection? Decorations => Underline ? TextDecorations.Underline : null;
    public TextAlignment Alignment => Align switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, TextAlign.Justify => TextAlignment.Justify, _ => TextAlignment.Left };

    partial void OnBoldChanged(bool value)
    {
        OnPropertyChanged(nameof(Weight));
        GrowToFit();
    }

    partial void OnItalicChanged(bool value)
    {
        OnPropertyChanged(nameof(Style));
        GrowToFit();
    }

    partial void OnUnderlineChanged(bool value) => OnPropertyChanged(nameof(Decorations));
    partial void OnAlignChanged(TextAlign value) => OnPropertyChanged(nameof(Alignment));
    /// <summary>Line height matching the PDF renderer (1.15 × font size).</summary>
    public double LineHeight => Math.Max(1, FontSize * 1.15);

    partial void OnTextChanged(string value) => GrowToFit();

    partial void OnFontSizeChanged(double value)
    {
        OnPropertyChanged(nameof(LineHeight));
        GrowToFit();
    }

    partial void OnFontNameChanged(string value) => GrowToFit();

    /// <summary>Widens the box as you type (up to the page edge) instead of wrapping, until it is resized by hand.</summary>
    public bool AutoWidth { get; set; }

    /// <summary>Width of the page the box is on, which limits <see cref="AutoWidth"/>.</summary>
    public double PageWidth { get; init; }

    public override void SetBounds(Rect bounds)
    {
        if (Math.Abs(bounds.Width - Bounds.Width) > 0.5) AutoWidth = false;
        base.SetBounds(bounds);
    }

    /// <summary>Makes the box big enough for its text: wider first (with <see cref="AutoWidth"/>), then taller.</summary>
    public void GrowToFit()
    {
        var b = Bounds;
        if (AutoWidth && PageWidth > 0)
        {
            var wanted = MeasureWidth();
            if (wanted > b.Width) b.Width = Math.Min(wanted, Math.Max(b.Width, PageWidth - b.X - 6));
        }
        var needed = MeasureHeight(b.Width);
        if (needed > b.Height + 0.5) b.Height = needed;
        if (b != Bounds) Bounds = b;
    }

    public double MeasureHeight(double width)
    {
        var text = string.IsNullOrEmpty(Text) ? "Ag" : Text;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(FontName), Style, Weight, FontStretches.Normal), Math.Max(1, FontSize), Brushes.Black, 1.0)
        {
            MaxTextWidth = Math.Max(1, width - 2),
            LineHeight = FontSize * 1.15,
        };
        return Math.Ceiling(ft.Height + FontSize * 0.3);
    }

    /// <summary>Width of the text on one line, for sizing new boxes.</summary>
    public double MeasureWidth()
    {
        var widest = (string.IsNullOrEmpty(Text) ? "Type here" : Text).Split('\n').Max(line =>
            new FormattedText(line.Length == 0 ? " " : line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(FontName), Style, Weight, FontStretches.Normal), Math.Max(1, FontSize), Brushes.Black, 1.0).WidthIncludingTrailingWhitespace);
        return Math.Ceiling(widest + FontSize * 0.6);
    }

    public TextElement ToElement() => new()
    {
        PageIndex = PageIndex,
        Bounds = Bounds,
        Opacity = Opacity,
        Text = Text,
        Align = Align,
        Background = Background,
        Style = new TextStyle { Font = FontName, Size = FontSize, Bold = Bold, Italic = Italic, Underline = Underline, Color = Color },
    };
}

/// <summary>Picture: image, signature, initials or company stamp.</summary>
public sealed partial class ImageItem : EditorItem
{
    private readonly string _kind;

    public ImageItem(byte[] data, string kind = "Image")
    {
        Data = data;
        Image = ImageIO.Decode(data);
        _kind = kind;
    }

    public byte[] Data { get; }
    public BitmapSource Image { get; }
    public override string Kind => _kind;
    public override bool KeepAspectRatio => true;

    public ImageElement ToElement() => new() { PageIndex = PageIndex, Bounds = Bounds, Opacity = Opacity, Data = Data };
}

/// <summary>Rectangle, ellipse, line, arrow, or a white-out box.</summary>
public sealed partial class ShapeItem : EditorItem
{
    [ObservableProperty] public partial ShapeKind Shape { get; set; } = ShapeKind.Rectangle;
    [ObservableProperty] public partial string? Stroke { get; set; } = "#D32F2F";
    [ObservableProperty] public partial string? Fill { get; set; }
    [ObservableProperty] public partial double StrokeWidth { get; set; } = 2;
    [ObservableProperty] public partial bool RisingDiagonal { get; set; }

    public bool IsWhiteout { get; init; }
    public bool IsLine => Shape is ShapeKind.Line or ShapeKind.Arrow;
    public bool HasFill => Fill != null;
    public override double MinWidth => IsLine ? 0 : 6;
    public override double MinHeight => IsLine ? 0 : 6;

    public override string Kind => IsWhiteout ? "White-out" : Shape.ToString();

    /// <summary>Fill colour for the editor's colour picker; switching it on and off uses <see cref="Fill"/>.</summary>
    public string FillColor
    {
        get => Fill ?? "#FFFFFF";
        set
        {
            Fill = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFill));
        }
    }

    public bool FillEnabled
    {
        get => Fill != null;
        set
        {
            Fill = value ? FillColor : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFill));
        }
    }

    public ShapeElement ToElement() => new()
    {
        PageIndex = PageIndex,
        Bounds = Bounds,
        Opacity = Opacity,
        Shape = Shape,
        Stroke = Stroke,
        Fill = Fill,
        StrokeWidth = Stroke == null ? 0 : StrokeWidth,
        RisingDiagonal = RisingDiagonal,
    };
}

/// <summary>Freehand drawing. Points are absolute display coordinates and follow moves and resizes.</summary>
public sealed partial class InkItem : EditorItem
{
    [ObservableProperty] public partial string Color { get; set; } = "#1F3A93";
    [ObservableProperty] public partial double Width { get; set; } = 2;

    public List<List<Point>> Strokes { get; } = [];
    public override string Kind => "Drawing";
    public override double MinWidth => 2;
    public override double MinHeight => 2;

    /// <summary>Sets <see cref="EditorItem.Bounds"/> to the strokes' extent.</summary>
    public void FitBounds()
    {
        var pts = Strokes.SelectMany(s => s).ToList();
        if (pts.Count == 0) return;
        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        var pad = Width / 2 + 1;
        Bounds = new Rect(minX - pad, minY - pad, Math.Max(2, maxX - minX + 2 * pad), Math.Max(2, maxY - minY + 2 * pad));
    }

    public override void SetBounds(Rect bounds)
    {
        var old = Bounds;
        if (old.Width > 0 && old.Height > 0)
        {
            var sx = bounds.Width / old.Width;
            var sy = bounds.Height / old.Height;
            foreach (var stroke in Strokes)
                for (var i = 0; i < stroke.Count; i++)
                    stroke[i] = new Point(bounds.X + (stroke[i].X - old.X) * sx, bounds.Y + (stroke[i].Y - old.Y) * sy);
        }
        Bounds = bounds;
    }

    public InkElement ToElement() => new()
    {
        PageIndex = PageIndex,
        Bounds = Bounds,
        Opacity = Opacity,
        Color = Color,
        Width = Width,
        Strokes = Strokes.Select(s => (IReadOnlyList<Point>)s.ToList()).ToList(),
    };
}

/// <summary>An area to black out in Redact PDF.</summary>
public sealed partial class RedactItem : EditorItem
{
    public string? MatchedText { get; init; }
    public string? Reason { get; init; }
    public override string Kind => Reason ?? "Area";
    public string Description => MatchedText is { Length: > 0 } t ? $"{Kind}: {t}" : $"{Kind} on page {PageIndex + 1}";
    public override double MinWidth => 2;
    public override double MinHeight => 2;
}

/// <summary>The area kept by Crop PDF.</summary>
public sealed partial class CropItem : EditorItem
{
    public required Size PageSize { get; init; }
    public override string Kind => "Crop area";
    public override bool CanDelete => false;
    public override double MinWidth => 12;
    public override double MinHeight => 12;

    public override void SetBounds(Rect bounds)
    {
        bounds.Intersect(new Rect(PageSize));
        if (!bounds.IsEmpty) Bounds = bounds;
    }

    /// <summary>The area as fractions of the page, as <see cref="CropTool"/> expects.</summary>
    public Rect Fraction => new(Bounds.X / PageSize.Width, Bounds.Y / PageSize.Height, Bounds.Width / PageSize.Width, Bounds.Height / PageSize.Height);
}

/// <summary>A form field: an existing one (filled in place) or a new one being created.</summary>
public sealed partial class FieldItem : EditorItem
{
    public required FormFieldKind FieldKind { get; init; }
    public bool IsExisting { get; init; }
    public bool ReadOnly { get; init; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Value { get; set; } = "";
    [ObservableProperty] public partial string OptionsText { get; set; } = "";
    [ObservableProperty] public partial bool Multiline { get; set; }

    /// <summary>Value as it was in the file, to report only real changes.</summary>
    public string OriginalValue { get; init; } = "";

    public override bool CanMove => !IsExisting;
    public override bool CanResize => !IsExisting;
    public override bool CanDelete => !IsExisting;
    public override string Kind => FieldKind switch
    {
        FormFieldKind.Text => Multiline ? "Multi-line text" : "Text field",
        FormFieldKind.CheckBox => "Checkbox",
        FormFieldKind.RadioGroup => "Radio buttons",
        FormFieldKind.ComboBox => "Dropdown",
        FormFieldKind.ListBox => "List",
        FormFieldKind.Signature => "Signature field",
        _ => "Button",
    };

    /// <summary>Font size for filling the field in place (existing fields keep their size).</summary>
    public double InputFontSize => Multiline ? Math.Clamp(Bounds.Height / 5, 6, 11) : Math.Clamp(Bounds.Height * 0.6, 5, 13);

    public IReadOnlyList<string> Options => OptionsText.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public bool HasOptions => FieldKind is FormFieldKind.ComboBox or FormFieldKind.ListBox or FormFieldKind.RadioGroup;
    public bool IsText => FieldKind == FormFieldKind.Text;
    public bool IsCheck => FieldKind == FormFieldKind.CheckBox;
    public bool IsChoice => HasOptions;
    public bool IsFillable => IsExisting && !ReadOnly && FieldKind is not (FormFieldKind.Signature or FormFieldKind.Button);

    public bool Checked
    {
        get => Value.Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1" or "x" or "checked";
        set
        {
            Value = value ? "Yes" : "Off";
            OnPropertyChanged();
        }
    }

    partial void OnOptionsTextChanged(string value) => OnPropertyChanged(nameof(Options));
    partial void OnMultilineChanged(bool value) => OnPropertyChanged(nameof(Kind));
    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(Checked));

    public NewFormField ToNewField() => new(FieldKind, Name, PageIndex, Bounds, HasOptions ? Options : null, string.IsNullOrEmpty(Value) ? null : Value, Multiline);
}

/// <summary>Where a visible digital signature goes.</summary>
public sealed partial class SignatureBoxItem : EditorItem
{
    public override string Kind => "Digital signature";
    public override double MinWidth => 60;
    public override double MinHeight => 24;
}
