using System.IO;
using System.Windows;
using StoreShot.App.Services;

namespace StoreShot.App.Views;

public partial class NewProjectWindow : Window
{
    private readonly IDialogService _dialogs;

    public NewProjectWindow(IDialogService dialogs, string defaultParentFolder)
    {
        _dialogs = dialogs;
        InitializeComponent();
        FolderBox.Text = defaultParentFolder;
        NameBox.Text = NextFreeName(defaultParentFolder);
        NameBox.SelectAll();
        Validate();
    }

    public string ProjectName => NameBox.Text.Trim();

    public string ParentFolder => FolderBox.Text.Trim();

    private static string NextFreeName(string parent)
    {
        for (var i = 1; i < 1000; i++)
        {
            var candidate = i == 1 ? "My App" : $"My App {i}";
            if (!Directory.Exists(Path.Combine(parent, candidate)))
            {
                return candidate;
            }
        }

        return "My App";
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (_dialogs.PickFolder("Where should the project be created?", ParentFolder) is { } folder)
        {
            FolderBox.Text = folder;
        }
    }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Validate();

    private bool Validate()
    {
        string? error = null;
        if (ProjectName.Length == 0)
        {
            error = "Enter a project name.";
        }
        else if (ProjectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ProjectName.EndsWith('.'))
        {
            error = "The name can't contain \\ / : * ? \" < > | or end with a dot.";
        }
        else if (ParentFolder.Length == 0 || !Path.IsPathFullyQualified(ParentFolder))
        {
            error = "Choose a full folder path for the location.";
        }
        else if (Directory.Exists(Path.Combine(ParentFolder, ProjectName)) &&
                 Directory.EnumerateFileSystemEntries(Path.Combine(ParentFolder, ProjectName)).Any())
        {
            error = "A non-empty folder with this name already exists there.";
        }

        // Called from TextChanged during InitializeComponent, before every named element exists.
        if (ErrorText is null || PreviewText is null || CreateButton is null)
        {
            return error is null;
        }

        ErrorText.Text = error ?? string.Empty;
        PreviewText.Text = error is null ? $"Will create: {Path.Combine(ParentFolder, ProjectName)}" : string.Empty;
        CreateButton.IsEnabled = error is null;
        return error is null;
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        if (Validate())
        {
            DialogResult = true;
        }
    }
}
