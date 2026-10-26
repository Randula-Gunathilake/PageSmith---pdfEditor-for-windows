using System.Windows.Controls;
using System.Windows.Input;
using PageSmith.App.Tools;

namespace PageSmith.App.Controls;

/// <summary>HTML to PDF: a web address box with a preview, or a dropped HTML file.</summary>
public partial class HtmlWorkspace : UserControl
{
    public HtmlWorkspace() => InitializeComponent();

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is HtmlToPdfController c && c.ShowPreviewCommand.CanExecute(null))
            c.ShowPreviewCommand.Execute(null);
    }
}
