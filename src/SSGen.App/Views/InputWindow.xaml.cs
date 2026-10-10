using System.Windows;

namespace SSGen.App.Views;

/// <summary>Single-value prompt with optional validation (returns an error message, or null when valid).</summary>
public partial class InputWindow : Window
{
    private readonly Func<string, string?>? _validate;

    public InputWindow(string title, string message, string initial, Func<string, string?>? validate)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ValueBox.Text = initial;
        _validate = validate;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_validate?.Invoke(Value) is { } error)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }
}
