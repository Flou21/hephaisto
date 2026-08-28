using System.Reflection;

namespace Hephaisto.ServiceDefaults;

/// <summary>
/// What version is actually running, read from the assembly rather than from configuration.
/// </summary>
/// <remarks>
/// <para>
/// The source is <see cref="AssemblyInformationalVersionAttribute"/>, which MinVer stamps at
/// build time from the git tag. That matters: a chart value, an env var or a label can all
/// disagree with the binary they are attached to - someone edits the tag in a values file, or
/// a deployment rolls back and the annotation does not. The assembly cannot disagree with
/// itself, so this answers "what is running" rather than "what was someone intending to run".
/// </para>
/// <para>
/// MinVer's format is <c>{semver}+{commit}</c>, e.g. <c>0.0.2-main.0.42+80ed67df...</c>. The
/// build metadata after <c>+</c> is split off because it is not part of the version for any
/// comparison purpose, and because OCI tags cannot contain <c>+</c> at all.
/// </para>
/// </remarks>
public static class BuildInfo
{
    static BuildInfo()
    {
        // This assembly, not Assembly.GetEntryAssembly(). Everything in the image is built
        // from one commit in one repo, so they carry the same stamp - and the entry assembly
        // under a test runner or `dotnet exec` is the host, which carries someone else's.
        (Version, Commit) = Parse(typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }

    /// <summary>Splits <c>{semver}+{commit}</c>. Separate from the constructor so it is testable.</summary>
    internal static (string Version, string Commit) Parse(string? informational)
    {
        // Only reachable if the attribute was stripped, or a build set Version without
        // InformationalVersion. Say "unknown" rather than reporting a plausible-looking
        // zero, which an operator would read as "0.0.0 is deployed".
        if (string.IsNullOrWhiteSpace(informational))
        {
            return ("unknown", "unknown");
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);

        if (plus < 0)
        {
            return (informational, "unknown");
        }

        var version = plus == 0 ? "unknown" : informational[..plus];
        var commit = plus == informational.Length - 1 ? "unknown" : informational[(plus + 1)..];

        return (version, commit);
    }

    /// <summary>The semantic version, without build metadata. Safe as an OCI tag.</summary>
    public static string Version { get; }

    /// <summary>The commit the binary was built from, or <c>unknown</c>.</summary>
    public static string Commit { get; }

    /// <summary>The first 12 characters of <see cref="Commit"/> - enough to identify it, short enough to read.</summary>
    public static string ShortCommit => Commit.Length <= 12 ? Commit : Commit[..12];
}
