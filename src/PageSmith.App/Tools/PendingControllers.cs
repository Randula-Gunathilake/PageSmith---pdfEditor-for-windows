using PageSmith.Core.Common;

namespace PageSmith.App.Tools;

// Temporary controllers, replaced one by one by their interactive versions.
// The engines behind these tools are in PageSmith.Core and covered by tests; what is missing is the
// interactive workspace (page editor, signature pad, scanner, web capture) that drives them.
public abstract class PendingController(string toolId) : ToolController(toolId)
{
    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct) =>
        throw new UserFacingException($"{Descriptor.Title} is still being built and will arrive in the next update. See TODO.md for its status.");
}

public sealed class ScanController() : PendingController("scan");

public sealed class HtmlToPdfController() : PendingController("html-to-pdf");
