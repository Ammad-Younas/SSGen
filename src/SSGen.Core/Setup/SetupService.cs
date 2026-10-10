using SSGen.Core.Processes;
using SSGen.Core.Settings;

namespace SSGen.Core.Setup;

public interface ISetupService
{
    IReadOnlyList<ISetupCheck> Checks { get; }

    /// <summary>Locates the SDK and Java using current settings. Cheap enough to call before every run.</summary>
    Task<SetupContext> CreateContextAsync(CancellationToken ct);

    /// <summary>Runs one check, converting unexpected exceptions into a Fail result so the assistant never fails silently.</summary>
    Task<CheckResult> RunCheckAsync(ISetupCheck check, SetupContext context, CancellationToken ct);
}

public sealed class SetupService : ISetupService
{
    private readonly ISettingsService _settings;
    private readonly IProcessRunner _runner;
    private readonly AppPaths _paths;

    public SetupService(IEnumerable<ISetupCheck> checks, ISettingsService settings, IProcessRunner runner, AppPaths paths)
    {
        Checks = checks.OrderBy(c => c.Order).ToList();
        _settings = settings;
        _runner = runner;
        _paths = paths;
    }

    public IReadOnlyList<ISetupCheck> Checks { get; }

    public async Task<SetupContext> CreateContextAsync(CancellationToken ct)
    {
        var settings = _settings.Current;
        var sdk = AndroidSdkLocator.Locate(settings.AndroidSdkPath);
        var java = await new JavaLocator(_runner).LocateAsync(settings.JavaHomePath, ct);
        return new SetupContext(settings, sdk, java, _runner, _paths);
    }

    public async Task<CheckResult> RunCheckAsync(ISetupCheck check, SetupContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        try
        {
            return await check.RunAsync(context, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UserFacingException ex)
        {
            return new CheckResult(CheckStatus.Fail, ex.Message, FixInstructions: ex.SuggestedFix);
        }
#pragma warning disable CA1031 // Any failure must surface as a readable result rather than crash the assistant.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new CheckResult(CheckStatus.Fail, "The check failed unexpectedly.", ex.ToString(), "Export logs from Settings and report the problem.");
        }
    }
}
