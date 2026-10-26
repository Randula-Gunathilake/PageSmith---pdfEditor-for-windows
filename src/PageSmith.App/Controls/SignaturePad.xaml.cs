using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Imaging;
using Wpf.Ui.Controls;

namespace PageSmith.App.Controls;

/// <summary>Draw, type or upload a signature (or initials). The result is a trimmed PNG with a transparent background.</summary>
public partial class SignaturePad : UserControl
{
    /// <summary>Handwriting-style fonts that ship with Windows or Office; only installed ones are offered.</summary>
    private static readonly string[] ScriptFonts =
    [
        "Segoe Script", "Ink Free", "Lucida Handwriting", "Brush Script MT", "Freestyle Script", "Mistral", "Bradley Hand ITC",
        "Kunstler Script", "Vladimir Script", "Edwardian Script ITC", "Segoe Print", "Monotype Corsiva", "Pristina", "French Script MT", "Gabriola",
    ];

    private BitmapSource? _upload;

    public SignaturePad(string? suggestedText)
    {
        InitializeComponent();
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fonts = ScriptFonts.Where(installed.Contains).ToList();
        if (fonts.Count == 0) fonts.Add("Segoe UI");
        FontChoice.ItemsSource = fonts;
        FontChoice.SelectedIndex = 0;
        TypedText.Text = suggestedText ?? "";
        DependencyPropertyDescriptor.FromProperty(ColorPicker.ColorProperty, typeof(ColorPicker)).AddValueChanged(InkColor, (_, _) => ApplyColor());
        ApplyColor();
        OnPenChanged(this, new RoutedPropertyChangedEventArgs<double>(0, PenWidth.Value));
    }

    private Color CurrentColor
    {
        get
        {
            try { return (Color)ColorConverter.ConvertFromString(InkColor.Color); }
            catch { return Colors.Black; }
        }
    }

    private void ApplyColor()
    {
        Ink.DefaultDrawingAttributes.Color = CurrentColor;
        foreach (var stroke in Ink.Strokes) stroke.DrawingAttributes.Color = CurrentColor;
        Typed.Foreground = new SolidColorBrush(CurrentColor);
    }

    private void OnPenChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Ink == null) return;
        Ink.DefaultDrawingAttributes.Width = Ink.DefaultDrawingAttributes.Height = e.NewValue;
        Ink.DefaultDrawingAttributes.FitToCurve = true;
        Ink.DefaultDrawingAttributes.StylusTip = StylusTip.Ellipse;
    }

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (DrawOptions == null) return;
        var draw = DrawTab.IsChecked == true;
        var type = TypeTab.IsChecked == true;
        var upload = UploadTab.IsChecked == true;
        DrawOptions.Visibility = Ink.Visibility = Baseline.Visibility = draw ? Visibility.Visible : Visibility.Collapsed;
        SignHere.Visibility = draw && Ink.Strokes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TypeOptions.Visibility = Typed.Visibility = type ? Visibility.Visible : Visibility.Collapsed;
        UploadOptions.Visibility = upload ? Visibility.Visible : Visibility.Collapsed;
        Uploaded.Visibility = upload && _upload != null ? Visibility.Visible : Visibility.Collapsed;
        UploadHint.Visibility = upload && _upload == null ? Visibility.Visible : Visibility.Collapsed;
        if (type) OnTypedChanged(this, e);
    }

    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e) => SignHere.Visibility = Visibility.Collapsed;

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Ink.Strokes.Clear();
        SignHere.Visibility = Visibility.Visible;
    }

    private void OnTypedChanged(object sender, RoutedEventArgs e)
    {
        if (Typed == null || FontChoice.SelectedItem is not string font) return;
        Typed.Text = TypedText.Text;
        Typed.FontFamily = new FontFamily(font);
    }

    private void OnChooseImage(object sender, RoutedEventArgs e)
    {
        var path = AppServices.PickFile("Choose your signature image", $"Images|{string.Join(";", ImageIO.SupportedExtensions.Select(x => "*" + x))}");
        if (path == null) return;
        try
        {
            _upload = ImageIO.Load(path);
            ShowUpload();
        }
        catch (Exception ex)
        {
            AppServices.Notify("Could not open the image", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
        }
    }

    private void OnRemoveBackgroundChanged(object sender, RoutedEventArgs e) => ShowUpload();

    private void ShowUpload()
    {
        if (Uploaded == null || _upload == null) return;
        Uploaded.Source = RemoveBackground.IsChecked == true ? WithoutPaper(_upload) : _upload;
        OnTabChanged(this, new RoutedEventArgs());
    }

    /// <summary>The signature as PNG bytes, or null when nothing was drawn, typed or chosen.</summary>
    public byte[]? ExportPng()
    {
        BitmapSource? bitmap = null;
        if (DrawTab.IsChecked == true && Ink.Strokes.Count > 0) bitmap = RenderInk();
        else if (TypeTab.IsChecked == true && !string.IsNullOrWhiteSpace(TypedText.Text)) bitmap = RenderTyped();
        else if (UploadTab.IsChecked == true && Uploaded.Source is BitmapSource up) bitmap = up;
        if (bitmap == null) return null;
        return ImageIO.EncodePng(Trim(bitmap));
    }

    private BitmapSource RenderInk()
    {
        const double scale = 3;
        var bounds = Ink.Strokes.GetBounds();
        bounds.Inflate(4, 4);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            Ink.Strokes.Draw(dc);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private BitmapSource RenderTyped()
    {
        var text = new FormattedText(TypedText.Text.Trim(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface((string)FontChoice.SelectedItem), 150, new SolidColorBrush(CurrentColor), 1.0);
        // Script fonts overhang their advance box; leave room on every side and trim afterwards.
        var pad = 80;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawText(text, new Point(pad, pad));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(text.WidthIncludingTrailingWhitespace + 2 * pad), (int)Math.Ceiling(text.Height + 2 * pad), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>Makes paper-white pixels transparent and fades light grey ones, keeping the ink.</summary>
    internal static BitmapSource WithoutPaper(BitmapSource source)
    {
        var px = ImageIO.ToPixels(source);
        var p = px.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            var light = Math.Min(p[i], Math.Min(p[i + 1], p[i + 2]));
            var alpha = light >= 225 ? 0 : light <= 150 ? 255 : (int)(255 * (225 - light) / 75.0);
            p[i + 3] = (byte)Math.Min(p[i + 3], alpha);
        }
        return ImageIO.FromPixels(px);
    }

    /// <summary>Crops transparent margins, keeping a little space around the ink.</summary>
    internal static BitmapSource Trim(BitmapSource source)
    {
        var px = ImageIO.ToPixels(source);
        int minX = px.Width, minY = px.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < px.Height; y++)
        for (var x = 0; x < px.Width; x++)
        {
            if (px.Pixels[(y * px.Width + x) * 4 + 3] < 12) continue;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }
        if (maxX < 0) return source;
        var pad = 6;
        var rect = new Int32Rect(Math.Max(0, minX - pad), Math.Max(0, minY - pad), 0, 0);
        rect.Width = Math.Min(px.Width, maxX + pad + 1) - rect.X;
        rect.Height = Math.Min(px.Height, maxY + pad + 1) - rect.Y;
        var cropped = new CroppedBitmap(ImageIO.FromPixels(px), rect);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>Shows the pad in a dialog; returns the PNG, or null when cancelled or left empty.</summary>
    public static async Task<byte[]?> AskAsync(string title, string? suggestedText)
    {
        var pad = new SignaturePad(suggestedText);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = pad,
            PrimaryButtonText = "Use this",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            DialogMaxWidth = 720,
            DialogMaxHeight = 680,
        };
        if (await AppServices.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return null;
        var png = pad.ExportPng();
        if (png == null) AppServices.Notify("Nothing to use", "Draw, type or upload a signature first.", ControlAppearance.Caution);
        return png;
    }
}
