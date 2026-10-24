using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using PageSmith.App.Controls;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Common;
using PageSmith.Core.Pdf;
using PageSmith.Core.Tools.Security;
using PageSmith.Core.Workflows;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PageSmith.App.Pages;

/// <summary>Lists, edits and runs workflows.</summary>
public partial class WorkflowsPage : Page
{
    private static readonly (Type Type, string Title, SymbolRegular Icon)[] StepTypes =
    [
        (typeof(CompressStep), "Compress", SymbolRegular.ArrowMinimize24),
        (typeof(RotateStep), "Rotate", SymbolRegular.ArrowRotateClockwise24),
        (typeof(PageNumbersStep), "Page numbers", SymbolRegular.DocumentPageNumber24),
        (typeof(WatermarkStep), "Watermark", SymbolRegular.Drop24),
        (typeof(ProtectStep), "Protect", SymbolRegular.LockClosed24),
        (typeof(UnlockStep), "Unlock", SymbolRegular.LockOpen24),
        (typeof(OcrStep), "OCR", SymbolRegular.ScanText24),
        (typeof(PdfAStep), "PDF/A", SymbolRegular.Archive24),
        (typeof(RepairStep), "Repair", SymbolRegular.WrenchScrewdriver24),
        (typeof(RemovePagesStep), "Remove pages", SymbolRegular.DocumentDismiss24),
        (typeof(ExtractPagesStep), "Extract pages", SymbolRegular.DocumentArrowRight24),
        (typeof(CropStep), "Crop", SymbolRegular.Crop24),
        (typeof(RedactPatternsStep), "Redact personal data", SymbolRegular.EyeOff24),
        (typeof(MetadataStep), "Document properties", SymbolRegular.Info24),
    ];

    private List<Workflow> _workflows = [];
    private Workflow? _editingOriginal;
    private List<WorkflowStep> _editingSteps = [];

    public WorkflowsPage()
    {
        InitializeComponent();
        foreach (var (type, title, icon) in StepTypes)
        {
            var item = new MenuItem { Header = title, Icon = new SymbolIcon(icon) };
            item.Click += (_, _) =>
            {
                _editingSteps.Add((WorkflowStep)Activator.CreateInstance(type)!);
                RenderSteps();
            };
            AddStepMenu.Items.Add(item);
        }
        Reload();
    }

    private void Reload()
    {
        _workflows = AppServices.Workflows.Load().ToList();
        List.ItemsSource = null;
        List.ItemsSource = _workflows;
    }

    private static Workflow Clone(Workflow w) =>
        JsonSerializer.Deserialize<Workflow>(JsonSerializer.Serialize(w, WorkflowStore.Json), WorkflowStore.Json)!;

    private void OnNew(object sender, RoutedEventArgs e) => BeginEdit(new Workflow { Name = "My workflow", Steps = [new CompressStep()] }, null);

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Workflow w) return;
        var copy = Clone(w);
        if (w.IsPreset) copy = copy with { Id = Guid.NewGuid().ToString("N"), Name = w.Name + " (copy)", IsPreset = false };
        BeginEdit(copy, w.IsPreset ? null : w);
    }

    private void BeginEdit(Workflow w, Workflow? original)
    {
        _editingOriginal = original;
        NameBox.Text = w.Name;
        DescriptionBox.Text = w.Description ?? "";
        MergeFirstBox.IsChecked = w.MergeInputsFirst;
        _editingSteps = w.Steps.ToList();
        Tag = w.Id;
        RenderSteps();
        EditorColumn.Width = new GridLength(440);
        Editor.Visibility = Visibility.Visible;
    }

    private void OnCancelEdit(object sender, RoutedEventArgs e) => CloseEditor();

    private void CloseEditor()
    {
        Editor.Visibility = Visibility.Collapsed;
        EditorColumn.Width = new GridLength(0);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_editingSteps.Count == 0)
        {
            AppServices.Notify("Add a step", "A workflow needs at least one step.", ControlAppearance.Caution);
            return;
        }
        var workflow = new Workflow
        {
            Id = Tag as string ?? Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "My workflow" : NameBox.Text.Trim(),
            Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? string.Join(" → ", _editingSteps.Select(s => s.Title)) : DescriptionBox.Text.Trim(),
            MergeInputsFirst = MergeFirstBox.IsChecked == true,
            Steps = _editingSteps.ToList(),
        };
        var custom = _workflows.Where(w => !w.IsPreset && w.Id != workflow.Id).ToList();
        var index = _editingOriginal != null ? _workflows.Where(w => !w.IsPreset).ToList().FindIndex(w => w.Id == workflow.Id) : -1;
        if (index >= 0) custom.Insert(Math.Min(index, custom.Count), workflow);
        else custom.Add(workflow);
        AppServices.Workflows.Save(custom);
        CloseEditor();
        Reload();
        AppServices.Notify("Workflow saved", workflow.Name, ControlAppearance.Success, SymbolRegular.Checkmark24);
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Workflow w || w.IsPreset) return;
        if (!await AppServices.ConfirmAsync("Delete workflow?", $"\"{w.Name}\" will be deleted.", "Delete")) return;
        AppServices.Workflows.Save(_workflows.Where(x => !x.IsPreset && x.Id != w.Id));
        Reload();
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Workflow w) return;
        var files = AppServices.PickFiles(ToolRegistry.Get("merge") with { Title = w.Name });
        if (files.Length == 0) return;

        var progressBar = new ProgressBar { Height = 4, Maximum = 100, Margin = new Thickness(0, 12, 0, 0), Width = 380 };
        var status = new TextBlock { Text = "Starting…", TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
        var panel = new StackPanel();
        panel.Children.Add(status);
        panel.Children.Add(progressBar);
        var cts = new CancellationTokenSource();
        var dialog = new ContentDialog { Title = $"Running \"{w.Name}\"", Content = panel, CloseButtonText = "Cancel" };
        dialog.ButtonClicked += (_, _) => cts.Cancel();
        _ = AppServices.ShowDialogAsync(dialog);
        var progress = new Progress<ToolProgress>(p =>
        {
            progressBar.Value = p.Fraction * 100;
            if (p.Status != null) status.Text = p.Status;
        });
        try
        {
            var inputs = new List<InputFile>();
            foreach (var f in files)
            {
                string? password = null;
                if (PdfDocs.RequiresPassword(f))
                {
                    dialog.Hide(ContentDialogResult.None);
                    password = await AppServices.AskPasswordAsync(System.IO.Path.GetFileName(f), false);
                    if (password == null) return;
                    _ = AppServices.ShowDialogAsync(dialog);
                }
                inputs.Add(new InputFile(f, password));
            }
            var result = await WorkflowRunner.RunAsync(w, inputs, AppServices.OutputDirectoryFor(files[0]), progress, cts.Token);
            dialog.Hide(ContentDialogResult.None);
            if (await AppServices.ConfirmAsync("Workflow finished", result.Summary + Environment.NewLine + string.Join(Environment.NewLine, result.Warnings), "Show files", "Close"))
                AppServices.ShowInFolder(result.OutputFiles[0]);
        }
        catch (OperationCanceledException)
        {
            dialog.Hide(ContentDialogResult.None);
        }
        catch (Exception ex) when (ex is UserFacingException or PasswordRequiredException)
        {
            dialog.Hide(ContentDialogResult.None);
            await AppServices.ConfirmAsync("Workflow stopped", ex.Message, "OK", "Close");
        }
        catch (Exception ex)
        {
            dialog.Hide(ContentDialogResult.None);
            AppServices.LogError(ex);
            await AppServices.ConfirmAsync("Workflow stopped", ex.Message, "OK", "Close");
        }
    }

    // ------------------------------------------------------------ step editor

    private void RenderSteps()
    {
        StepsPanel.Children.Clear();
        for (var i = 0; i < _editingSteps.Count; i++)
        {
            var step = _editingSteps[i];
            var index = i;
            var expander = new CardExpander
            {
                Margin = new Thickness(0, 0, 0, 6),
                Header = $"{i + 1}. {step.Title}",
                Icon = new SymbolIcon(StepTypes.FirstOrDefault(t => t.Type == step.GetType()).Icon),
                Content = BuildStepEditor(step),
            };
            var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            tools.Children.Add(SmallButton(SymbolRegular.ArrowUp24, "Move up", () => Move(index, -1)));
            tools.Children.Add(SmallButton(SymbolRegular.ArrowDown24, "Move down", () => Move(index, 1)));
            tools.Children.Add(SmallButton(SymbolRegular.Delete24, "Remove step", () =>
            {
                _editingSteps.RemoveAt(index);
                RenderSteps();
            }));
            ((StackPanel)expander.Content).Children.Add(tools);
            StepsPanel.Children.Add(expander);
        }
    }

    private void Move(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= _editingSteps.Count) return;
        (_editingSteps[index], _editingSteps[target]) = (_editingSteps[target], _editingSteps[index]);
        RenderSteps();
    }

    private static Wpf.Ui.Controls.Button SmallButton(SymbolRegular icon, string tip, Action action)
    {
        var b = new Wpf.Ui.Controls.Button { Icon = new SymbolIcon(icon), ToolTip = tip, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(6) };
        b.Click += (_, _) => action();
        return b;
    }

    /// <summary>Builds controls for a step's settings from its properties.</summary>
    private static StackPanel BuildStepEditor(object target)
    {
        var panel = new StackPanel();
        foreach (var prop in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.GetIndexParameters().Length == 0))
        {
            var label = Label(prop.Name);
            var value = prop.GetValue(target);
            var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            FrameworkElement? editor = null;
            if (type == typeof(bool))
            {
                var box = new CheckBox { Content = label, IsChecked = (bool?)value == true, Margin = new Thickness(0, 6, 0, 0) };
                box.Click += (_, _) => prop.SetValue(target, box.IsChecked == true);
                panel.Children.Add(box);
                continue;
            }
            if (type.IsEnum)
            {
                var combo = new ComboBox { ItemsSource = Enum.GetValues(type), SelectedItem = value };
                combo.SelectionChanged += (_, _) => prop.SetValue(target, combo.SelectedItem);
                editor = combo;
            }
            else if (type == typeof(double) || type == typeof(int))
            {
                var number = new NumberBox { Value = Convert.ToDouble(value ?? 0), MaxDecimalPlaces = type == typeof(int) ? 0 : 2 };
                number.ValueChanged += (_, _) => prop.SetValue(target, type == typeof(int) ? (object)(int)(number.Value ?? 0) : number.Value ?? 0);
                editor = number;
            }
            else if (type == typeof(string) && prop.Name.StartsWith("Protected", StringComparison.Ordinal))
            {
                var box = new Wpf.Ui.Controls.PasswordBox { Password = SecretStore.Unprotect(value as string), PlaceholderText = "Leave empty for none" };
                box.PasswordChanged += (_, _) => prop.SetValue(target, SecretStore.Protect(box.Password));
                label = label.Replace("Protected ", "");
                editor = box;
            }
            else if (type == typeof(string) && prop.Name == "ImagePath")
            {
                var row = new DockPanel();
                var text = new Wpf.Ui.Controls.TextBox { Text = value as string ?? "", PlaceholderText = "No image (use text)" };
                var browse = new Wpf.Ui.Controls.Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0) };
                browse.Click += (_, _) =>
                {
                    var path = AppServices.PickFile("Watermark image", "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif");
                    if (path != null) text.Text = path;
                };
                text.TextChanged += (_, _) => prop.SetValue(target, string.IsNullOrWhiteSpace(text.Text) ? null : text.Text);
                DockPanel.SetDock(browse, Dock.Right);
                row.Children.Add(browse);
                row.Children.Add(text);
                editor = row;
            }
            else if (type == typeof(string))
            {
                var box = new Wpf.Ui.Controls.TextBox { Text = value as string ?? "" };
                box.TextChanged += (_, _) => prop.SetValue(target, box.Text);
                editor = box;
            }
            else if (type == typeof(TextStyle) && value is TextStyle style)
            {
                var sub = BuildStepEditor(style);
                editor = sub;
            }
            else if (prop.PropertyType == typeof(IReadOnlyList<RedactionPattern>))
            {
                var selected = ((IReadOnlyList<RedactionPattern>?)value ?? []).ToHashSet();
                var list = new StackPanel();
                foreach (var pattern in Enum.GetValues<RedactionPattern>())
                {
                    var box = new CheckBox { Content = RedactTool.Describe(pattern), IsChecked = selected.Contains(pattern) };
                    box.Click += (_, _) =>
                    {
                        if (box.IsChecked == true) selected.Add(pattern);
                        else selected.Remove(pattern);
                        prop.SetValue(target, selected.ToList());
                    };
                    list.Children.Add(box);
                }
                editor = list;
            }
            if (editor == null) continue;
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4), FontSize = 12, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextFillColorSecondaryBrush") });
            panel.Children.Add(editor);
        }
        if (panel.Children.Count == 0)
            panel.Children.Add(new TextBlock { Text = "No settings.", Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextFillColorSecondaryBrush") });
        return panel;
    }

    private static string Label(string name)
    {
        name = name.Replace("DocumentTitle", "Title");
        var spaced = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
        return char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }
}
