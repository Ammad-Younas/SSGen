using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using StoreShot.App.Services;
using StoreShot.App.ViewModels;
using StoreShot.App.Views;
using StoreShot.Core;
using StoreShot.Emulator;

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
        if (_services is { } services)
        {
            // Disposing the container stops the live preview and every emulator StoreShot started; emulators save
            // their quick-boot snapshot first, which can take a while. Run off the UI thread to avoid deadlocking
            // on the dispatcher. If it overruns, the job object terminates whatever is left when we exit.
            _services = null;
            var shutdown = Task.Run(() => services.DisposeAsync().AsTask());
            try
            {
                if (!shutdown.Wait(TimeSpan.FromSeconds(90)))
                {
                    Log.Warning("Shutdown timed out; the job object will terminate remaining emulator processes");
                }
            }
            catch (AggregateException ex)
            {
                Log.Error(ex, "Error during shutdown");
            }
        }

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

        services.AddSingleton(sp => new DeviceHub(
            sp.GetRequiredService<StoreShot.Core.Settings.ISettingsService>(),
            sp.GetRequiredService<StoreShot.Core.Processes.IProcessRunner>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.AddSingleton<EmulatorViewModel>();

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
        e.Handled = true;
        if (_services is null)
        {
            return; // shutting down: log only
        }

        IDialogService? dialogs;
        try
        {
            dialogs = _services.GetService<IDialogService>();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (dialogs is not null)
        {
            dialogs.ShowError("Something went wrong", e.Exception);
        }
        else
        {
            MessageBox.Show(e.Exception.Message, "StoreShot Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
