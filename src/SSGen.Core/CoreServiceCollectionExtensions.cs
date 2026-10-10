using Microsoft.Extensions.DependencyInjection;
using SSGen.Core.Data;
using SSGen.Core.Processes;
using SSGen.Core.Projects;
using SSGen.Core.Security;
using SSGen.Core.Settings;
using SSGen.Core.Setup;

namespace SSGen.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers Core services. The app database is migrated on first resolution.</summary>
    public static IServiceCollection AddSSGenCore(this IServiceCollection services, AppPaths paths)
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
