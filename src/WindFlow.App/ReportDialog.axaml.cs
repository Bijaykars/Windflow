using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WindFlow.App;

/// <summary>The three lines a PDF report needs that the run cannot know. Defaults come from settings (project
/// and author) and from the current state (scenario). Returns true from ShowDialog when Export was pressed.</summary>
public partial class ReportDialog : Window
{
    public ReportDialog()
    {
        InitializeComponent();   // the generated method, not AvaloniaXamlLoader: it assigns the x:Name fields
    }

    public ReportDialog(Settings settings, string modelName, string scenario) : this()
    {
        ProjectBox.Text = settings.ProjectName.Length > 0 ? settings.ProjectName : modelName;
        AuthorBox.Text = settings.Author;
        ScenarioBox.Text = scenario;
    }

    public string ProjectName => ProjectBox.Text ?? "";
    public string Author => AuthorBox.Text ?? "";
    public string Scenario => ScenarioBox.Text ?? "";

    void OnExport(object? sender, RoutedEventArgs e) => Close(true);

    void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
