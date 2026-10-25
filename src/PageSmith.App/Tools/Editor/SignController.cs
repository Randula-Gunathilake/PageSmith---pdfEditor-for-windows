using System.Collections.ObjectModel;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PageSmith.App.Controls;
using PageSmith.App.Infrastructure;
using PageSmith.App.Tools.Editor;
using PageSmith.Core.Common;
using PageSmith.Core.Imaging;
using PageSmith.Core.Net;
using PageSmith.Core.Pdf;
using PageSmith.Core.Tools.Security;
using Wpf.Ui.Controls;

namespace PageSmith.App.Tools;

public enum CertificateSource
{
    Windows,
    File,
    New,
}

public sealed record CertificateChoice(X509Certificate2 Certificate)
{
    public string Text => Certificates.Describe(Certificate);
}

/// <summary>Sign PDF: place a drawn, typed or uploaded signature and other details, and optionally sign with a certificate.</summary>
public sealed partial class SignController() : EditorController("sign")
{
    private static readonly EditorTool SelectTool = new("select", "Select", SymbolRegular.Cursor24, PointerAction.Select,
        "Click a placed item to select it; drag to move, use the handles to resize.");
    private static readonly EditorTool SignatureTool = new("signature", "Place signature", SymbolRegular.Signature24, PointerAction.Click, "Click where your signature goes.");
    private static readonly EditorTool InitialsTool = new("initials", "Place initials", SymbolRegular.CalligraphyPen24, PointerAction.Click, "Click where your initials go.");
    private static readonly EditorTool NameTool = new("name-text", "Place name", SymbolRegular.PersonEdit24, PointerAction.Click, "Click where your name goes.");
    private static readonly EditorTool DateTool = new("date-text", "Place date", SymbolRegular.CalendarLtr24, PointerAction.Click, "Click where the date goes.");
    private static readonly EditorTool TextTool = new("text", "Place text", SymbolRegular.TextT24, PointerAction.Click, "Click where the text goes, then type.");
    private static readonly EditorTool StampTool = new("stamp", "Place company stamp", SymbolRegular.Building24, PointerAction.Click, "Click where the company stamp goes.");
    private static readonly EditorTool DigitalBoxTool = new("digital", "Digital signature box", SymbolRegular.Certificate24, PointerAction.DrawRect,
        "Drag a box where the visible digital signature should appear. Without a box the digital signature is invisible.");

    public override IReadOnlyList<EditorTool> Tools { get; } = [SelectTool, SignatureTool, InitialsTool, NameTool, DateTool, TextTool, StampTool, DigitalBoxTool];

    public IReadOnlyList<string> DateFormats { get; } = ["dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "d MMMM yyyy", "MMMM d, yyyy"];

    [ObservableProperty] public partial string SignerName { get; set; } = "";
    [ObservableProperty] public partial string DateFormat { get; set; } = "dd/MM/yyyy";
    [ObservableProperty] public partial byte[]? Signature { get; set; }
    [ObservableProperty] public partial byte[]? Initials { get; set; }
    [ObservableProperty] public partial byte[]? Stamp { get; set; }

    [ObservableProperty] public partial bool UseDigital { get; set; }
    [ObservableProperty] public partial CertificateSource Source { get; set; } = CertificateSource.Windows;
    [ObservableProperty] public partial CertificateChoice? SelectedCertificate { get; set; }
    [ObservableProperty] public partial string? PfxPath { get; set; }
    [ObservableProperty] public partial string PfxPassword { get; set; } = "";
    [ObservableProperty] public partial string NewCertName { get; set; } = "";
    [ObservableProperty] public partial string NewCertEmail { get; set; } = "";
    [ObservableProperty] public partial string NewCertOrganization { get; set; } = "";
    [ObservableProperty] public partial bool SaveNewCertToWindows { get; set; } = true;
    [ObservableProperty] public partial string Reason { get; set; } = "";
    [ObservableProperty] public partial string Location { get; set; } = "";
    [ObservableProperty] public partial bool ShowHandwritingInBox { get; set; } = true;
    [ObservableProperty] public partial bool AddTimestamp { get; set; }

    public ObservableCollection<CertificateChoice> StoreCertificates { get; } = [];

    public BitmapSource? SignaturePreview => Signature == null ? null : ImageIO.Decode(Signature);
    public BitmapSource? InitialsPreview => Initials == null ? null : ImageIO.Decode(Initials);
    public BitmapSource? StampPreview => Stamp == null ? null : ImageIO.Decode(Stamp);
    public string PfxName => PfxPath == null ? "No file chosen" : System.IO.Path.GetFileName(PfxPath);
    public bool HasDigitalBox => Items.OfType<SignatureBoxItem>().Any();
    public bool NoStoreCertificates => StoreCertificates.Count == 0;

    /// <summary>The selected item when it has properties to edit (text boxes).</summary>
    public object? PropertiesTarget => SelectedItem as TextItem;

    partial void OnSignatureChanged(byte[]? value) => OnPropertyChanged(nameof(SignaturePreview));
    partial void OnInitialsChanged(byte[]? value) => OnPropertyChanged(nameof(InitialsPreview));
    partial void OnStampChanged(byte[]? value) => OnPropertyChanged(nameof(StampPreview));
    partial void OnPfxPathChanged(string? value) => OnPropertyChanged(nameof(PfxName));

    partial void OnSignerNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(NewCertName) || NewCertName == _lastAutoCertName) NewCertName = _lastAutoCertName = value;
    }

    private string _lastAutoCertName = "";

    partial void OnUseDigitalChanged(bool value)
    {
        if (value) LoadStoreCertificates();
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(SelectedItem)) OnPropertyChanged(nameof(PropertiesTarget));
    }

    protected override void OnItemsChanged() => OnPropertyChanged(nameof(HasDigitalBox));

    public override bool KeepToolAfterCreate(EditorTool tool) => false;

    private void LoadStoreCertificates()
    {
        var selected = SelectedCertificate?.Certificate.Thumbprint;
        StoreCertificates.Clear();
        try
        {
            foreach (var c in Certificates.SigningCertificates()) StoreCertificates.Add(new CertificateChoice(c));
        }
        catch (Exception ex)
        {
            AppServices.LogError(ex);
        }
        SelectedCertificate = StoreCertificates.FirstOrDefault(c => c.Certificate.Thumbprint == selected) ?? StoreCertificates.FirstOrDefault();
        OnPropertyChanged(nameof(NoStoreCertificates));
    }

    // ------------------------------------------------------------------ your signature

    [RelayCommand]
    private async Task CreateSignatureAsync()
    {
        if (await SignaturePad.AskAsync("Your signature", SignerName) is { } png) Signature = png;
    }

    [RelayCommand]
    private async Task CreateInitialsAsync()
    {
        if (await SignaturePad.AskAsync("Your initials", InitialsOf(SignerName)) is { } png) Initials = png;
    }

    [RelayCommand]
    private void ChooseStamp()
    {
        var path = AppServices.PickFile("Choose your company stamp", $"Images|{string.Join(";", ImageIO.SupportedExtensions.Select(e => "*" + e))}");
        if (path == null) return;
        try
        {
            Stamp = EditController.LoadImageData(path);
        }
        catch (Exception ex)
        {
            Error = $"That image could not be opened: {ex.Message}";
        }
    }

    private static string InitialsOf(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0])));

    // ------------------------------------------------------------------ placing

    public override EditorItem? CreateAt(EditorTool tool, int page, Point point)
    {
        if (tool == SignatureTool || tool == InitialsTool || tool == StampTool)
        {
            var data = tool == SignatureTool ? Signature : tool == InitialsTool ? Initials : Stamp;
            if (data == null)
            {
                _ = AskThenPlaceAsync(tool, page, point);
                return null;
            }
            return PlaceImage(tool, data, page, point);
        }
        if (tool == NameTool || tool == DateTool || tool == TextTool)
        {
            var text = tool == NameTool ? (string.IsNullOrWhiteSpace(SignerName) ? "Your name" : SignerName)
                : tool == DateTool ? DateTime.Now.ToString(DateFormat)
                : "";
            var item = new TextItem
            {
                PageIndex = page,
                PageWidth = Pages[page].Size.Width,
                AutoWidth = true,
                Text = text,
                FontName = "Arial",
                FontSize = 12,
                Color = "#000000",
            }.WithKind(tool == NameTool ? "Name" : tool == DateTool ? "Date" : "Text");
            var width = Math.Max(tool == TextTool ? 140 : 40, item.MeasureWidth());
            var height = item.MeasureHeight(width);
            item.Bounds = PlaceBox(page, new Point(point.X + width / 2, point.Y + height / 2 - item.FontSize * 0.6), new Size(width, height));
            return item;
        }
        return null;
    }

    /// <summary>Signature not made yet: ask for it first, then place it where the user clicked.</summary>
    private async Task AskThenPlaceAsync(EditorTool tool, int page, Point point)
    {
        if (tool == StampTool) ChooseStamp();
        else if (tool == SignatureTool) await CreateSignatureAsync();
        else await CreateInitialsAsync();
        var data = tool == SignatureTool ? Signature : tool == InitialsTool ? Initials : Stamp;
        if (data == null) return;
        AddItem(PlaceImage(tool, data, page, point));
        ActiveTool = SelectTool;
    }

    private EditorItem PlaceImage(EditorTool tool, byte[] data, int page, Point point)
    {
        var kind = tool == SignatureTool ? "Signature" : tool == InitialsTool ? "Initials" : "Company stamp";
        var item = new ImageItem(data, kind) { PageIndex = page };
        var (maxW, maxH) = tool == SignatureTool ? (180.0, 54.0) : tool == InitialsTool ? (70.0, 36.0) : (120.0, 120.0);
        var scale = Math.Min(maxW / item.Image.PixelWidth, maxH / item.Image.PixelHeight);
        item.Bounds = PlaceBox(page, point, new Size(item.Image.PixelWidth * scale, item.Image.PixelHeight * scale));
        return item;
    }

    public override EditorItem? CreateFromRect(EditorTool tool, int page, Rect rect)
    {
        if (tool != DigitalBoxTool) return null;
        if (rect.Width < 4 || rect.Height < 4) rect = PlaceBox(page, rect.TopLeft, new Size(200, 60));
        // One visible digital signature per document: drawing a new box moves it.
        foreach (var old in Items.OfType<SignatureBoxItem>().ToList()) RemoveItem(old);
        UseDigital = true;
        return new SignatureBoxItem { PageIndex = page, Bounds = rect };
    }

    // ------------------------------------------------------------------ certificates

    [RelayCommand]
    private void ChoosePfx()
    {
        var path = AppServices.PickFile("Choose your certificate", "Certificates (*.pfx;*.p12)|*.pfx;*.p12|All files|*.*");
        if (path != null) PfxPath = path;
    }

    [RelayCommand]
    private void CreateCertificate()
    {
        try
        {
            var cert = Certificates.CreateSelfSigned(NewCertName, NewCertEmail, NewCertOrganization, 3, SaveNewCertToWindows);
            var choice = new CertificateChoice(cert);
            if (SaveNewCertToWindows) LoadStoreCertificates();
            if (StoreCertificates.FirstOrDefault(c => c.Certificate.Thumbprint == cert.Thumbprint) is not { } listed)
            {
                StoreCertificates.Insert(0, choice);
                listed = choice;
            }
            SelectedCertificate = listed;
            Source = CertificateSource.Windows;
            OnPropertyChanged(nameof(NoStoreCertificates));
            AppServices.Notify("Certificate created", $"\"{NewCertName}\" can now sign documents" + (SaveNewCertToWindows ? " and is saved in your Windows certificate store." : " during this session."),
                ControlAppearance.Success, SymbolRegular.Certificate24);
        }
        catch (UserFacingException ex)
        {
            Error = ex.Message;
        }
    }

    private X509Certificate2? ResolveCertificate() => Source switch
    {
        CertificateSource.File when PfxPath != null => Certificates.LoadPfx(PfxPath, PfxPassword),
        CertificateSource.Windows or CertificateSource.New => SelectedCertificate?.Certificate,
        _ => null,
    };

    // ------------------------------------------------------------------ run

    private List<PageElement> Elements() => Items.Select<EditorItem, PageElement?>(i => i switch
    {
        TextItem t when !string.IsNullOrWhiteSpace(t.Text) => t.ToElement(),
        ImageItem img => img.ToElement(),
        _ => null,
    }).OfType<PageElement>().ToList();

    public override string? Validate()
    {
        if (base.Validate() is { } problem) return problem;
        if (!UseDigital && Elements().Count == 0) return "Place your signature on the page, or turn on the digital signature.";
        if (UseDigital)
        {
            if (Source == CertificateSource.File && PfxPath == null) return "Choose your certificate file (.pfx).";
            if (Source != CertificateSource.File && SelectedCertificate == null)
                return Source == CertificateSource.New ? "Create the certificate first." : "Choose a certificate, or create a new one.";
        }
        return null;
    }

    protected override async Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        DigitalSignatureRequest? digital = null;
        if (UseDigital)
        {
            Uri? timestamp = null;
            if (AddTimestamp)
            {
                await Connectivity.EnsureOnlineAsync("A trusted timestamp", ct);
                timestamp = new Uri("http://timestamp.digicert.com");
            }
            var cert = ResolveCertificate() ?? throw new UserFacingException("Choose a certificate first.");
            var box = Items.OfType<SignatureBoxItem>().FirstOrDefault();
            digital = new DigitalSignatureRequest
            {
                Certificate = cert,
                PageIndex = box?.PageIndex ?? CurrentPageIndex,
                Bounds = box?.Bounds ?? Rect.Empty,
                Reason = string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim(),
                Location = string.IsNullOrWhiteSpace(Location) ? null : Location.Trim(),
                AppearanceImage = ShowHandwritingInBox ? Signature : null,
                TimestampServer = timestamp,
            };
        }
        return await SignTool.RunAsync(new SignOptions { Input = Document!.ToInput(), OutputPath = OutputFor("signed"), Elements = Elements(), Digital = digital }, progress, ct);
    }

    public override async Task SeedDemoAsync()
    {
        CurrentPageIndex = 0;
        SignerName = "Ada Lovelace";
        Signature = DemoSignature();
        Initials = ImageIO.EncodePng(SignaturePad.Trim(RenderText("AL", 90)));
        var page = CurrentPageIndex;
        AddItem(PlaceImage(SignatureTool, Signature, page, new Point(150, 640)));
        AddItem(PlaceImage(InitialsTool, Initials, page, new Point(520, 790)));
        if (CreateAt(NameTool, page, new Point(80, 680)) is { } name) AddItem(name);
        if (CreateAt(DateTool, page, new Point(80, 700)) is { } date) AddItem(date);
        if (CreateFromRect(DigitalBoxTool, page, new Rect(300, 600, 230, 64)) is { } box) AddItem(box);
        Source = CertificateSource.New;
        NewCertName = "Ada Lovelace";
        NewCertEmail = "ada@example.com";
        SaveNewCertToWindows = false;
        CreateCertificate();
        Reason = "I approve this document";
        Location = "Colombo";
        await Task.CompletedTask;
    }

    public override void ShowDemoDialog() => _ = SignaturePad.AskAsync("Your signature", SignerName);

    private static byte[] DemoSignature() => ImageIO.EncodePng(SignaturePad.Trim(RenderText("Ada Lovelace", 110)));

    private static BitmapSource RenderText(string text, double size)
    {
        var ft = new System.Windows.Media.FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface("Segoe Script"), size, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x3A, 0x93)), 1.0);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawText(ft, new Point(40, 40));
        var bitmap = new RenderTargetBitmap((int)ft.WidthIncludingTrailingWhitespace + 80, (int)ft.Height + 80, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }
}
