using System.IO;
using System.Windows;
using System.Windows.Controls;
using PageSmith.App.Infrastructure;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;

namespace PageSmith.App.Pages;

/// <summary>All tools as cards with category filters, plus a drop area that suggests tools for files.</summary>
public partial class HomePage : Page
{
    private string? _filter;
    private string[] _dropped = [];

    public HomePage()
    {
        InitializeComponent();
        BuildFilters();
        ApplyFilter(null);
    }

    private void BuildFilters()
    {
        AddFilter("All tools", null, true);
        foreach (var c in Enum.GetValues<ToolCategory>()) AddFilter(ToolRegistry.CategoryTitle(c), c.ToString(), false);
        var workflows = new RadioButton { Content = "Workflows", Style = (Style)FindResource("Segment"), GroupName = "HomeFilter" };
        workflows.Click += (_, _) => AppServices.NavigateTo(typeof(WorkflowsPage));
        Filters.Children.Add(workflows);
    }

    private void AddFilter(string title, string? key, bool isChecked)
    {
        var button = new RadioButton
        {
            Content = title,
            Style = (Style)FindResource("Segment"),
            GroupName = "HomeFilter",
            IsChecked = isChecked,
        };
        button.Checked += (_, _) => ApplyFilter(key);
        Filters.Children.Add(button);
    }

    private void ApplyFilter(string? key)
    {
        _filter = key;
        Cards.ItemsSource = key == null
            ? ToolRegistry.All
            : ToolRegistry.All.Where(t => t.Category.ToString() == key).ToList();
    }

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id) AppServices.OpenTool(id, _dropped.Length > 0 ? Accepted(id) : null);
    }

    private IEnumerable<string>? Accepted(string toolId)
    {
        var tool = ToolRegistry.Get(toolId);
        var files = _dropped.Where(tool.Accepts).ToList();
        return files.Count > 0 ? files : null;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) ShowSuggestions(files);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Choose files to work with",
            Filter = "Documents and images|*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.odt;*.ods;*.odp;*.rtf;*.txt;*.csv;*.html;*.htm;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ShowSuggestions(dialog.FileNames);
    }

    /// <summary>Lists the tools that accept the given files; clicking one opens it with them.</summary>
    private void ShowSuggestions(string[] files)
    {
        _dropped = files.Where(File.Exists).ToArray();
        Suggestions.Children.Clear();
        if (_dropped.Length == 0) return;
        var tools = ToolRegistry.All.Where(t => _dropped.Any(t.Accepts)).ToList();
        DropTitle.Text = _dropped.Length == 1 ? $"What do you want to do with \"{Path.GetFileName(_dropped[0])}\"?" : $"What do you want to do with these {_dropped.Length} files?";
        DropHint.Text = tools.Count == 0 ? "None of the tools can open these files." : "Pick a tool:";
        foreach (var tool in tools)
        {
            var chip = new Wpf.Ui.Controls.Button
            {
                Content = tool.Title,
                Icon = new SymbolIcon(tool.Icon),
                Margin = new Thickness(0, 0, 6, 6),
                Tag = tool.Id,
            };
            chip.Click += OnToolClick;
            Suggestions.Children.Add(chip);
        }
    }
}
