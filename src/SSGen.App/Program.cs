using Velopack;

namespace SSGen.App;

/// <summary>
/// Entry point. Velopack runs first: when the installer or uninstaller starts SSGen with its own arguments
/// (install, update, uninstall hooks) it handles them and exits; otherwise the WPF app starts normally.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
