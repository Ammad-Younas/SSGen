using Microsoft.Extensions.DependencyInjection;
using StoreShot.Core.Data;
using StoreShot.Core.Processes;
using StoreShot.Core.Projects;
using StoreShot.Core.Security;
using StoreShot.Core.Settings;
using StoreShot.Core.Setup;

namespace StoreShot.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers Core services. The app database is migrated on first resolution.</summary>
    public static IServiceCollection AddStoreShotCore(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton(_ =>
        {
            var db = new SqliteDatabase(paths.AppDatabase, MigrationSet.App);
            db.Migrate();
            return db;
        });
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        services.AddSingleton<IProjectService, ProjectService>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();

        services.AddSingleton<ISetupCheck, DotNetRuntimeCheck>();
        services.AddSingleton<ISetupCheck, AndroidSdkCheck>();
        services.AddSingleton<ISetupCheck, PlatformToolsCheck>();
        services.AddSingleton<ISetupCheck, EmulatorCheck>();
        services.AddSingleton<ISetupCheck, BuildToolsCheck>();
        services.AddSingleton<ISetupCheck, CmdlineToolsCheck>();
        services.AddSingleton<ISetupCheck, SystemImageCheck>();
        services.AddSingleton<ISetupCheck, AccelerationCheck>();
        services.AddSingleton<ISetupCheck, JavaCheck>();
        services.AddSingleton<ISetupCheck, DiskSpaceCheck>();
        services.AddSingleton<ISetupService, SetupService>();
        return services;
    }
}
