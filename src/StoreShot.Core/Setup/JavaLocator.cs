using System.Globalization;
using System.Text.RegularExpressions;
using StoreShot.Core.Processes;

namespace StoreShot.Core.Setup;

/// <param name="JavaHome">Folder containing bin\java.exe, or null when java was found only on PATH.</param>
public sealed record JavaInstall(string JavaExe, string? JavaHome, int MajorVersion, string VersionText, string Source);

public sealed partial class JavaLocator
{
    private readonly IProcessRunner _runner;
    private readonly Func<string, string?> _getEnv;

    public JavaLocator(IProcessRunner runner, Func<string, string?>? getEnv = null)
    {
        _runner = runner;
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
    }

    [GeneratedRegex("version \"(?<v>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    /// <summary>Returns the first working Java among: configured path, JAVA_HOME, Android Studio's bundled JBR, PATH.</summary>
    public async Task<JavaInstall?> LocateAsync(string? configuredHome, CancellationToken ct)
    {
        var candidates = new List<(string Exe, string? Home, string Source)>();
        void AddHome(string? home, string source)
        {
            if (!string.IsNullOrWhiteSpace(home))
            {
                candidates.Add((Path.Combine(home, "bin", "java.exe"), home, source));
            }
        }

        AddHome(configuredHome, "Settings");
        AddHome(_getEnv("JAVA_HOME"), "JAVA_HOME");
        if (_getEnv("ProgramFiles") is { } pf)
        {
            AddHome(Path.Combine(pf, "Android", "Android Studio", "jbr"), "Android Studio bundled JDK");
        }

        candidates.Add(("java", null, "PATH"));

        foreach (var (exe, home, source) in candidates)
        {
            if (home is not null && !File.Exists(exe))
            {
                continue;
            }

            ProcessResult result;
            try
            {
                result = await _runner.RunAsync(new ProcessSpec(exe, ["-version"]) { Timeout = TimeSpan.FromSeconds(20) }, ct);
            }
            catch (UserFacingException)
            {
                continue; // Not runnable (e.g. not on PATH).
            }

            if (result.Succeeded && ParseMajorVersion(result.Combined) is { } parsed)
            {
                return new JavaInstall(exe, home, parsed.Major, parsed.Text, source);
            }
        }

        return null;
    }

    /// <summary>Parses `java -version` output. "1.8.0_301" -> 8, "17.0.2" -> 17, "21" -> 21.</summary>
    public static (int Major, string Text)? ParseMajorVersion(string output)
    {
        var m = VersionPattern().Match(output ?? string.Empty);
        if (!m.Success)
        {
            return null;
        }

        var text = m.Groups["v"].Value;
        var parts = text.Split('.', '_', '-', '+');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first))
        {
            return null;
        }

        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var legacy))
        {
            return (legacy, text);
        }

        return (first, text);
    }
}
