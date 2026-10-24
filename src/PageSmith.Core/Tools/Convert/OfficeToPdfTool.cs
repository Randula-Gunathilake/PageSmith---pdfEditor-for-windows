using PageSmith.Core.Office;
using PageSmith.Core.Office.BuiltIn;

namespace PageSmith.Core.Tools.Convert;

public enum ConversionEngine
{
    /// <summary>Microsoft Office if installed, then LibreOffice, then the built-in renderer.</summary>
    Auto,
    MicrosoftOffice,
    LibreOffice,
    BuiltIn,
}

public sealed record OfficeToPdfOptions
{
    public required IReadOnlyList<InputFile> Files { get; init; }

    /// <summary>Folder for the PDFs (one per input).</summary>
    public required string OutputDirectory { get; init; }

    public ConversionEngine Engine { get; init; } = ConversionEngine.Auto;
}

/// <summary>Word, Excel, PowerPoint, OpenDocument, RTF, CSV and text files to PDF.</summary>
public static class OfficeToPdfTool
{
    public static readonly string[] WordExtensions = [".doc", ".docx", ".docm", ".dot", ".dotx", ".odt", ".ott", ".rtf", ".txt", ".wpd"];
    public static readonly string[] ExcelExtensions = [".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".ods", ".ots", ".csv"];
    public static readonly string[] PowerPointExtensions = [".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".pot", ".potx", ".odp"];

    public static async Task<ToolResult> RunAsync(OfficeToPdfOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        if (options.Files.Count == 0) throw new UserFacingException("Add at least one document.");
        var scope = new ProgressScope(progress);
        var result = new ToolResult();
        var engines = new HashSet<string>();
        for (var i = 0; i < options.Files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = options.Files[i];
            scope.Report(i, options.Files.Count, $"Converting {file.Name}");
            result.InputBytes += new FileInfo(file.Path).Length;
            var output = OutputPaths.Unique(options.OutputDirectory, Path.GetFileNameWithoutExtension(file.Path) + ".pdf");
            engines.Add(await ConvertAsync(file, output, options.Engine, ct).ConfigureAwait(false));
            result.AddOutput(output);
        }
        result.Summary = $"Converted {result.OutputFiles.Count} document{(result.OutputFiles.Count == 1 ? "" : "s")} to PDF using {string.Join(" and ", engines)}.";
        scope.Report(1);
        return result;
    }

    /// <summary>Converts one file; returns the name of the engine that did the work.</summary>
    public static async Task<string> ConvertAsync(InputFile file, string output, ConversionEngine engine, CancellationToken ct = default)
    {
        var app = OfficeDetection.AppFor(file.Path) ?? throw new UserFacingException($"\"{file.Name}\" is not an Office document PageSmith can convert.");
        var errors = new List<string>();

        if (engine is ConversionEngine.Auto or ConversionEngine.MicrosoftOffice && OfficeDetection.IsMicrosoftOfficeAppInstalled(app))
        {
            try
            {
                await MicrosoftOffice.ToPdfAsync(file.Path, output, app, file.Password, ct).ConfigureAwait(false);
                if (File.Exists(output)) return $"Microsoft {app}";
            }
            catch (PasswordRequiredException)
            {
                throw;
            }
            catch (Exception ex) when (engine == ConversionEngine.Auto)
            {
                errors.Add($"Microsoft {app}: {ex.Message}");
            }
        }
        else if (engine == ConversionEngine.MicrosoftOffice)
        {
            throw new UserFacingException($"Microsoft {app} is not installed. Choose another conversion engine in Settings.");
        }

        if (engine is ConversionEngine.Auto or ConversionEngine.LibreOffice && LibreOffice.IsInstalled)
        {
            try
            {
                await LibreOffice.ToPdfAsync(file.Path, output, ct).ConfigureAwait(false);
                return "LibreOffice";
            }
            catch (Exception ex) when (engine == ConversionEngine.Auto)
            {
                errors.Add($"LibreOffice: {ex.Message}");
            }
        }
        else if (engine == ConversionEngine.LibreOffice)
        {
            throw new UserFacingException("LibreOffice is not installed. Choose another conversion engine in Settings.");
        }

        var ext = Path.GetExtension(file.Path).ToLowerInvariant();
        try
        {
            await Task.Run(() =>
            {
                switch (ext)
                {
                    case ".docx" or ".docm" or ".dotx":
                        DocxRenderer.Render(file.Path, output);
                        break;
                    case ".xlsx" or ".xlsm" or ".xltx" or ".csv":
                        XlsxRenderer.Render(file.Path, output);
                        break;
                    case ".pptx" or ".pptm" or ".potx" or ".ppsx":
                        PptxRenderer.Render(file.Path, output);
                        break;
                    case ".txt":
                        TextRenderer.Render(file.Path, output);
                        break;
                    default:
                        throw new UserFacingException(
                            $"\"{file.Name}\" ({ext}) needs Microsoft Office or LibreOffice to convert. " +
                            "The built-in converter handles .docx, .xlsx, .pptx, .csv and .txt files." +
                            (errors.Count > 0 ? " " + string.Join(" ", errors) : ""));
                }
            }, ct).ConfigureAwait(false);
            return "the built-in converter";
        }
        catch (UserFacingException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new UserFacingException($"\"{file.Name}\" could not be converted: {ex.Message}", ex);
        }
    }
}
