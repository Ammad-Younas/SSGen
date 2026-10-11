using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SSGen.App.Services;
using SSGen.Core.Projects;
using SSGen.Core.Settings;

namespace SSGen.App.ViewModels;

public sealed record NavItem(PageKey Key, string Title, string Glyph, object Page);

/// <summary>Shell: left navigation, current page, window title.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(
        HomeViewModel home,
        EmulatorViewModel emulator,
        AgentViewModel agent,
        DesignViewModel design,
        SetupViewModel setup,
        SettingsViewModel settings,
        INavigationService navigation,
        IProjectService projects,
        ISettingsService settingsService,
        IDialogService dialogs)
    {
        _dialogs = dialogs;
        NavItems =
        [
            new(PageKey.Home, "Home", "", home),
            new(PageKey.Emulator, "Emulator", "\uE8EA", emulator),
            new(PageKey.Agent, "Explore", "", agent),
            new(PageKey.Design, "Design", "", design),
            new(PageKey.Gallery, "Gallery", "", new PlaceholderViewModel("Gallery", "", "M6",
                "Your generated store images, grouped by store and device size, with a store-listing preview.")),
            new(PageKey.Editor, "Editor", "", new PlaceholderViewModel("Editor", "", "M6",
                "A layered editor for any image: text, backgrounds, frames, filters, undo/redo.")),
            new(PageKey.Export, "Export", "", new PlaceholderViewModel("Export", "", "M7",
                "Validate and export store-ready files per store and device size, with a manifest and optional ZIP.")),
            new(PageKey.Setup, "Environment", "", setup),
            new(PageKey.Settings, "Settings", "", settings),
        ];

        navigation.NavigationRequested += (_, key) => SelectedNav = NavItems.First(n => n.Key == key);
        projects.CurrentChanged += (_, p) => UpdateTitle(p);
        UpdateTitle(projects.Current);

        _settings = settingsService;
        IsNavCollapsed = settingsService.Current.NavCollapsed;

        // First run lands on the setup assistant; afterwards on Home.
        SelectedNav = NavItems.First(n => n.Key == (settingsService.Current.SetupCompleted ? PageKey.Home : PageKey.Setup));
    }

    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;

    /// <summary>Once, after the window is shown: users who finished setup before AI support existed get the AI choice.</summary>
    public void OnShown()
    {
        if (!_settings.Current.SetupCompleted || _settings.Current.AiOnboardingDone)
        {
            return;
        }

        var add = _dialogs.ShowAiOnboarding();
        _settings.Save(_settings.Current with { AiOnboardingDone = true });
        if (add && _dialogs.EditProvider(null) is { } profile && _settings.Current.ActiveProviderId is null)
        {
            _settings.Save(_settings.Current with { ActiveProviderId = profile.Id });
        }
    }

    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty]
    public partial bool IsNavCollapsed { get; set; }

    [RelayCommand]
    private void ToggleNav()
    {
        IsNavCollapsed = !IsNavCollapsed;
        _settings.Save(_settings.Current with { NavCollapsed = IsNavCollapsed });
    }

    [ObservableProperty]
    public partial NavItem? SelectedNav { get; set; }

    [ObservableProperty]
    public partial object? CurrentPage { get; set; }

    [ObservableProperty]
    public partial string WindowTitle { get; set; } = "SSGen";

    [ObservableProperty]
    public partial string? ProjectName { get; set; }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null)
        {
            return;
        }

        CurrentPage = value.Page;
        if (value.Page is IPageActivated activated)
        {
            activated.OnActivated();
        }
    }

    private void UpdateTitle(Project? project)
    {
        ProjectName = project?.Name;
        WindowTitle = project is null ? "SSGen" : $"{project.Name} - SSGen";
    }
}

/// <summary>Implemented by pages that refresh when shown.</summary>
public interface IPageActivated
{
    void OnActivated();
}

/// <summary>Empty state for features arriving in a later milestone.</summary>
public sealed record PlaceholderViewModel(string Title, string Glyph, string Milestone, string Description);
