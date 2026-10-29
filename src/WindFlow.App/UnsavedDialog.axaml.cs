using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WindFlow.App;

/// <summary>S4-02: "the model has changes that were never saved" before something replaces or closes it. Closes with
/// "save", "discard", or null (Cancel, or the window's own close button).</summary>
public partial class UnsavedDialog : Window
{
    public UnsavedDialog()
    {
        InitializeComponent();   // the generated method, not AvaloniaXamlLoader: it assigns the x:Name fields
    }

    public UnsavedDialog(string modelName, string action) : this()
    {
        MessageText.Text = $"“{(modelName.Length > 0 ? modelName : "The model")}” has changes (devices, openings, " +
                           $"discharge coefficients, occupants, names) that are not saved. Save them before {action}?";
    }

    void OnSave(object? sender, RoutedEventArgs e) => Close("save");

    void OnDiscard(object? sender, RoutedEventArgs e) => Close("discard");

    void OnCancel(object? sender, RoutedEventArgs e) => Close((string?)null);
}
