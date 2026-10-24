using System.Reflection;
using System.Windows.Controls;
using PageSmith.App.Infrastructure;

namespace PageSmith.App.Pages;

public partial class AboutPage : Page
{
    public sealed record CatalogRow(string Tool, string Category, string ILovePdf, string PageSmith);

    public AboutPage()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3)} · .NET {Environment.Version}";
        var rows = ToolRegistry.All.Select(t => new CatalogRow(
            t.Title,
            ToolRegistry.CategoryTitle(t.Category),
            t.PremiumNote != null ? $"{t.ILovePdfTier} · {t.PremiumNote}" : t.ILovePdfTier,
            t.InProgress ? "Coming in the next update"
            : t.OnlineNote != null ? $"Included, no limits · {t.OnlineNote}" : "Included, no limits · works offline")).ToList();
        rows.Add(new CatalogRow("Workflows", "Workflows", "Premium only", "Included: presets plus your own chains of tools"));
        rows.Add(new CatalogRow("Batch processing", "All tools", "Free: 1–25 files per task · Premium: more", "As many files as you like"));
        rows.Add(new CatalogRow("File size", "All tools", "Free: 15–400 MB per task · Premium: 4 GB", "Limited only by your PC"));
        rows.Add(new CatalogRow("Desktop app", "Platform", "Premium", "This app"));
        Catalog.ItemsSource = rows;
    }
}
