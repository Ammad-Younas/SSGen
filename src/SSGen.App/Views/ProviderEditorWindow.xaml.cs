using System.Windows;
using System.Windows.Controls;
using SSGen.Ai;
using SSGen.Ai.Profiles;
using SSGen.Ai.Providers;

namespace SSGen.App.Views;

/// <summary>Add/edit one AI provider profile, fetch its models and test the connection before saving.</summary>
public partial class ProviderEditorWindow : Window
{
    private readonly AiService _ai;
    private readonly ProviderProfile? _existing;
    private readonly bool _initializing = true;

    public ProviderEditorWindow(AiService ai, ProviderProfile? existing)
    {
        _ai = ai;
        _existing = existing;
        InitializeComponent();
        if (existing is not null)
        {
            Heading.Text = "Edit AI provider";
            SelectType(existing.Type);
            NameBox.Text = existing.Name;
            UrlBox.Text = existing.BaseUrl ?? "";
            ModelBox.Text = existing.Model;
            SelectVision(existing.VisionMode);
            KeyHint.Text = ai.Profiles.HasApiKey(existing) ? "A key is saved. Leave empty to keep it." : "No key saved yet.";
        }
        else
        {
            TypeBox.SelectedIndex = 0;
        }

        _initializing = false;
        ApplyType(fillDefaults: existing is null);
        Loaded += async (_, _) =>
        {
            if (SelectedType == ProviderType.Ollama && existing is null)
            {
                await FetchModelsAsync(quiet: true);
            }
        };
    }

    public ProviderProfile? Result { get; private set; }

    private ProviderType SelectedType => Enum.Parse<ProviderType>((string)((ComboBoxItem)TypeBox.SelectedItem).Tag);

    private VisionMode SelectedVision => Enum.Parse<VisionMode>((string)((ComboBoxItem)VisionBox.SelectedItem).Tag);

    private void SelectType(ProviderType t) =>
        TypeBox.SelectedItem = TypeBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == t.ToString());

    private void SelectVision(VisionMode v) =>
        VisionBox.SelectedItem = VisionBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == v.ToString());

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            ApplyType(fillDefaults: true);
        }
    }

    private void ApplyType(bool fillDefaults)
    {
        var type = SelectedType;
        var needsKey = type is not ProviderType.Ollama;
        KeyPanel.Visibility = needsKey ? Visibility.Visible : Visibility.Collapsed;
        if (_existing is null || _existing.Type != type)
        {
            KeyHint.Text = type == ProviderType.OpenAICompatible ? "Optional for local servers such as LM Studio." : "Required.";
        }

        UrlHint.Text = type switch
        {
            ProviderType.OpenAICompatible => "e.g. https://openrouter.ai/api/v1 · https://api.groq.com/openai/v1 · http://127.0.0.1:1234/v1 (LM Studio)",
            ProviderType.Ollama => "Default: " + ProviderProfile.DefaultBaseUrl(type),
            _ => "Leave empty for the official endpoint (" + ProviderProfile.DefaultBaseUrl(type) + ").",
        };

        if (!fillDefaults)
        {
            return;
        }

        NameBox.Text = type switch
        {
            ProviderType.Anthropic => "Claude",
            ProviderType.OpenAI => "OpenAI",
            ProviderType.Gemini => "Gemini",
            ProviderType.Ollama => "Ollama (local)",
            _ => "OpenAI-compatible",
        };
        UrlBox.Text = type is ProviderType.OpenAICompatible or ProviderType.Ollama ? ProviderProfile.DefaultBaseUrl(type) : "";
        ModelBox.Text = type == ProviderType.Anthropic ? AnthropicProvider.SuggestedModel : "";
        ModelBox.ItemsSource = null;
    }

    private ProviderProfile? BuildProfile(bool requireModel)
    {
        var name = NameBox.Text.Trim();
        var model = ModelBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowResult(false, "Enter a name for this provider.");
            return null;
        }

        if (requireModel && model.Length == 0)
        {
            ShowResult(false, "Enter or fetch a model name.");
            return null;
        }

        var url = UrlBox.Text.Trim();
        if (url.Length > 0 && !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            ShowResult(false, "The base URL isn't a valid URL (it should start with http:// or https://).");
            return null;
        }

        return new ProviderProfile
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            CreatedUtc = _existing?.CreatedUtc ?? DateTime.UtcNow,
            Name = name,
            Type = SelectedType,
            BaseUrl = url.Length == 0 ? null : url,
            Model = model,
            VisionMode = SelectedVision,
            DetectedVision = _existing?.Model == model && _existing.Type == SelectedType ? _existing.DetectedVision : null,
        };
    }

    /// <summary>The key typed in the box, else the saved one (when editing), else null.</summary>
    private string? KeyForTest(ProviderProfile profile) =>
        KeyBox.Password.Length > 0 ? KeyBox.Password : _existing is not null ? _ai.Profiles.GetApiKey(profile) : null;

    private async void OnFetchModels(object sender, RoutedEventArgs e) => await FetchModelsAsync(quiet: false);

    private async Task FetchModelsAsync(bool quiet)
    {
        if (BuildProfile(requireModel: false) is not { } profile)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var provider = _ai.Create(profile with { Model = profile.Model.Length == 0 ? "probe" : profile.Model }, apiKeyOverride: KeyForTest(profile) ?? "");
            var models = await provider.ListModelsAsync();
            ModelBox.ItemsSource = models;
            if (ModelBox.Text.Length == 0 && models.Count > 0)
            {
                ModelBox.Text = models[0];
            }

            if (!quiet)
            {
                ShowResult(true, models.Count == 0 ? "Connected, but the server listed no models." : $"{models.Count} models available. Pick one from the list.");
            }
        }
        catch (LlmException ex)
        {
            if (!quiet)
            {
                ShowResult(false, ex.Message + (ex.SuggestedFix is { } f ? " " + f : ""));
            }
            else if (SelectedType == ProviderType.Ollama)
            {
                ShowResult(false, "Ollama isn't running at " + profile.EffectiveBaseUrl + ". Install it from ollama.com, run a vision model (e.g. 'ollama pull qwen2.5vl'), then Fetch models.");
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (BuildProfile(requireModel: true) is not { } profile)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _ai.TestConnectionAsync(profile, KeyForTest(profile) ?? "");
            ShowResult(result.Success, result.Message);
            if (result.Models.Count > 0)
            {
                ModelBox.ItemsSource = result.Models;
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (BuildProfile(requireModel: true) is not { } profile)
        {
            return;
        }

        var key = KeyBox.Password;
        if (profile.NeedsApiKey && key.Length == 0 && (_existing is null || !_ai.Profiles.HasApiKey(profile)))
        {
            ShowResult(false, "Enter the API key for this provider.");
            return;
        }

        _ai.Profiles.Save(profile, key.Length > 0 ? key : null);
        Result = profile;
        DialogResult = true;
    }

    private void ShowResult(bool ok, string message)
    {
        ResultBorder.Visibility = Visibility.Visible;
        ResultIcon.Text = ok ? "" : "";
        ResultIcon.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
        ResultText.Text = message;
    }

    private void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        IsEnabled = !busy;
    }
}
