using System.Windows;

namespace SSGen.App.Views;

/// <summary>First-run choice: add an AI provider now, or skip and work in manual mode.</summary>
public partial class AiOnboardingWindow : Window
{
    public AiOnboardingWindow() => InitializeComponent();

    public bool AddProvider { get; private set; }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        AddProvider = true;
        DialogResult = true;
    }

    private void OnSkip(object sender, RoutedEventArgs e) => DialogResult = true;
}
