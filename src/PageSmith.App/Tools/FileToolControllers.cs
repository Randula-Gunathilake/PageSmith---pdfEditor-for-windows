using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Common;
using PageSmith.Core.Imaging;
using PageSmith.Core.Intelligence;
using PageSmith.Core.Ocr;
using PageSmith.Core.Office;
using PageSmith.Core.Pdf;
using PageSmith.Core.Tools.Convert;
using PageSmith.Core.Tools.Edit;
using PageSmith.Core.Tools.Intelligence;
using PageSmith.Core.Tools.Optimize;
using PageSmith.Core.Tools.Organize;
using PageSmith.Core.Tools.Security;

namespace PageSmith.App.Tools;

/// <summary>A value with a label, for combo boxes.</summary>
public sealed record Choice(object Value, string Label);

// ---------------------------------------------------------------- Organize

public sealed partial class MergeController() : ToolController("merge")
{
    public override bool CanReorder => true;
    public override bool CanRotateFiles => true;

    [ObservableProperty] public partial bool AddBookmarks { get; set; } = true;
    [ObservableProperty] public partial string OutputName { get; set; } = "merged";

    public override string? Validate() => Files.Count < 2 ? "Add at least two PDFs to merge." : base.Validate();

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var output = OutputPaths.Unique(OutputDirectory(), OutputPaths.Sanitize(string.IsNullOrWhiteSpace(OutputName) ? "merged" : OutputName) + ".pdf");
        return MergeTool.RunAsync(new MergeOptions
        {
            Items = Files.Select(f => new MergeItem(f.ToInput(), f.Rotation)).ToList(),
            OutputPath = output,
            AddBookmarks = AddBookmarks,
        }, progress, ct);
    }
}

/// <summary>An editable page range ("from" and "to", 1-based) for Split → Range.</summary>
public sealed partial class RangeRow : ObservableObject
{
    [ObservableProperty] public partial double From { get; set; } = 1;
    [ObservableProperty] public partial double To { get; set; } = 1;
    public string Spec => $"{(int)From}-{(int)To}";
}

public sealed partial class SplitController() : ToolController("split")
{
    [ObservableProperty] public partial string Tab { get; set; } = "Range";
    [ObservableProperty] public partial bool FixedRanges { get; set; }
    [ObservableProperty] public partial double PagesPerFile { get; set; } = 1;
    [ObservableProperty] public partial bool MergeRanges { get; set; }
    [ObservableProperty] public partial bool ExtractAll { get; set; } = true;
    [ObservableProperty] public partial string SelectedPages { get; set; } = "";
    [ObservableProperty] public partial bool MergeExtracted { get; set; }
    [ObservableProperty] public partial double MaxSize { get; set; } = 5;
    [ObservableProperty] public partial bool SizeInMegabytes { get; set; } = true;
    [ObservableProperty] public partial bool AllowCompression { get; set; }
    [ObservableProperty] public partial SmartSplitStrategy SmartStrategy { get; set; } = SmartSplitStrategy.Auto;
    [ObservableProperty] public partial bool DropSeparators { get; set; } = true;

    public ObservableCollection<RangeRow> Ranges { get; } = [new() { From = 1, To = 1 }];

    public int PageCount => Files.FirstOrDefault()?.PageCount ?? 0;

    public string Explanation => Tab switch
    {
        "Range" when FixedRanges => PageCount > 0
            ? $"This PDF will be split into files of {(int)PagesPerFile} page{((int)PagesPerFile == 1 ? "" : "s")}: {Math.Ceiling(PageCount / Math.Max(1, PagesPerFile))} PDFs will be created."
            : "Choose how many pages each file gets.",
        "Range" => MergeRanges ? "All ranges will be merged into one PDF." : $"{Ranges.Count} PDF{(Ranges.Count == 1 ? "" : "s")} will be created.",
        "Pages" when ExtractAll => PageCount > 0 ? $"Every page becomes its own PDF: {PageCount} PDFs will be created." : "Every page becomes its own PDF.",
        "Pages" => MergeExtracted ? "The selected pages will be merged into one PDF." : "Each selected page becomes its own PDF.",
        "Size" => PageCount > 0 ? $"Files will be no larger than {MaxSize:0.#} {(SizeInMegabytes ? "MB" : "KB")} each." : "Choose the maximum size of each file.",
        _ => "PageSmith looks for bookmarks or blank separator pages and splits there — handy for batches of scanned documents.",
    };

    partial void OnTabChanged(string value) => OnPropertyChanged(nameof(Explanation));
    partial void OnFixedRangesChanged(bool value) => OnPropertyChanged(nameof(Explanation));
    partial void OnPagesPerFileChanged(double value) => OnPropertyChanged(nameof(Explanation));
    partial void OnMergeRangesChanged(bool value) => OnPropertyChanged(nameof(Explanation));
    partial void OnExtractAllChanged(bool value) => OnPropertyChanged(nameof(Explanation));
    partial void OnMergeExtractedChanged(bool value) => OnPropertyChanged(nameof(Explanation));
    partial void OnMaxSizeChanged(double value) => OnPropertyChanged(nameof(Explanation));

    protected override void OnFilesChanged()
    {
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(Explanation));
    }

    protected override Task OnFilesAddedAsync(IReadOnlyList<FileEntry> added)
    {
        if (added.Count > 0 && added[0].PageCount > 0)
        {
            Ranges.Clear();
            Ranges.Add(new RangeRow { From = 1, To = added[0].PageCount });
        }
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(Explanation));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void AddRange()
    {
        var last = Ranges.LastOrDefault();
        var from = Math.Min(PageCount > 0 ? PageCount : 1, (last?.To ?? 0) + 1);
        Ranges.Add(new RangeRow { From = from, To = Math.Max(from, PageCount) });
        OnPropertyChanged(nameof(Explanation));
    }

    [RelayCommand]
    private void RemoveRange(RangeRow row)
    {
        if (Ranges.Count > 1) Ranges.Remove(row);
        OnPropertyChanged(nameof(Explanation));
    }

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var file = Files[0];
        var options = new SplitOptions { Input = file.ToInput(), OutputDirectory = OutputDirectory() };
        options = Tab switch
        {
            "Range" when FixedRanges => options with { Mode = SplitMode.FixedRange, PagesPerFile = Math.Max(1, (int)PagesPerFile) },
            "Range" => options with { Mode = SplitMode.CustomRanges, Ranges = Ranges.Select(r => r.Spec).ToList(), MergeIntoOne = MergeRanges },
            "Pages" when ExtractAll => options with { Mode = SplitMode.ExtractAll },
            "Pages" => options with { Mode = SplitMode.SelectPages, SelectedPages = SelectedPages, MergeIntoOne = MergeExtracted },
            "Size" => options with
            {
                Mode = SplitMode.BySize,
                MaxBytesPerFile = (long)(MaxSize * (SizeInMegabytes ? 1024 * 1024 : 1024)),
                AllowCompression = AllowCompression,
            },
            _ => options with { Mode = SplitMode.Smart, SmartStrategy = SmartStrategy, DropSeparatorPages = DropSeparators },
        };
        return SplitTool.RunAsync(options, progress, ct);
    }
}

// ---------------------------------------------------------------- Optimize

public sealed partial class CompressController() : ToolController("compress")
{
    [ObservableProperty] public partial CompressionLevel Level { get; set; } = CompressionLevel.Recommended;

    protected override async Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var total = new ToolResult();
        var scope = new ProgressScope(progress);
        for (var i = 0; i < Files.Count; i++)
        {
            var f = Files[i];
            var r = await CompressTool.RunAsync(new CompressOptions { Input = f.ToInput(), OutputPath = OutputFile(f.Path, "compressed", ".pdf"), Level = Level },
                scope.Slice((double)i / Files.Count, (double)(i + 1) / Files.Count).AsProgress(), ct);
            Merge(total, r);
        }
        var saved = 1 - (double)total.OutputBytes / Math.Max(1, total.InputBytes);
        total.Summary = Files.Count == 1
            ? total.Summary
            : $"{Files.Count} PDFs compressed: {CompressTool.Format(total.InputBytes)} → {CompressTool.Format(total.OutputBytes)} ({Math.Max(0, saved):P0} smaller).";
        return total;
    }

    internal static void Merge(ToolResult total, ToolResult part)
    {
        total.InputBytes += part.InputBytes;
        foreach (var o in part.OutputFiles) total.AddOutput(o);
        total.Warnings.AddRange(part.Warnings.Except(total.Warnings));
        total.Summary = part.Summary;
        if (part.Text != null) total.Text = total.Text == null ? part.Text : total.Text + "\n\n" + part.Text;
    }
}

public sealed partial class RepairController() : ToolController("repair")
{
    protected override async Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var total = new ToolResult();
        var summaries = new List<string>();
        foreach (var f in Files)
        {
            var r = await RepairTool.RunAsync(new RepairOptions { Input = f.ToInput(), OutputPath = OutputFile(f.Path, "repaired", ".pdf") }, progress, ct);
            CompressController.Merge(total, r);
            summaries.Add($"{f.Name}: {r.Summary}");
        }
        total.Summary = Files.Count == 1 ? summaries[0][(Files[0].Name.Length + 2)..] : string.Join(" ", summaries);
        return total;
    }
}

public sealed partial class OcrController() : ToolController("ocr")
{
    public IReadOnlyList<OcrLanguage> Languages { get; } = OcrService.AvailableLanguages();
    public bool HasLanguages => Languages.Count > 0;

    [ObservableProperty] public partial OcrLanguage? Language { get; set; }
    [ObservableProperty] public partial bool SkipPagesWithText { get; set; } = true;
    [ObservableProperty] public partial bool ExportText { get; set; }

    public override Task OnShownAsync()
    {
        Language ??= Languages.FirstOrDefault(l => l.Tag == AppServices.Settings.OcrLanguage) ?? Languages.FirstOrDefault();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private static void AddLanguages() => AppServices.OpenUri("ms-settings:regionlanguage");

    public override string? Validate() => !HasLanguages
        ? "No OCR language is installed in Windows. Use \"Add languages\" to install one (Settings › Time & language › Language & region)."
        : base.Validate();

    protected override async Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        AppServices.Settings.OcrLanguage = Language?.Tag;
        var total = new ToolResult();
        foreach (var f in Files)
        {
            var r = await OcrTool.RunAsync(new OcrOptions
            {
                Input = f.ToInput(), OutputPath = OutputFile(f.Path, "ocr", ".pdf"), Language = Language?.Tag,
                SkipPagesWithText = SkipPagesWithText, ExportText = ExportText,
            }, progress, ct);
            CompressController.Merge(total, r);
        }
        return total;
    }
}

// ---------------------------------------------------------------- Convert to PDF

public sealed partial class ImagesToPdfController() : ToolController("jpg-to-pdf")
{
    public override bool CanReorder => true;
    public override bool CanRotateFiles => true;

    [ObservableProperty] public partial PaperOrientation Orientation { get; set; } = PaperOrientation.Portrait;
    [ObservableProperty] public partial ImagePageSize PageSize { get; set; } = ImagePageSize.A4;
    [ObservableProperty] public partial PageMargin Margin { get; set; } = PageMargin.None;
    [ObservableProperty] public partial bool MergeIntoOne { get; set; } = true;

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct) =>
        ImagesToPdfTool.RunAsync(new ImagesToPdfOptions
        {
            Images = Files.Select(f => f.Path).ToList(),
            OutputPath = OutputPaths.Unique(OutputDirectory(), Files.Count == 1 || !MergeIntoOne ? Path.GetFileNameWithoutExtension(Files[0].Path) + ".pdf" : "images.pdf"),
            Orientation = PageSize == ImagePageSize.Fit ? PaperOrientation.Auto : Orientation,
            PageSize = PageSize,
            Margin = Margin,
            MergeIntoOne = MergeIntoOne,
            Rotations = Files.Where(f => f.Rotation != 0).ToDictionary(f => f.Path, f => f.Rotation),
        }, progress, ct);
}

public sealed partial class OfficeToPdfController(string id) : ToolController(id)
{
    [ObservableProperty] public partial ConversionEngine Engine { get; set; } = AppServices.Settings.OfficeEngine;

    public string EngineStatus
    {
        get
        {
            var app = Descriptor.Input switch { InputKind.Excel => OfficeApp.Excel, InputKind.PowerPoint => OfficeApp.PowerPoint, _ => OfficeApp.Word };
            var parts = new List<string>
            {
                OfficeDetection.IsMicrosoftOfficeAppInstalled(app) ? $"Microsoft {app} is installed (best fidelity)." : $"Microsoft {app} was not found.",
                Core.Office.LibreOffice.IsInstalled ? "LibreOffice is installed." : "LibreOffice was not found.",
                "The built-in converter handles .docx, .xlsx, .pptx, .csv and .txt without any office suite.",
            };
            return string.Join(" ", parts);
        }
    }

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct) =>
        OfficeToPdfTool.RunAsync(new OfficeToPdfOptions { Files = Files.Select(f => f.ToInput()).ToList(), OutputDirectory = OutputDirectory(), Engine = Engine }, progress, ct);
}

// ---------------------------------------------------------------- Convert from PDF

public sealed partial class PdfToImagesController() : ToolController("pdf-to-jpg")
{
    [ObservableProperty] public partial PdfToImageMode Mode { get; set; } = PdfToImageMode.PageToImage;
    [ObservableProperty] public partial ImageFormatKind Format { get; set; } = ImageFormatKind.Jpeg;
    [ObservableProperty] public partial string Quality { get; set; } = "Normal";
    [ObservableProperty] public partial double CustomDpi { get; set; } = 200;
    [ObservableProperty] public partial string Pages { get; set; } = "";
    [ObservableProperty] public partial bool MultiPageTiff { get; set; }

    private double Dpi => Quality switch { "High" => 300, "Custom" => Math.Clamp(CustomDpi, 36, 1200), _ => 150 };

    protected override async Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var total = new ToolResult();
        foreach (var f in Files)
        {
            var r = await PdfToImagesTool.RunAsync(new PdfToImagesOptions
            {
                Input = f.ToInput(), OutputDirectory = OutputDirectory(f.Path), Mode = Mode, Format = Format, Dpi = Dpi,
                Pages = string.IsNullOrWhiteSpace(Pages) ? null : Pages, MultiPageTiff = MultiPageTiff,
            }, progress, ct);
            CompressController.Merge(total, r);
        }
        if (Files.Count > 1) total.Summary = $"Created {total.OutputFiles.Count} images from {Files.Count} PDFs.";
        return total;
    }
}

public sealed partial class PdfToOfficeController(string id) : ToolController(id)
{
    public bool IsWord => Descriptor.Id == "pdf-to-word";
    public bool IsPowerPoint => Descriptor.Id == "pdf-to-powerpoint";
    public bool IsExcel => Descriptor.Id == "pdf-to-excel";
    public bool WordInstalled => OfficeDetection.IsMicrosoftOfficeAppInstalled(OfficeApp.Word);
    public IReadOnlyList<OcrLanguage> Languages { get; } = OcrService.AvailableLanguages();

    [ObservableProperty] public partial bool Ocr { get; set; }
    [ObservableProperty] public partial OcrLanguage? OcrLanguage { get; set; }
    [ObservableProperty] public partial bool UseWord { get; set; } = AppServices.Settings.PdfToWordEngine != ConversionEngine.BuiltIn;
    [ObservableProperty] public partial bool SlidesAsImages { get; set; }
    [ObservableProperty] public partial bool SheetPerPage { get; set; }

    public override Task OnShownAsync()
    {
        OcrLanguage ??= Languages.FirstOrDefault(l => l.Tag == AppServices.Settings.OcrLanguage) ?? Languages.FirstOrDefault();
        return Task.CompletedTask;
    }

    protected override async Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var total = new ToolResult();
        foreach (var f in Files)
        {
            var ext = IsWord ? ".docx" : IsPowerPoint ? ".pptx" : ".xlsx";
            var options = new PdfToOfficeOptions
            {
                Input = f.ToInput(),
                OutputPath = OutputPaths.Unique(OutputDirectory(f.Path), Path.GetFileNameWithoutExtension(f.Path) + ext),
                Ocr = Ocr, OcrLanguage = OcrLanguage?.Tag,
                Engine = UseWord && WordInstalled ? ConversionEngine.Auto : ConversionEngine.BuiltIn,
                SlidesAsImages = SlidesAsImages, SheetPerPage = SheetPerPage,
            };
            var r = IsWord ? await PdfToWordTool.RunAsync(options, progress, ct)
                : IsPowerPoint ? await PdfToPowerPointTool.RunAsync(options, progress, ct)
                : await PdfToExcelTool.RunAsync(options, progress, ct);
            CompressController.Merge(total, r);
        }
        return total;
    }
}

public sealed record ConformanceOption(PdfAConformance Value, string Title, string Description);

public sealed partial class PdfToPdfAController() : ToolController("pdf-to-pdfa")
{
    public IReadOnlyList<ConformanceOption> Conformances { get; } =
    [
        new(PdfAConformance.PdfA1b, "PDF/A-1b", "PDF 1.4 base. Basic level: fonts embedded, colour managed, metadata. No transparency."),
        new(PdfAConformance.PdfA1a, "PDF/A-1a", "PDF 1.4 with accessibility: needs a tagged PDF (falls back to 1b)."),
        new(PdfAConformance.PdfA2b, "PDF/A-2b", "PDF 1.7 base: allows transparency, layers and JPEG 2000. Recommended."),
        new(PdfAConformance.PdfA2u, "PDF/A-2u", "Like 2b, and all text maps to Unicode (searchable and copyable)."),
        new(PdfAConformance.PdfA2a, "PDF/A-2a", "Like 2u, plus accessibility tags (needs a tagged PDF)."),
        new(PdfAConformance.PdfA3b, "PDF/A-3b", "Like 2b, and any file type may be attached."),
        new(PdfAConformance.PdfA3u, "PDF/A-3u", "Like 3b with Unicode text."),
        new(PdfAConformance.PdfA3a, "PDF/A-3a", "Like 3u plus accessibility tags."),
    ];

    [ObservableProperty] public partial ConformanceOption? Conformance { get; set; }

    public override Task OnShownAsync()
    {
        Conformance ??= Conformances[2];
        return Task.CompletedTask;
    }

    protected override async Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var total = new ToolResult();
        foreach (var f in Files)
        {
            var r = await PdfToPdfATool.RunAsync(new PdfToPdfAOptions
            {
                Input = f.ToInput(), OutputPath = OutputFile(f.Path, "pdfa", ".pdf"), Conformance = Conformance?.Value ?? PdfAConformance.PdfA2b,
            }, progress, ct);
            CompressController.Merge(total, r);
        }
        return total;
    }
}

public sealed partial class PdfToMarkdownController() : ToolController("pdf-to-markdown")
{
    [ObservableProperty] public partial bool ExportImages { get; set; } = true;
    [ObservableProperty] public partial bool Ocr { get; set; }

    protected override async Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var total = new ToolResult();
        foreach (var f in Files)
        {
            var r = await PdfToMarkdownTool.RunAsync(new PdfToMarkdownOptions
            {
                Input = f.ToInput(), OutputPath = OutputPaths.Unique(OutputDirectory(f.Path), Path.GetFileNameWithoutExtension(f.Path) + ".md"),
                ExportImages = ExportImages, Ocr = Ocr, OcrLanguage = AppServices.Settings.OcrLanguage,
            }, progress, ct);
            CompressController.Merge(total, r);
        }
        return total;
    }
}

// ---------------------------------------------------------------- Edit

public sealed partial class PageNumbersController() : ToolController("page-numbers")
{
    public IReadOnlyList<string> Fonts { get; } = WindowsFontResolver.InstalledFamilies();

    [ObservableProperty] public partial bool FacingPages { get; set; }
    [ObservableProperty] public partial bool FirstPageIsCover { get; set; }
    [ObservableProperty] public partial PagePosition Position { get; set; } = PagePosition.BottomCenter;
    [ObservableProperty] public partial NumberMargin Margin { get; set; } = NumberMargin.Recommended;
    [ObservableProperty] public partial double FirstNumber { get; set; } = 1;
    [ObservableProperty] public partial double FromPage { get; set; } = 1;
    [ObservableProperty] public partial double? ToPage { get; set; }
    [ObservableProperty] public partial string TextMode { get; set; } = "Number";
    [ObservableProperty] public partial string CustomTemplate { get; set; } = "Page {n} of {p}";
    [ObservableProperty] public partial string FontFamily { get; set; } = "Arial";
    [ObservableProperty] public partial double FontSize { get; set; } = 11;
    [ObservableProperty] public partial bool Bold { get; set; }
    [ObservableProperty] public partial bool Italic { get; set; }
    [ObservableProperty] public partial bool Underline { get; set; }
    [ObservableProperty] public partial string Color { get; set; } = "#000000";

    private string Template => TextMode switch
    {
        "Page" => "Page {n}",
        "PageOf" => "Page {n} of {p}",
        "Custom" => CustomTemplate,
        _ => "{n}",
    };

    protected override async Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct) =>
        await PageNumbersTool.RunAsync(new PageNumberOptions
        {
            Files = Files.Select(f => f.ToInput()).ToList(), OutputDirectory = OutputDirectory(),
            Position = Position, Margin = Margin, FacingPages = FacingPages, FirstPageIsCover = FirstPageIsCover,
            FirstNumber = (int)FirstNumber,
            FromPage = FromPage > 1 ? (int)FromPage : null,
            ToPage = ToPage >= 1 ? (int)ToPage.Value : null,
            Template = Template,
            Style = new TextStyle { Font = FontFamily, Size = FontSize, Bold = Bold, Italic = Italic, Underline = Underline, Color = Color },
        }, progress, ct);
}

public sealed partial class WatermarkController() : ToolController("watermark")
{
    public IReadOnlyList<string> Fonts { get; } = WindowsFontResolver.InstalledFamilies();

    [ObservableProperty] public partial bool UseImage { get; set; }
    [ObservableProperty] public partial string Text { get; set; } = "CONFIDENTIAL";
    [ObservableProperty] public partial string FontFamily { get; set; } = "Arial";
    [ObservableProperty] public partial double FontSize { get; set; } = 48;
    [ObservableProperty] public partial bool Bold { get; set; } = true;
    [ObservableProperty] public partial bool Italic { get; set; }
    [ObservableProperty] public partial bool Underline { get; set; }
    [ObservableProperty] public partial string Color { get; set; } = "#D32F2F";
    [ObservableProperty] public partial string? ImagePath { get; set; }
    [ObservableProperty] public partial double ImageScale { get; set; } = 40;
    [ObservableProperty] public partial PagePosition Position { get; set; } = PagePosition.Center;
    [ObservableProperty] public partial bool Mosaic { get; set; }
    [ObservableProperty] public partial double Transparency { get; set; } = 50;
    [ObservableProperty] public partial double Rotation { get; set; } = 45;
    [ObservableProperty] public partial double FromPage { get; set; } = 1;
    [ObservableProperty] public partial double? ToPage { get; set; }
    [ObservableProperty] public partial bool BelowContent { get; set; }

    public IReadOnlyList<Choice> TransparencyChoices { get; } = [new(0d, "No transparency"), new(25d, "25%"), new(50d, "50%"), new(75d, "75%")];
    public IReadOnlyList<Choice> RotationChoices { get; } = [new(0d, "Do not rotate"), new(45d, "45 degrees"), new(90d, "90 degrees"), new(180d, "180 degrees"), new(270d, "270 degrees")];

    public string ImageName => ImagePath == null ? "No image chosen" : Path.GetFileName(ImagePath);
    partial void OnImagePathChanged(string? value) => OnPropertyChanged(nameof(ImageName));

    [RelayCommand]
    private void ChooseImage()
    {
        var path = AppServices.PickFile("Choose the watermark image", "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff");
        if (path != null)
        {
            ImagePath = path;
            UseImage = true;
        }
    }

    public override string? Validate() =>
        UseImage && ImagePath == null ? "Choose an image for the watermark." :
        !UseImage && string.IsNullOrWhiteSpace(Text) ? "Type the watermark text." : base.Validate();

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        string? pages = null;
        if (FromPage > 1 || ToPage >= 1) pages = $"{Math.Max(1, (int)FromPage)}-{(ToPage >= 1 ? ((int)ToPage.Value).ToString() : "")}";
        return WatermarkTool.RunAsync(new WatermarkOptions
        {
            Files = Files.Select(f => f.ToInput()).ToList(), OutputDirectory = OutputDirectory(),
            Kind = UseImage ? WatermarkKind.Image : WatermarkKind.Text, Text = Text, ImagePath = ImagePath, ImageScale = ImageScale / 100,
            Style = new TextStyle { Font = FontFamily, Size = FontSize, Bold = Bold, Italic = Italic, Underline = Underline, Color = Color },
            Position = Position, Mosaic = Mosaic, Opacity = 1 - Transparency / 100, Rotation = Rotation,
            Pages = Files.Count > 1 ? null : pages, BelowContent = BelowContent,
        }, progress, ct);
    }
}

// ---------------------------------------------------------------- Security

public sealed partial class UnlockController() : ToolController("unlock")
{
    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct) =>
        UnlockTool.RunAsync(new UnlockOptions { Files = Files.Select(f => f.ToInput()).ToList(), OutputDirectory = OutputDirectory() }, progress, ct);
}

public sealed partial class ProtectController() : ToolController("protect")
{
    [ObservableProperty] public partial bool Advanced { get; set; }
    [ObservableProperty] public partial string OpenPassword { get; set; } = "";
    [ObservableProperty] public partial string ConfirmPassword { get; set; } = "";
    [ObservableProperty] public partial string PermissionsPassword { get; set; } = "";
    [ObservableProperty] public partial bool DisablePrinting { get; set; }
    [ObservableProperty] public partial bool DisableModification { get; set; }
    [ObservableProperty] public partial bool DisableCopying { get; set; }
    [ObservableProperty] public partial bool DisableComments { get; set; }
    [ObservableProperty] public partial bool DisableFormFilling { get; set; }
    [ObservableProperty] public partial bool DisableAssembly { get; set; }
    [ObservableProperty] public partial EncryptionStrength Strength { get; set; } = EncryptionStrength.Aes256;

    public int Score => ProtectTool.PasswordScore(OpenPassword);
    public string ScoreText => Score switch { 0 => "", 1 => "Weak", 2 => "Fair", 3 => "Good", _ => "Strong" };
    public bool Mismatch => ConfirmPassword.Length > 0 && ConfirmPassword != OpenPassword;

    partial void OnOpenPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(Score));
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(Mismatch));
    }

    partial void OnConfirmPasswordChanged(string value) => OnPropertyChanged(nameof(Mismatch));

    public override string? Validate()
    {
        if (OpenPassword.Length > 0 && OpenPassword != ConfirmPassword) return "The passwords do not match.";
        var restricts = Advanced && (DisablePrinting || DisableModification || DisableCopying || DisableComments || DisableFormFilling || DisableAssembly);
        if (OpenPassword.Length == 0 && !restricts) return "Type a password (or set restrictions under Advanced).";
        return base.Validate();
    }

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct) =>
        ProtectTool.RunAsync(new ProtectOptions
        {
            Files = Files.Select(f => f.ToInput()).ToList(), OutputDirectory = OutputDirectory(),
            OpenPassword = OpenPassword,
            PermissionsPassword = Advanced && PermissionsPassword.Length > 0 ? PermissionsPassword : null,
            AllowPrinting = !(Advanced && DisablePrinting),
            AllowHighQualityPrinting = !(Advanced && DisablePrinting),
            AllowModification = !(Advanced && DisableModification),
            AllowCopying = !(Advanced && DisableCopying),
            AllowComments = !(Advanced && DisableComments),
            AllowFormFilling = !(Advanced && DisableFormFilling),
            AllowAssembly = !(Advanced && DisableAssembly),
            Strength = Strength,
        }, progress, ct);
}

// ---------------------------------------------------------------- Intelligence

public sealed partial class SummarizeController() : ToolController("summarize")
{
    [ObservableProperty] public partial SummaryLength Length { get; set; } = SummaryLength.Medium;
    [ObservableProperty] public partial string OutputFormat { get; set; } = "Markdown";

    /// <summary>This PC, or the online AI service picked from the list.</summary>
    [ObservableProperty] public partial SummaryService Service { get; set; } = AppServices.Settings.Summarizer;

    [ObservableProperty] public partial string Language { get; set; } = AppServices.Settings.SummaryLanguage;

    public IReadOnlyList<string> Languages { get; } = Core.Intelligence.Languages.All.Select(l => l.Name).ToList();

    private AiProvider? Provider => Service switch
    {
        SummaryService.Claude => AiProvider.Claude,
        SummaryService.Gemini => AiProvider.Gemini,
        SummaryService.OpenAI => AiProvider.OpenAI,
        _ => null,
    };

    public bool UsesAi => Provider != null;

    public string ServiceNote => Provider is { } p
        ? $"{AiService.Describe(p)} writes the summary in its own words. The document's text is sent to {AiService.Company(p)} with your API key, so this needs the internet."
        : "Picks the key sentences and keywords on this PC. Your document never leaves your computer.";

    /// <summary>An AI service is picked but has no API key yet.</summary>
    public bool NeedsKey => Provider is { } p && !AiService.HasKey(AppServices.Settings.KeyFor(p));

    partial void OnServiceChanged(SummaryService value)
    {
        OnPropertyChanged(nameof(UsesAi));
        OnPropertyChanged(nameof(ServiceNote));
        OnPropertyChanged(nameof(NeedsKey));
    }

    public override Task OnShownAsync()
    {
        // A key may have been added in Settings since.
        OnPropertyChanged(nameof(NeedsKey));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private static void OpenAiSettings() => AppServices.NavigateTo(typeof(Pages.SettingsPage));

    public override Task SeedDemoAsync()
    {
        Service = SummaryService.Gemini;
        Language = "Spanish";
        return Task.CompletedTask;
    }

    public override string? Validate() =>
        base.Validate() ?? (NeedsKey && Provider is { } p
            ? $"Add your {AiService.Company(p)} API key in Settings › AI services, or summarize on this PC."
            : null);

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var f = Files[0];
        var ext = OutputFormat switch { "PDF" => ".pdf", "Text" => ".txt", _ => ".md" };
        AppServices.Settings.Summarizer = Service;
        AppServices.Settings.SummaryLanguage = Language;
        AppServices.SaveSettings();
        return SummarizeTool.RunAsync(new SummarizeOptions
        {
            Input = f.ToInput(),
            OutputPath = OutputPaths.Unique(OutputDirectory(f.Path), Path.GetFileNameWithoutExtension(f.Path) + "_summary" + ext),
            Length = Length,
            Ai = Provider is { } p ? AppServices.Settings.CreateAi(p) : null,
            OutputLanguage = Language,
        }, progress, ct);
    }
}

public sealed partial class TranslateController() : ToolController("translate")
{
    public IReadOnlyList<LanguageInfo> SourceLanguages { get; } = [new("", "Detect automatically"), .. Core.Intelligence.Languages.All];
    public IReadOnlyList<LanguageInfo> TargetLanguages { get; } = Core.Intelligence.Languages.All;

    [ObservableProperty] public partial LanguageInfo? Source { get; set; }
    [ObservableProperty] public partial LanguageInfo? Target { get; set; }
    [ObservableProperty] public partial TranslationProvider Provider { get; set; } = AppServices.Settings.Translator;
    [ObservableProperty] public partial bool ExportText { get; set; }

    public override Task OnShownAsync()
    {
        Source ??= SourceLanguages[0];
        Target ??= TargetLanguages.First(l => l.Code == "es");
        return Task.CompletedTask;
    }

    private AiProvider? AiProviderPicked => Provider switch
    {
        TranslationProvider.Claude => AiProvider.Claude,
        TranslationProvider.Gemini => AiProvider.Gemini,
        TranslationProvider.OpenAI => AiProvider.OpenAI,
        _ => null,
    };

    public override string? Validate() =>
        base.Validate() ?? (AiProviderPicked is { } p && !AiService.HasKey(AppServices.Settings.KeyFor(p))
            ? $"Add your {AiService.Company(p)} API key in Settings › AI services, or pick another translation service."
            : null);

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var f = Files[0];
        AppServices.Settings.Translator = Provider;
        var target = Target ?? TargetLanguages[0];
        return TranslateTool.RunAsync(new TranslateOptions
        {
            Input = f.ToInput(),
            OutputPath = OutputFile(f.Path, target.Code, ".pdf"),
            Translator = AppServices.Settings.CreateTranslator(),
            SourceLanguage = string.IsNullOrEmpty(Source?.Code) ? null : Source.Code,
            TargetLanguage = target.Code,
            ExportText = ExportText,
        }, progress, ct);
    }
}
