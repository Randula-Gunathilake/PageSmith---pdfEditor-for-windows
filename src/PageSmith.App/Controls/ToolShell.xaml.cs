using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PageSmith.App.Tools;

namespace PageSmith.App.Controls;

/// <summary>Common layout of every tool: header, messages, workspace, options and result.</summary>
public partial class ToolShell : UserControl
{
    public ToolShell()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BuildWorkspace();
    }

    private ToolController? Controller => DataContext as ToolController;

    private void BuildWorkspace()
    {
        if (Controller is not { } c) return;
        WorkspaceHost.Content = c.Workspace switch
        {
            WorkspaceKind.Files => new FileList { DataContext = c },
            WorkspaceKind.Custom => c.CreateWorkspace(),
            _ => c.CreateWorkspace() ?? new FileList { DataContext = c },
        };
        if (c is INotifyPropertyChanged npc)
        {
            npc.PropertyChanged -= OnControllerChanged;
            npc.PropertyChanged += OnControllerChanged;
        }
    }

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolController.Workspace)) BuildWorkspace();
    }
}
