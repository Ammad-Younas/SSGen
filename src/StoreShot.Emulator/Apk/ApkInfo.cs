namespace StoreShot.Emulator.Apk;

/// <summary>What StoreShot knows about an APK. Serialised into the project, so keep it a plain record.</summary>
public sealed record ApkInfo
{
    public required string PackageName { get; init; }
    public string? Label { get; init; }
    public long? VersionCode { get; init; }
    public string? VersionName { get; init; }
    public int? MinSdk { get; init; }
    public int? TargetSdk { get; init; }

    /// <summary>Fully qualified launcher activity, e.g. "com.example/.MainActivity" style resolved to a class name.</summary>
    public string? LauncherActivity { get; init; }

    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>ABIs with native code (from lib/&lt;abi&gt;/ in the APK). Empty means pure Java/Kotlin: runs anywhere.</summary>
    public IReadOnlyList<string> NativeAbis { get; init; } = [];

    /// <summary>True if the manifest says the app can't run without additional split APKs.</summary>
    public bool RequiresSplits { get; init; }

    /// <summary>"aapt2" or "managed": which parser produced this. The managed one can't resolve resource labels.</summary>
    public required string ParsedBy { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? PackageName : Label;

    /// <summary>True if the APK can run on a device with the given ABI (x86_64 images also run x86 code).</summary>
    public bool SupportsAbi(string deviceAbi) =>
        NativeAbis.Count == 0 ||
        NativeAbis.Contains(deviceAbi) ||
        (deviceAbi == "x86_64" && NativeAbis.Contains("x86")) ||
        (deviceAbi == "arm64-v8a" && NativeAbis.Contains("armeabi-v7a"));
}

/// <summary>Result of inspecting an APK file, including the launcher icon as PNG if it could be extracted.</summary>
public sealed record ApkInspection(ApkInfo Info, byte[]? IconPng, IReadOnlyList<string> Warnings);
