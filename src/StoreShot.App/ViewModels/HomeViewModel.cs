using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StoreShot.App.Services;
using StoreShot.Core;
using StoreShot.Core.Projects;
using StoreShot.Core.Settings;

namespace StoreShot.App.ViewModels;

public sealed partial class HomeViewModel : ObservableObject, IPageActivated
{
    private readonly IProjectService _projects;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly ILogger<HomeViewModel> _log;

    public HomeViewModel(
        IProjectService projects,
        ISettingsService settings,
        IDialogService dialogs,
        INavigationService navigation,
        ILogger<HomeViewModel> log)
    {
        _projects = projects;
        _settings = settings;
        _dialogs = dialogs;
        _navigation = navigation;
        _log = log;
        _projects.CurrentChanged += (_, p) =>
        {
            CurrentProject = p;
            RefreshRecent();
        };
        RefreshRecent();
    }

    public ObservableCollection<RecentProject> RecentProjects { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    public partial Project? CurrentProject { get; set; }

    public bool HasProject => CurrentProject is not null;

    public bool HasRecent => RecentProjects.Count > 0;

    public void OnActivated() => RefreshRecent();

    [RelayCommand]
    private void NewProject()
    {
        var parent = _settings.Current.ProjectsFolder ?? AppPaths.DefaultProjectsDir;
        if (_dialogs.ShowNewProjectDialog(parent) is not { } input)
        {
            return;
        }

        Try("Couldn't create the project", () =>
        {
            Directory.CreateDirectory(input.ParentFolder);
            var project = _projects.Create(input.Name, input.ParentFolder);
            _log.LogInformation("Created project {Name} at {Folder}", project.Name, project.Folder);
        });
    }

    [RelayCommand]
    private void OpenProject()
    {
        var folder = _dialogs.PickFolder("Open a StoreShot project folder", _settings.Current.ProjectsFolder ?? AppPaths.DefaultProjectsDir);
        if (folder is not null)
        {
            Try("Couldn't open the project", () => _projects.Open(folder));
        }
    }

    [RelayCommand]
    private void OpenRecent(RecentProject? recent)
    {
        if (recent is null)
        {
            return;
        }

        if (!recent.Exists)
        {
            if (_dialogs.Confirm("Project not found", $"\"{recent.Path}\" no longer exists or was moved.\n\nRemove it from the recent list?"))
            {
                _projects.RemoveRecent(recent.Path);
                RefreshRecent();
            }

            return;
        }

        Try("Couldn't open the project", () => _projects.Open(recent.Path));
    }

    [RelayCommand]
    private void RemoveRecent(RecentProject? recent)
    {
        if (recent is not null)
        {
            _projects.RemoveRecent(recent.Path);
            RefreshRecent();
        }
    }

    [RelayCommand]
    private void ShowProjectFolder()
    {
        if (CurrentProject is { } p)
        {
            _dialogs.OpenInExplorer(p.Folder);
        }
    }

    [RelayCommand]
    private void CloseProject() => _projects.Close();

    [RelayCommand]
    private void CheckEnvironment() => _navigation.NavigateTo(PageKey.Setup);

    private void RefreshRecent()
    {
        RecentProjects.Clear();
        foreach (var r in _projects.GetRecent())
        {
            RecentProjects.Add(r);
        }

        OnPropertyChanged(nameof(HasRecent));
    }

    private void Try(string title, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is UserFacingException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log.LogWarning(ex, "{Title}", title);
            _dialogs.ShowError(title, ex);
        }
    }
}
