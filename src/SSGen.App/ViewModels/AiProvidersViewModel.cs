using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SSGen.Ai;
using SSGen.Ai.Profiles;
using SSGen.App.Services;
using SSGen.Core.Settings;

namespace SSGen.App.ViewModels;

/// <summary>A provider choice for a task; <see cref="Id"/> "" means "use the active provider".</summary>
public sealed record ProviderChoice(string Id, string Name);

/// <summary>One row of the per-task model table.</summary>
public sealed partial class TaskAssignmentViewModel : ObservableObject
{
    private readonly Action<TaskAssignmentViewModel> _save;
    private bool _loading;

    public TaskAssignmentViewModel(AiTask task, string title, string purpose, Action<TaskAssignmentViewModel> save)
    {
        Task = task;
        Title = title;
        Purpose = purpose;
        _save = save;
    }

    public AiTask Task { get; }
    public string Title { get; }
    public string Purpose { get; }

    /// <summary>Selected profile id, or "" for the active provider.</summary>
    [ObservableProperty]
    public partial string ProfileId { get; set; } = "";

    [ObservableProperty]
    public partial string? Model { get; set; }

    partial void OnProfileIdChanged(string value) => Persist();

    partial void OnModelChanged(string? value) => Persist();

    public void Load(string? profileId, string? model)
    {
        _loading = true;
        ProfileId = profileId ?? "";
        Model = model;
        _loading = false;
    }

    private void Persist()
    {
        if (!_loading)
        {
            _save(this);
        }
    }
}

/// <summary>An editable price row.</summary>
public sealed partial class PriceRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Model { get; set; } = "";

    [ObservableProperty]
    public partial string Input { get; set; } = "";

    [ObservableProperty]
    public partial string Output { get; set; } = "";
}

/// <summary>Settings > AI providers: profiles, active provider, per-task models, prices, cost cap, consent.</summary>
public sealed partial class AiProvidersViewModel : ObservableObject
{
    private readonly AiService _ai;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private bool _loading;

    public AiProvidersViewModel(AiService ai, ISettingsService settings, IDialogService dialogs)
    {
        _ai = ai;
        _settings = settings;
        _dialogs = dialogs;
        Tasks =
        [
            new(AiTask.Exploration, "Exploration", "Chooses each step while exploring the app (many calls; a fast model works well).", SaveTask),
            new(AiTask.Ranking, "Screen ranking", "Scores and orders the captured screens (M5).", SaveTask),
            new(AiTask.Headlines, "Headlines", "Writes marketing captions (M5).", SaveTask),
            new(AiTask.Translation, "Translation", "Translates captions into other languages (M7).", SaveTask),
        ];
        Reload();
    }

    public ObservableCollection<ProviderProfile> Profiles { get; } = [];

    /// <summary>"Active provider" plus every profile, for task rows.</summary>
    public ObservableCollection<ProviderChoice> TaskProfileChoices { get; } = [];

    public IReadOnlyList<TaskAssignmentViewModel> Tasks { get; }

    public ObservableCollection<PriceRowViewModel> Prices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfiles), nameof(ActiveSummary))]
    public partial ProviderProfile? ActiveProfile { get; set; }

    [ObservableProperty]
    public partial ProviderProfile? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial string CostCap { get; set; } = "";

    [ObservableProperty]
    public partial bool CloudConsent { get; set; }

    [ObservableProperty]
    public partial string BlocklistExtra { get; set; } = "";

    public bool HasProfiles => Profiles.Count > 0;

    public string ActiveSummary => ActiveProfile is null
        ? "No AI provider active: exploration runs in systematic (no-AI) mode."
        : $"Active: {ActiveProfile.Name} · {ActiveProfile.Model}";

    partial void OnActiveProfileChanged(ProviderProfile? value)
    {
        if (!_loading)
        {
            _settings.Save(_settings.Current with { ActiveProviderId = value?.Id });
        }
    }

    partial void OnCostCapChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        decimal? cap = decimal.TryParse(value.Trim().TrimStart('$'), NumberStyles.Number, CultureInfo.CurrentCulture, out var v) && v > 0 ? v : null;
        _settings.Save(_settings.Current with { CostCapUsd = cap });
    }

    partial void OnCloudConsentChanged(bool value)
    {
        if (!_loading)
        {
            _settings.Save(_settings.Current with { CloudScreenshotConsent = value });
        }
    }

    partial void OnBlocklistExtraChanged(string value)
    {
        if (!_loading)
        {
            _settings.Save(_settings.Current with { AgentBlocklistExtra = value });
        }
    }

    public void Reload()
    {
        _loading = true;
        var profiles = _ai.Profiles.List();
        Profiles.Clear();
        TaskProfileChoices.Clear();
        TaskProfileChoices.Add(new ProviderChoice("", "Active provider"));
        foreach (var p in profiles)
        {
            Profiles.Add(p);
            TaskProfileChoices.Add(new ProviderChoice(p.Id, p.Name));
        }

        ActiveProfile = profiles.FirstOrDefault(p => p.Id == _settings.Current.ActiveProviderId);
        foreach (var t in Tasks)
        {
            var (profileId, model) = _ai.Profiles.GetTask(t.Task);
            t.Load(profiles.Any(p => p.Id == profileId) ? profileId : null, model);
        }

        Prices.Clear();
        foreach (var price in _ai.Profiles.ListPrices())
        {
            Prices.Add(new PriceRowViewModel
            {
                Model = price.Model,
                Input = price.InputPerMillion.ToString(CultureInfo.CurrentCulture),
                Output = price.OutputPerMillion.ToString(CultureInfo.CurrentCulture),
            });
        }

        CostCap = _settings.Current.CostCapUsd?.ToString(CultureInfo.CurrentCulture) ?? "";
        CloudConsent = _settings.Current.CloudScreenshotConsent;
        BlocklistExtra = _settings.Current.AgentBlocklistExtra;
        _loading = false;
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(ActiveSummary));
    }

    [RelayCommand]
    private void AddProvider()
    {
        if (_dialogs.EditProvider(null) is { } saved)
        {
            if (ActiveProfile is null)
            {
                _settings.Save(_settings.Current with { ActiveProviderId = saved.Id });
            }

            Reload();
        }
    }

    [RelayCommand]
    private void EditProvider(ProviderProfile? profile)
    {
        if ((profile ?? SelectedProfile) is { } p && _dialogs.EditProvider(p) is not null)
        {
            Reload();
        }
    }

    [RelayCommand]
    private void DeleteProvider(ProviderProfile? profile)
    {
        if ((profile ?? SelectedProfile) is not { } p ||
            !_dialogs.Confirm("Remove provider", $"Remove \"{p.Name}\" and its saved API key?"))
        {
            return;
        }

        _ai.Profiles.Delete(p.Id);
        if (_settings.Current.ActiveProviderId == p.Id)
        {
            _settings.Save(_settings.Current with { ActiveProviderId = null });
        }

        Reload();
    }

    [RelayCommand]
    private void AddPrice() => Prices.Add(new PriceRowViewModel { Model = ActiveProfile?.Model ?? "" });

    [RelayCommand]
    private void SavePrices()
    {
        var errors = new List<string>();
        foreach (var existing in _ai.Profiles.ListPrices())
        {
            if (!Prices.Any(p => string.Equals(p.Model.Trim(), existing.Model, StringComparison.OrdinalIgnoreCase)))
            {
                _ai.Profiles.DeletePrice(existing.Model);
            }
        }

        foreach (var row in Prices.Where(p => !string.IsNullOrWhiteSpace(p.Model)))
        {
            if (decimal.TryParse(row.Input, NumberStyles.Number, CultureInfo.CurrentCulture, out var i) && i >= 0 &&
                decimal.TryParse(row.Output, NumberStyles.Number, CultureInfo.CurrentCulture, out var o) && o >= 0)
            {
                _ai.Profiles.SetPrice(new ModelPrice(row.Model.Trim(), i, o));
            }
            else
            {
                errors.Add(row.Model);
            }
        }

        if (errors.Count > 0)
        {
            _dialogs.ShowInfo("Some prices weren't saved", "Enter numbers (USD per million tokens) for: " + string.Join(", ", errors));
        }
        else
        {
            _dialogs.ShowInfo("Prices saved", "Cost estimates and the spending cap use these prices.");
        }

        Reload();
    }

    [RelayCommand]
    private void RemovePrice(PriceRowViewModel? row)
    {
        if (row is not null)
        {
            Prices.Remove(row);
        }
    }

    private void SaveTask(TaskAssignmentViewModel t) =>
        _ai.Profiles.SetTask(t.Task, t.ProfileId.Length == 0 ? null : t.ProfileId, string.IsNullOrWhiteSpace(t.Model) ? null : t.Model.Trim());
}
