using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PageSmith.App.Infrastructure;
using PageSmith.App.Tools.Editor;
using PageSmith.Core.Common;
using PageSmith.Core.Tools.Edit;
using Wpf.Ui.Controls;

namespace PageSmith.App.Tools;

/// <summary>PDF Forms: fill existing fields in place, draw new fillable fields, or detect them automatically.</summary>
public sealed partial class FormsController() : EditorController("forms")
{
    private static readonly EditorTool FillTool = new("fill", "Fill in the form", SymbolRegular.Form24, PointerAction.Interact,
        "Click a highlighted field on the page and type. Tick boxes and pick options the same way.");
    private static readonly EditorTool SelectTool = new("select", "Select fields", SymbolRegular.Cursor24, PointerAction.Select,
        "Click a new field to rename it or change its options; drag to move it, use the handles to resize.");
    private static readonly EditorTool TextFieldTool = new("text", "Text field", SymbolRegular.TextField24, PointerAction.DrawRect, "Drag on the page to draw a text field.");
    private static readonly EditorTool MultilineTool = new("multiline", "Multi-line text field", SymbolRegular.Textbox24, PointerAction.DrawRect, "Drag on the page to draw a box for several lines of text.");
    private static readonly EditorTool CheckboxTool = new("checkbox", "Checkbox", SymbolRegular.CheckboxChecked24, PointerAction.DrawRect, "Click or drag on the page to add a checkbox.");
    private static readonly EditorTool RadioTool = new("radio", "Radio buttons", SymbolRegular.RadioButton24, PointerAction.DrawRect, "Drag an area for a group of radio buttons; set the choices on the right.");
    private static readonly EditorTool DropdownTool = new("dropdown", "Dropdown", SymbolRegular.ChevronCircleDown24, PointerAction.DrawRect, "Drag on the page to draw a dropdown; set the choices on the right.");
    private static readonly EditorTool ListTool = new("list", "List", SymbolRegular.TextBulletListLtr24, PointerAction.DrawRect, "Drag on the page to draw a list box; set the choices on the right.");

    public override IReadOnlyList<EditorTool> Tools { get; } =
        [FillTool, SelectTool, TextFieldTool, MultilineTool, CheckboxTool, RadioTool, DropdownTool, ListTool];

    [ObservableProperty] public partial bool Flatten { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";

    public bool HasExistingFields => Items.OfType<FieldItem>().Any(f => f.IsExisting);

    public override bool KeepToolAfterCreate(EditorTool tool) => tool == CheckboxTool;
    protected override bool CountsAsEdit(EditorItem item) => item is FieldItem { IsExisting: false };

    protected override async Task OnDocumentOpenedAsync()
    {
        var input = Document!.ToInput();
        IReadOnlyList<FormFieldInfo> fields;
        try
        {
            fields = await Task.Run(() => FormsTool.ReadFields(input));
        }
        catch (Exception ex) when (ex is UserFacingException or InvalidOperationException)
        {
            fields = [];
        }
        foreach (var f in fields.Where(f => f.PageIndex >= 0 && f.PageIndex < Pages.Count && f.Bounds.Width > 0))
        {
            var item = new FieldItem
            {
                FieldKind = f.Kind,
                IsExisting = true,
                ReadOnly = f.ReadOnly,
                PageIndex = f.PageIndex,
                Bounds = f.Bounds,
                Name = f.Name,
                Multiline = f.Multiline,
                FixedOptions = f.Options,
                OriginalValue = f.Value,
            };
            item.Value = f.Value;
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FieldItem.Value)) UpdateSummary();
            };
            Items.Add(item);
        }
        ActiveTool = HasExistingFields ? FillTool : TextFieldTool;
        OnPropertyChanged(nameof(HasExistingFields));
        UpdateSummary();
    }

    protected override void OnItemsChanged() => UpdateSummary();

    private void UpdateSummary()
    {
        var fields = Items.OfType<FieldItem>().ToList();
        var existing = fields.Count(f => f.IsExisting);
        var changed = fields.Count(f => f.IsExisting && f.Value != f.OriginalValue);
        var added = fields.Count(f => !f.IsExisting);
        Summary = (existing, added) switch
        {
            (0, 0) => "This PDF has no form fields yet. Draw some with the field tools above the page, or let PageSmith detect them.",
            (0, _) => $"{added} new field{(added == 1 ? "" : "s")} will be added.",
            _ => $"{existing} field{(existing == 1 ? "" : "s")} in this form · {changed} changed" + (added > 0 ? $" · {added} new" : ""),
        };
    }

    public override EditorItem? CreateFromRect(EditorTool tool, int page, Rect rect)
    {
        var (kind, multiline, size) = tool.Id switch
        {
            "multiline" => (FormFieldKind.Text, true, new Size(220, 64)),
            "checkbox" => (FormFieldKind.CheckBox, false, new Size(14, 14)),
            "radio" => (FormFieldKind.RadioGroup, false, new Size(110, 54)),
            "dropdown" => (FormFieldKind.ComboBox, false, new Size(150, 20)),
            "list" => (FormFieldKind.ListBox, false, new Size(150, 60)),
            _ => (FormFieldKind.Text, false, new Size(180, 20)),
        };
        if (rect.Width < 4 || rect.Height < 4)
        {
            if (kind != FormFieldKind.CheckBox && rect.Width < 4 && rect.Height < 4) rect = PlaceBox(page, new Point(rect.X + size.Width / 2, rect.Y + size.Height / 2), size);
            else if (kind == FormFieldKind.CheckBox) rect = PlaceBox(page, rect.TopLeft, size);
            else return null;
        }
        return new FieldItem
        {
            FieldKind = kind,
            PageIndex = page,
            Bounds = rect,
            Name = UniqueName(kind switch
            {
                FormFieldKind.CheckBox => "Check",
                FormFieldKind.RadioGroup => "Choice",
                FormFieldKind.ComboBox => "Dropdown",
                FormFieldKind.ListBox => "List",
                _ => "Text",
            }),
            Multiline = multiline,
            OptionsText = kind is FormFieldKind.RadioGroup or FormFieldKind.ComboBox or FormFieldKind.ListBox ? "Option 1, Option 2, Option 3" : "",
        };
    }

    private string UniqueName(string stem)
    {
        var names = Items.OfType<FieldItem>().Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = 1; ; i++)
            if (!names.Contains($"{stem}{i}")) return $"{stem}{i}";
    }

    [RelayCommand]
    private async Task DetectFieldsAsync()
    {
        if (Document is not { } doc) return;
        Status = "Looking for blanks, boxes and underlines…";
        try
        {
            var found = await Task.Run(() => FormsTool.DetectFields(doc.ToInput()));
            var taken = Items.OfType<FieldItem>().ToList();
            var items = new List<EditorItem>();
            foreach (var f in found)
            {
                if (taken.Concat(items.OfType<FieldItem>()).Any(t => t.PageIndex == f.PageIndex && t.Bounds.IntersectsWith(f.Bounds))) continue;
                items.Add(new FieldItem
                {
                    FieldKind = f.Kind,
                    PageIndex = f.PageIndex,
                    Bounds = f.Bounds,
                    Name = taken.Any(t => t.Name == f.Name) || items.OfType<FieldItem>().Any(t => t.Name == f.Name) ? UniqueName(f.Name) : f.Name,
                    Multiline = f.Multiline,
                    OptionsText = f.Options is { Count: > 0 } o ? string.Join(", ", o) : "",
                });
            }
            AddItems(items);
            Status = items.Count == 0 ? "No new places for fields were found." : $"Added {items.Count} field{(items.Count == 1 ? "" : "s")}. Check their names, then save.";
            if (items.Count > 0 && items[0].PageIndex != CurrentPageIndex) CurrentPageIndex = items[0].PageIndex;
            if (items.Count > 0) ActiveTool = SelectTool;
        }
        catch (Exception ex) when (ex is UserFacingException or PasswordRequiredException)
        {
            Error = ex.Message;
            Status = null;
        }
    }

    [RelayCommand]
    private void ClearValues()
    {
        foreach (var f in Items.OfType<FieldItem>().Where(f => f.IsFillable)) f.Value = "";
    }

    public override string? Validate()
    {
        if (base.Validate() is { } problem) return problem;
        var fields = Items.OfType<FieldItem>().ToList();
        if (!fields.Any(f => !f.IsExisting) && !fields.Any(f => f.IsExisting && f.Value != f.OriginalValue) && !Flatten)
            return HasExistingFields ? "Fill in at least one field first." : "Draw a field on the page, or use Detect fields.";
        if (fields.Where(f => !f.IsExisting).GroupBy(f => f.Name).FirstOrDefault(g => g.Count() > 1 || string.IsNullOrWhiteSpace(g.Key)) is { } bad)
            return string.IsNullOrWhiteSpace(bad.Key) ? "Every new field needs a name." : $"Two new fields are both called \"{bad.Key}\"; give each a different name.";
        if (fields.FirstOrDefault(f => !f.IsExisting && f.HasOptions && f.Options.Count == 0) is { } empty)
            return $"Add at least one choice to \"{empty.Name}\".";
        return null;
    }

    protected override async Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var scope = new ProgressScope(progress);
        var input = Document!.ToInput();
        var fields = Items.OfType<FieldItem>().ToList();
        var added = fields.Where(f => !f.IsExisting).Select(f => f.ToNewField()).ToList();
        var values = fields.Where(f => f.IsExisting && f.IsFillable && f.Value != f.OriginalValue)
            .GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.Last().Value);
        var output = OutputFor(added.Count > 0 ? "form" : "filled");
        var fill = values.Count > 0 || Flatten;
        var temp = Path.Combine(Path.GetTempPath(), $"pagesmith_fields_{Guid.NewGuid():N}.pdf");
        try
        {
            ToolResult? created = null;
            if (added.Count > 0)
            {
                created = await FormsTool.CreateFieldsAsync(new CreateFieldsOptions { Input = input, OutputPath = fill ? temp : output, Fields = added },
                    scope.Slice(0, 0.5).AsProgress(), ct);
                if (!fill) return created;
                input = new InputFile(temp, input.Password);
            }
            var filled = await FormsTool.FillAsync(new FillFormOptions { Input = input, OutputPath = output, Values = values, Flatten = Flatten },
                scope.Slice(added.Count > 0 ? 0.5 : 0, 1).AsProgress(), ct);
            filled.InputBytes = new FileInfo(Document.Path).Length;
            if (created != null) filled.Summary = $"{created.Summary} {filled.Summary}";
            return filled;
        }
        finally
        {
            try { File.Delete(temp); } catch { /* temp file */ }
        }
    }

    public override async Task SeedDemoAsync()
    {
        if (!HasExistingFields)
        {
            await DetectFieldsAsync();
            if (CreateFromRect(CheckboxTool, CurrentPageIndex, new Rect(60, 760, 1, 1)) is { } check) AddItem(check);
            if (CreateFromRect(DropdownTool, CurrentPageIndex, new Rect(330, 755, 160, 20)) is FieldItem dropdown)
            {
                dropdown.Name = "Region";
                dropdown.OptionsText = "Asia, Europe, Americas";
                AddItem(dropdown);
            }
        }
        else
        {
            foreach (var f in Items.OfType<FieldItem>().Where(f => f.IsFillable))
            {
                if (f.IsText) f.Value = f.Multiline ? "Line one\nLine two" : "Ada Lovelace";
                else if (f.IsCheck) f.Checked = true;
                else if (f.Options.Count > 0) f.Value = f.Options[^1];
            }
        }
    }
}
