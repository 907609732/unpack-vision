using System.Collections.ObjectModel;
using System.Windows;
using UnpackVision.Core;

namespace UnpackVision.App;

public sealed class SelectableIssueTag
{
    public required IssueTagDefinition Definition { get; init; }
    public string Name => Definition.Name;
    public bool Selected { get; set; }
}

public partial class RecordAnnotationDialog : Window
{
    public RecordAnnotationDialog(ScanRecord record, IReadOnlyList<IssueTagDefinition> definitions)
    {
        InitializeComponent();
        TrackingText.Text = $"快递单号：{record.TrackingNo}";
        NoteInput.Text = record.Note;
        Tags = new ObservableCollection<SelectableIssueTag>(definitions.Where(item => item.Enabled).OrderBy(item => item.SortOrder).Select(definition => new SelectableIssueTag
        {
            Definition = definition,
            Selected = record.Tags.Any(assignment => assignment.IsActive && string.Equals(assignment.TagId, definition.Id, StringComparison.OrdinalIgnoreCase))
        }));
        TagsControl.ItemsSource = Tags;
    }

    public ObservableCollection<SelectableIssueTag> Tags { get; }
    public string Note => NoteInput.Text.Trim();

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
