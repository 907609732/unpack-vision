using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace UnpackVision.App;

public sealed class SelectableTextColumn : DataGridBoundColumn
{
    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) =>
        CreateTextBox();

    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) =>
        CreateTextBox();

    private TextBox CreateTextBox()
    {
        var textBox = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            IsTabStop = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (Binding is not null)
        {
            textBox.SetBinding(TextBox.TextProperty, Binding);
        }
        return textBox;
    }
}
