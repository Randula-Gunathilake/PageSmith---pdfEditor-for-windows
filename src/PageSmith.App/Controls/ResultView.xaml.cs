using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PageSmith.App.Infrastructure;
using PageSmith.App.Tools;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace PageSmith.App.Controls;

/// <summary>Shows what a tool produced, with open/show buttons and "Continue with…" chaining.</summary>
public partial class ResultView : UserControl
{
    public ResultView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnChanged;
            if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnChanged;
            BuildContinueMenu();
        };
    }

    private ToolController? Controller => DataContext as ToolController;

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolController.Result)) BuildContinueMenu();
    }

    private IReadOnlyList<string> PdfOutputs =>
        Controller?.Result?.OutputFiles.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];

    private void BuildContinueMenu()
    {
        ContinueMenu.Items.Clear();
        var outputs = PdfOutputs;
        ContinueButton.Visibility = outputs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenFirstButton.Visibility = Controller?.Result?.OutputFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (outputs.Count == 0 || Controller == null) return;
        foreach (var tool in ToolRegistry.PdfTools.Where(t => t.Id != Controller.Descriptor.Id))
        {
            var item = new MenuItem { Header = tool.Title, Icon = new SymbolIcon(tool.Icon), Tag = tool.Id };
            item.Click += (_, _) => AppServices.OpenTool((string)item.Tag, tool.MultipleFiles ? outputs : outputs.Take(1));
            ContinueMenu.Items.Add(item);
        }
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) Open(path);
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) AppServices.ShowInFolder(path);
    }

    private void OnOpenFirst(object sender, RoutedEventArgs e)
    {
        var outputs = Controller?.Result?.OutputFiles;
        if (outputs is not { Count: > 0 }) return;
        // Many outputs (split, PDF to JPG) live in one folder: open the folder instead of a single file.
        if (outputs.Count > 3) AppServices.ShowInFolder(Path.GetDirectoryName(outputs[0])!);
        else Open(outputs[0]);
    }

    private static void Open(string path) => AppServices.OpenFile(path);

    private void OnCopyText(object sender, RoutedEventArgs e)
    {
        if (Controller?.Result?.Text is { } text)
        {
            Clipboard.SetText(text);
            AppServices.Notify("Copied", "The text is on the clipboard.", ControlAppearance.Success, SymbolRegular.Copy24);
        }
    }
}
