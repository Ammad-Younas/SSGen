using System.Runtime.InteropServices;
using SSGen.Core.Processes;
using SSGen.Core.Settings;

namespace SSGen.Core.Setup;

public enum CheckStatus
{
    Pending,
    Running,
    Pass,
    Warning,
    Fail,
}

/// <summary>An automatic fix the user can trigger from the setup assistant.</summary>
/// <param name="RequiresSdkLicense">True if running it accepts Android SDK licence terms on the user's behalf.</param>
public sealed record SetupFix(string Label, Func<IProgress<InstallProgress>, CancellationToken, Task> RunAsync, bool RequiresSdkLicense = false);

public sealed record CheckResult(
    CheckStatus Status,
    string Summary,
    string? Details = null,
    string? FixInstructions = null,
    SetupFix? Fix = null);

/// <summary>Everything the checks share: located tools, settings and a process runner. Built once per run.</summary>
public sealed class SetupContext
{
    private Task<SdkPackageList>? _packageList;

    public SetupContext(AppSettings settings, SdkLocation sdkLocation, JavaInstall? java, IProcessRunner runner, AppPaths paths)
    {
        Settings = settings;
        SdkLocation = sdkLocation;
        Java = java;
        Runner = runner;
        Paths = paths;
        Packages = sdkLocation.Sdk is { } sdk ? new SdkPackageManager(sdk, runner, java?.JavaHome) : null;
    }

    public AppSettings Settings { get; }
    public SdkLocation SdkLocation { get; }
    public AndroidSdk? Sdk => SdkLocation.Sdk;
    public JavaInstall? Java { get; }
    public IProcessRunner Runner { get; }
    public AppPaths Paths { get; }
    public SdkPackageManager? Packages { get; }

    /// <summary>System image ABI matching this PC's CPU.</summary>
    public static string HostAbi => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64-v8a" : "x86_64";

    /// <summary>Remote package list, fetched lazily and at most once per run (it's slow and needs the network).</summary>
    public Task<SdkPackageList> GetPackageListAsync(CancellationToken ct) =>
        _packageList ??= (Packages ?? throw new InvalidOperationException("No SDK")).ListAsync(ct);
}

public interface ISetupCheck
{
    string Id { get; }

    string Title { get; }

    /// <summary>One line telling the user why SSGen needs this.</summary>
    string Purpose { get; }

    /// <summary>Order in the assistant; checks are listed and run in ascending order.</summary>
    int Order { get; }

    Task<CheckResult> RunAsync(SetupContext context, CancellationToken ct);
}
