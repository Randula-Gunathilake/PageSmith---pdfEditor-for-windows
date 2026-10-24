namespace PageSmith.Core.Common;

/// <summary>Builds output file names that never overwrite existing files.</summary>
public static class OutputPaths
{
    /// <summary>
    /// Path for the output of <paramref name="inputPath"/>, e.g. <c>report_compressed.pdf</c>,
    /// in <paramref name="outputDir"/> or next to the input when no directory is given.
    /// </summary>
    public static string ForInput(string inputPath, string suffix, string extension, string? outputDir = null)
    {
        var dir = outputDir ?? Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? Environment.CurrentDirectory;
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var name = string.IsNullOrEmpty(suffix) ? stem : $"{stem}_{suffix}";
        return Unique(dir, name + NormalizeExtension(extension));
    }

    /// <summary>Returns <paramref name="fileName"/> inside <paramref name="directory"/>, adding " (2)", " (3)"… if taken.</summary>
    public static string Unique(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        fileName = Sanitize(fileName);
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
    }

    /// <summary>Creates a new, uniquely named folder for tools that produce many files.</summary>
    public static string UniqueDirectory(string parent, string name)
    {
        var path = Unique(parent, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public static string Sanitize(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(fileName.Length);
        foreach (var c in fileName) sb.Append(invalid.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "output" : s;
    }

    private static string NormalizeExtension(string ext) => ext.StartsWith('.') ? ext : "." + ext;
}
