using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using PageSmith.Core.Pdf;
using PageSmith.Core.Tools.Convert;
using PageSmith.Core.Tools.Edit;
using PageSmith.Core.Tools.Optimize;
using PageSmith.Core.Tools.Organize;
using PageSmith.Core.Tools.Security;

namespace PageSmith.Core.Workflows;

/// <summary>One step of a workflow: a tool with its saved options, applied to one PDF.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "tool")]
[JsonDerivedType(typeof(CompressStep), "compress")]
[JsonDerivedType(typeof(RotateStep), "rotate")]
[JsonDerivedType(typeof(PageNumbersStep), "page-numbers")]
[JsonDerivedType(typeof(WatermarkStep), "watermark")]
[JsonDerivedType(typeof(ProtectStep), "protect")]
[JsonDerivedType(typeof(UnlockStep), "unlock")]
[JsonDerivedType(typeof(OcrStep), "ocr")]
[JsonDerivedType(typeof(PdfAStep), "pdfa")]
[JsonDerivedType(typeof(RepairStep), "repair")]
[JsonDerivedType(typeof(RemovePagesStep), "remove-pages")]
[JsonDerivedType(typeof(ExtractPagesStep), "extract-pages")]
[JsonDerivedType(typeof(CropStep), "crop")]
[JsonDerivedType(typeof(RedactPatternsStep), "redact-patterns")]
[JsonDerivedType(typeof(MetadataStep), "metadata")]
public abstract record WorkflowStep
{
    [JsonIgnore]
    public abstract string Title { get; }

    [JsonIgnore]
    public abstract string Description { get; }

    /// <summary>Runs the step from <paramref name="input"/> to <paramref name="output"/>.</summary>
    public abstract Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct);

    protected static async Task MoveSingle(ToolResult result, string output)
    {
        var produced = result.OutputFiles.FirstOrDefault() ?? throw new UserFacingException("A workflow step produced no file.");
        if (!string.Equals(produced, output, StringComparison.OrdinalIgnoreCase)) File.Move(produced, output, true);
        var dir = Path.GetDirectoryName(produced);
        if (dir != null && Path.GetFileName(dir).StartsWith("pagesmith_wf_", StringComparison.Ordinal))
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
        await Task.CompletedTask;
    }

    protected static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pagesmith_wf_" + Guid.NewGuid().ToString("N")[..8])).FullName;
}

public sealed record CompressStep : WorkflowStep
{
    public CompressionLevel Level { get; init; } = CompressionLevel.Recommended;
    public override string Title => "Compress";
    public override string Description => $"{Level} compression";

    public override async Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct) =>
        await CompressTool.RunAsync(new CompressOptions { Input = input, OutputPath = output, Level = Level }, progress, ct);
}

public sealed record RotateStep : WorkflowStep
{
    public int Degrees { get; init; } = 90;
    public PageFilter Filter { get; init; } = PageFilter.All;
    public override string Title => "Rotate";
    public override string Description => $"{Degrees}° clockwise ({Filter.ToString().ToLowerInvariant()} pages)";

    public override async Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var dir = TempDir();
        var r = await RotateTool.RunAsync(new RotateOptions { Items = [new RotateItem(input, Degrees)], OutputDirectory = dir, Filter = Filter }, progress, ct);
        await MoveSingle(r, output);
    }
}

public sealed record PageNumbersStep : WorkflowStep
{
    public PagePosition Position { get; init; } = PagePosition.BottomCenter;
    public string Template { get; init; } = "{n}";
    public int FirstNumber { get; init; } = 1;
    public bool FirstPageIsCover { get; init; }
    public TextStyle Style { get; init; } = new() { Size = 11 };
    public override string Title => "Page numbers";
    public override string Description => $"\"{Template}\" at {Position}";

    public override async Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var dir = TempDir();
        var r = await PageNumbersTool.RunAsync(new PageNumberOptions
        {
            Files = [input], OutputDirectory = dir, Position = Position, Template = Template, FirstNumber = FirstNumber,
            FirstPageIsCover = FirstPageIsCover, Style = Style,
        }, progress, ct);
        await MoveSingle(r, output);
    }
}

public sealed record WatermarkStep : WorkflowStep
{
    public string Text { get; init; } = "CONFIDENTIAL";
    public string? ImagePath { get; init; }
    public TextStyle Style { get; init; } = new() { Size = 48, Bold = true, Color = "#D32F2F" };
    public double Opacity { get; init; } = 0.4;
    public double Rotation { get; init; } = 45;
    public PagePosition Position { get; init; } = PagePosition.Center;
    public bool Mosaic { get; init; }
    public override string Title => "Watermark";
    public override string Description => ImagePath != null ? $"Image {Path.GetFileName(ImagePath)}" : $"\"{Text}\"";

    public override async Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var dir = TempDir();
        var r = await WatermarkTool.RunAsync(new WatermarkOptions
        {
            Files = [input], OutputDirectory = dir, Kind = ImagePath != null ? WatermarkKind.Image : WatermarkKind.Text,
            Text = Text, ImagePath = ImagePath, Style = Style, Opacity = Opacity, Rotation = Rotation, Position = Position, Mosaic = Mosaic,
        }, progress, ct);
        await MoveSingle(r, output);
    }
}

public sealed record ProtectStep : WorkflowStep
{
    /// <summary>DPAPI-encrypted open password (only this Windows user can read it back).</summary>
    public string ProtectedOpenPassword { get; init; } = "";
    public string ProtectedPermissionsPassword { get; init; } = "";
    public bool AllowPrinting { get; init; } = true;
    public bool AllowCopying { get; init; } = true;
    public bool AllowModification { get; init; } = true;
    public EncryptionStrength Strength { get; init; } = EncryptionStrength.Aes256;
    public override string Title => "Protect";
    public override string Description => $"{ProtectTool.Describe(Strength)}{(ProtectedOpenPassword.Length > 0 ? ", open password" : "")}";

    public override async Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var dir = TempDir();
        var r = await ProtectTool.RunAsync(new ProtectOptions
        {
            Files = [input], OutputDirectory = dir,
            OpenPassword = SecretStore.Unprotect(ProtectedOpenPassword),
            PermissionsPassword = SecretStore.Unprotect(ProtectedPermissionsPassword),
            AllowPrinting = AllowPrinting, AllowCopying = AllowCopying, AllowModification = AllowModification, Strength = Strength,
        }, progress, ct);
        await MoveSingle(r, output);
    }
}

public sealed record UnlockStep : WorkflowStep
{
    public override string Title => "Unlock";
    public override string Description => "Remove passwords and restrictions";

    public override async Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct) =>
        await Task.Run(() => UnlockTool.Unlock(input, output), ct);
}

public sealed record OcrStep : WorkflowStep
{
    public string? Language { get; init; }
    public override string Title => "OCR";
    public override string Description => Language ?? "Windows languages";

    public override async Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct) =>
        await OcrTool.RunAsync(new OcrOptions { Input = input, OutputPath = output, Language = Language }, progress, ct);
}

public sealed record PdfAStep : WorkflowStep
{
    public PdfAConformance Conformance { get; init; } = PdfAConformance.PdfA2b;
    public override string Title => "PDF/A";
    public override string Description => Conformance.ToString().Replace("PdfA", "PDF/A-");

    public override async Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct) =>
        await PdfToPdfATool.RunAsync(new PdfToPdfAOptions { Input = input, OutputPath = output, Conformance = Conformance }, progress, ct);
}

public sealed record RepairStep : WorkflowStep
{
    public override string Title => "Repair";
    public override string Description => "Rebuild damaged structure";

    public override async Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var r = await RepairTool.RunAsync(new RepairOptions { Input = input, OutputPath = output }, progress, ct);
        if (!r.OutputFiles[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) throw new UserFacingException(r.Summary ?? "The file is not a PDF.");
    }
}

public sealed record RemovePagesStep : WorkflowStep
{
    public string Pages { get; init; } = "1";
    public override string Title => "Remove pages";
    public override string Description => $"Pages {Pages}";

    public override async Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct) =>
        await RemovePagesTool.RunAsync(new RemovePagesOptions { Input = input, Pages = Pages, OutputPath = output }, progress, ct);
}

public sealed record ExtractPagesStep : WorkflowStep
{
    public string Pages { get; init; } = "1";
    public override string Title => "Extract pages";
    public override string Description => $"Keep pages {Pages}";

    public override async Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var dir = TempDir();
        var r = await SplitTool.RunAsync(new SplitOptions { Input = input, OutputDirectory = dir, Mode = SplitMode.SelectPages, SelectedPages = Pages, MergeIntoOne = true }, progress, ct);
        await MoveSingle(r, output);
    }
}

public sealed record CropStep : WorkflowStep
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Right { get; init; }
    public double Bottom { get; init; }

    /// <summary>When true, ignore the margins and trim each page to its content.</summary>
    public bool AutoTrim { get; init; } = true;

    public override string Title => "Crop";
    public override string Description => AutoTrim ? "Trim to content" : $"Margins {Left:P0} {Top:P0} {Right:P0} {Bottom:P0}";

    public override async Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        Dictionary<int, Rect>? areas = null;
        if (AutoTrim)
        {
            using var renderer = await Rendering.PdfRenderer.OpenAsync(input.Path, input.Password, ct);
            areas = [];
            for (var i = 0; i < renderer.PageCount; i++) areas[i] = await CropTool.DetectContentAsync(renderer, i, ct: ct);
        }
        await CropTool.RunAsync(new CropOptions
        {
            Input = input, OutputPath = output,
            Area = new Rect(Left, Top, Math.Max(0.05, 1 - Left - Right), Math.Max(0.05, 1 - Top - Bottom)),
            PageAreas = areas,
        }, progress, ct);
    }
}

public sealed record RedactPatternsStep : WorkflowStep
{
    public IReadOnlyList<RedactionPattern> Patterns { get; init; } = [RedactionPattern.Email, RedactionPattern.Phone, RedactionPattern.CreditCard];
    public override string Title => "Redact";
    public override string Description => string.Join(", ", Patterns.Select(RedactTool.Describe));

    public override async Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var marks = RedactTool.FindPatterns(input, Patterns, ct);
        if (marks.Count == 0)
        {
            File.Copy(input.Path, output, true);
            return;
        }
        await RedactTool.RunAsync(new RedactOptions { Input = input, OutputPath = output, Marks = marks }, progress, ct);
    }
}

public sealed record MetadataStep : WorkflowStep
{
    public string? DocumentTitle { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }
    public override string Title => "Document properties";
    public override string Description => string.Join(", ", new[] { DocumentTitle, Author, Subject }.Where(s => !string.IsNullOrEmpty(s)));

    public override Task RunAsync(InputFile input, string output, IProgress<ToolProgress>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var doc = PdfDocs.Open(input);
            if (DocumentTitle != null) doc.Info.Title = DocumentTitle;
            if (Author != null) doc.Info.Author = Author;
            if (Subject != null) doc.Info.Subject = Subject;
            if (Keywords != null) doc.Info.Keywords = Keywords;
            PdfDocs.Save(doc, output);
        }, ct);
}

/// <summary>A named, reusable chain of steps.</summary>
public sealed record Workflow
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>Merge every input file into one PDF before the first step.</summary>
    public bool MergeInputsFirst { get; init; }

    public IReadOnlyList<WorkflowStep> Steps { get; init; } = [];
    public bool IsPreset { get; init; }
}

/// <summary>Saves workflows as JSON in the user's application data folder.</summary>
public sealed class WorkflowStore(string path)
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PageSmith", "workflows.json");

    public IReadOnlyList<Workflow> Load()
    {
        var saved = new List<Workflow>();
        try
        {
            if (File.Exists(path)) saved = JsonSerializer.Deserialize<List<Workflow>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".broken", true);
        }
        return Presets.Concat(saved.Where(w => !w.IsPreset)).ToList();
    }

    public void Save(IEnumerable<Workflow> workflows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(workflows.Where(w => !w.IsPreset).ToList(), Json));
    }

    public static IReadOnlyList<Workflow> Presets { get; } =
    [
        new()
        {
            Id = "preset-archive", Name = "Archive scans", IsPreset = true,
            Description = "Repair, make searchable with OCR, compress and convert to PDF/A-2b.",
            Steps = [new RepairStep(), new OcrStep(), new CompressStep(), new PdfAStep()],
        },
        new()
        {
            Id = "preset-share", Name = "Prepare for sharing", IsPreset = true,
            Description = "Compress, stamp \"CONFIDENTIAL\" and remove personal data like emails and phone numbers.",
            Steps = [new CompressStep(), new WatermarkStep { Opacity = 0.25 }, new RedactPatternsStep()],
        },
        new()
        {
            Id = "preset-print", Name = "Print-ready booklet", IsPreset = true, MergeInputsFirst = true,
            Description = "Merge the files, add \"Page n of p\" numbers on facing pages and trim margins.",
            Steps = [new PageNumbersStep { Template = "Page {n} of {p}", Position = PagePosition.BottomRight }, new CropStep()],
        },
    ];
}

/// <summary>Runs a workflow over files, chaining each step's output into the next.</summary>
public static class WorkflowRunner
{
    public static async Task<ToolResult> RunAsync(Workflow workflow, IReadOnlyList<InputFile> files, string outputDirectory, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        if (workflow.Steps.Count == 0) throw new UserFacingException("This workflow has no steps.");
        if (files.Count == 0) throw new UserFacingException("Add at least one PDF.");
        var scope = new ProgressScope(progress);
        var result = new ToolResult();
        foreach (var f in files) result.InputBytes += new FileInfo(f.Path).Length;
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pagesmith_run_" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var inputs = files.ToList();
            if (workflow.MergeInputsFirst && files.Count > 1)
            {
                var merged = Path.Combine(work, "merged.pdf");
                await MergeTool.RunAsync(new MergeOptions { Items = files.Select(f => new MergeItem(f)).ToList(), OutputPath = merged }, scope.Slice(0, 0.05).AsProgress(), ct);
                inputs = [new InputFile(merged)];
            }

            var suffix = OutputPaths.Sanitize(workflow.Name.ToLowerInvariant().Replace(' ', '-'));
            for (var f = 0; f < inputs.Count; f++)
            {
                var fileScope = scope.Slice(0.05 + 0.95 * f / inputs.Count, 0.05 + 0.95 * (f + 1) / inputs.Count);
                var current = inputs[f];
                for (var s = 0; s < workflow.Steps.Count; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    var step = workflow.Steps[s];
                    var stepScope = fileScope.Slice((double)s / workflow.Steps.Count, (double)(s + 1) / workflow.Steps.Count);
                    stepScope.Report(0, $"{Path.GetFileName(files[Math.Min(f, files.Count - 1)].Path)}: {step.Title} ({s + 1}/{workflow.Steps.Count})");
                    var next = Path.Combine(work, $"f{f}_s{s}.pdf");
                    try
                    {
                        await step.RunAsync(current, next, stepScope.AsProgress(), ct);
                    }
                    catch (UserFacingException ex)
                    {
                        throw new UserFacingException($"Step {s + 1} ({step.Title}) failed: {ex.Message}", ex);
                    }
                    current = new InputFile(next);
                }
                var name = workflow.MergeInputsFirst && files.Count > 1 ? "merged" : Path.GetFileNameWithoutExtension(files[f].Path);
                var final = OutputPaths.Unique(outputDirectory, $"{name}_{suffix}.pdf");
                File.Copy(current.Path, final);
                result.AddOutput(final);
            }
            result.Summary = $"Ran \"{workflow.Name}\" ({workflow.Steps.Count} step{(workflow.Steps.Count == 1 ? "" : "s")}) on {files.Count} file{(files.Count == 1 ? "" : "s")}.";
            return result;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* temp */ }
        }
    }
}
