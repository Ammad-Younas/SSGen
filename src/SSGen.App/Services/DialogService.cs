using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using SSGen.App.Views;
using SSGen.Core;

namespace SSGen.App.Services;

/// <summary>All modal UI goes through here so view models stay testable.</summary>
public interface IDialogService
{
    void ShowError(string title, Exception exception);

    void ShowInfo(string title, string message);

    bool Confirm(string title, string message);

    string? PickFolder(string title, string? initialFolder = null);

    string? PickSaveFile(string title, string filter, string defaultFileName);

    /// <summary>Returns (name, parent folder) or null if cancelled.</summary>
    (string Name, string ParentFolder)? ShowNewProjectDialog(string defaultParentFolder);

    void OpenInExplorer(string path);

    void OpenUrl(string url);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current.MainWindow is { IsVisible: true } w ? w : null;

    public static string Describe(Exception exception) => exception switch
    {
        UserFacingException { SuggestedFix: { Length: > 0 } fix } u => $"{u.Message}\n\nWhat to do: {fix}",
        UserFacingException u => u.Message,
        _ => $"{exception.Message}\n\nDetails were written to the log (Settings > Export logs).",
    };

    public void ShowError(string title, Exception exception) =>
        Show(title, Describe(exception), MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string title, string message) =>
        Show(title, message, MessageBoxButton.OK, MessageBoxImage.Information);

    public bool Confirm(string title, string message) =>
        Show(title, message, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? PickFolder(string title, string? initialFolder = null)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrEmpty(initialFolder) && System.IO.Directory.Exists(initialFolder))
        {
            dialog.InitialDirectory = initialFolder;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public (string Name, string ParentFolder)? ShowNewProjectDialog(string defaultParentFolder)
    {
        var window = new NewProjectWindow(this, defaultParentFolder) { Owner = Owner };
        return window.ShowDialog() == true ? (window.ProjectName, window.ParentFolder) : null;
    }

    public void OpenInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });

    public void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static MessageBoxResult Show(string title, string message, MessageBoxButton buttons, MessageBoxImage image) =>
        Owner is { } owner
            ? MessageBox.Show(owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
}
