using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using StoreShot.App.Services;
using StoreShot.App.ViewModels;
using StoreShot.App.Views;
using StoreShot.Core;

namespace StoreShot.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                System.IO.Path.Combine(paths.LogsDir, "storeshot-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
        Log.Information("StoreShot Studio {Version} starting", typeof(App).Assembly.GetName().Version);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", args.IsTerminating);

        try
        {
            _services = ConfigureServices(paths);
            _services.GetRequiredService<ThemeService>().Initialize();
            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
#pragma warning disable CA1031 // Startup failure must be reported, whatever it is.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show(
                $"StoreShot Studio couldn't start.\n\n{ex.Message}\n\nLogs: {paths.LogsDir}",
                "StoreShot Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Exiting with code {Code}", e.ApplicationExitCode);
        _services?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices(AppPaths paths)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddSerilog(dispose: false));
        services.AddStoreShotCore(paths);

        services.AddSingleton<ThemeService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<INavigationService, NavigationService>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        var dialogs = _services?.GetService<IDialogService>();
        if (dialogs is not null)
        {
            dialogs.ShowError("Something went wrong", e.Exception);
        }
        else
        {
            MessageBox.Show(e.Exception.Message, "StoreShot Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        e.Handled = true;
    }
}
