using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SSGen.App.Services;
using SSGen.App.ViewModels;
using SSGen.App.Views;
using SSGen.Core;
using SSGen.Emulator;

namespace SSGen.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // SSGEN_DATA_DIR: run against a separate data folder (UI testing, clean-slate checks).
        var paths = new AppPaths(Environment.GetEnvironmentVariable("SSGEN_DATA_DIR") is { Length: > 0 } dataDir ? dataDir : null);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                System.IO.Path.Combine(paths.LogsDir, "ssgen-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
        Log.Information("SSGen {Version} starting", typeof(App).Assembly.GetName().Version);

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
                $"SSGen couldn't start.\n\n{ex.Message}\n\nLogs: {paths.LogsDir}",
                "SSGen",
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
            // Disposing the container stops the live preview and every emulator SSGen started; emulators save
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
        services.AddSSGenCore(paths);

        services.AddSingleton(_ => new System.Net.Http.HttpClient { Timeout = Timeout.InfiniteTimeSpan }); // providers apply their own timeouts
        services.AddSingleton(sp => new SSGen.Ai.Profiles.ProviderProfileStore(
            sp.GetRequiredService<SSGen.Core.Data.SqliteDatabase>(),
            sp.GetRequiredService<SSGen.Core.Security.ISecretStore>()));
        services.AddSingleton<SSGen.Ai.AiService>();
        services.AddSingleton<AiProvidersViewModel>();
        services.AddSingleton<AiRunGate>();

        services.AddSingleton<ThemeService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<INavigationService, NavigationService>();

        services.AddSingleton(sp => new DeviceHub(
            sp.GetRequiredService<SSGen.Core.Settings.ISettingsService>(),
            sp.GetRequiredService<SSGen.Core.Processes.IProcessRunner>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.AddSingleton<EmulatorViewModel>();
        services.AddSingleton<AgentViewModel>();
        services.AddSingleton<DesignViewModel>();
        services.AddSingleton<EditorViewModel>();
        services.AddSingleton<GalleryViewModel>();

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
            MessageBox.Show(e.Exception.Message, "SSGen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
