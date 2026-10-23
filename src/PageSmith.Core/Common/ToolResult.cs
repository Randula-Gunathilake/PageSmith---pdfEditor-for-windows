namespace PageSmith.Core.Common;

/// <summary>A file handed to a tool, with the password needed to open it when it is encrypted.</summary>
public sealed record InputFile(string Path, string? Password = null)
{
    public static implicit operator InputFile(string path) => new(path);
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Progress of a running tool: a fraction from 0 to 1 and an optional status line.</summary>
public readonly record struct ToolProgress(double Fraction, string? Status = null);

/// <summary>The outcome of running a tool.</summary>
public sealed class ToolResult
{
    public List<string> OutputFiles { get; } = [];

    /// <summary>Notes worth showing the user, such as pages that could not be processed.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>One-line description of what happened, shown on the result screen.</summary>
    public string? Summary { get; set; }

    /// <summary>Text produced by tools whose main result is text (summaries, comparisons).</summary>
    public string? Text { get; set; }

    public long InputBytes { get; set; }
    public long OutputBytes { get; set; }

    /// <summary>Tool-specific structured result (for example a comparison report).</summary>
    public object? Data { get; set; }

    public ToolResult AddOutput(string path)
    {
        OutputFiles.Add(path);
        if (File.Exists(path)) OutputBytes += new FileInfo(path).Length;
        return this;
    }
}

/// <summary>Reports progress for a loop of known length, optionally inside a slice of a larger operation.</summary>
public sealed class ProgressScope(IProgress<ToolProgress>? progress, double from = 0, double to = 1)
{
    public void Report(int done, int total, string? status = null)
    {
        if (progress is null) return;
        var f = total <= 0 ? 1 : Math.Clamp((double)done / total, 0, 1);
        progress.Report(new ToolProgress(from + (to - from) * f, status));
    }

    public void Report(double fraction, string? status = null) =>
        progress?.Report(new ToolProgress(from + (to - from) * Math.Clamp(fraction, 0, 1), status));

    /// <summary>A scope covering the part [a, b] of this scope.</summary>
    public ProgressScope Slice(double a, double b) => new(progress, from + (to - from) * a, from + (to - from) * b);

    public IProgress<ToolProgress>? Inner => progress;
}

/// <summary>Raised when a document cannot be opened without a (correct) password.</summary>
public sealed class PasswordRequiredException(string path, bool passwordWasWrong)
    : Exception(passwordWasWrong
        ? $"The password for \"{System.IO.Path.GetFileName(path)}\" is incorrect."
        : $"\"{System.IO.Path.GetFileName(path)}\" is password protected.")
{
    public string FilePath { get; } = path;
    public bool PasswordWasWrong { get; } = passwordWasWrong;
}

/// <summary>An error with a message that can be shown to the user as is.</summary>
public class UserFacingException(string message, Exception? inner = null) : Exception(message, inner);
