using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The plugin's version has exactly one source of truth — <c>&lt;Version&gt;</c> in
/// BTCPayServer.Plugins.Flint.csproj — and these tests keep it that way.
/// </summary>
/// <remarks>
/// <para>
/// BTCPay does not read a version from anywhere in this repository's source: <c>BaseBTCPayServerPlugin.Version</c>
/// reflects over the built assembly's attributes, which the SDK generates from the csproj property. So the
/// temptation is always to add a <c>Constants.PluginVersion</c> "for display" or a hard-coded number in a view,
/// and then to bump one and not the other. The Boltz plugin is the cautionary tale that prompted these tests: it
/// accumulated three version sources that drifted apart, so what the registry advertised, what the assembly
/// reported and what the repository said were three different numbers.
/// </para>
/// <para>
/// One duplicate is unavoidable and therefore mechanised rather than forbidden: the newest heading in
/// CHANGELOG.md. A release whose changelog does not mention it is a release nobody can read, so that heading
/// must agree with the csproj, and <see cref="The_changelog_documents_the_version_being_built"/> fails the suite
/// when it does not.
/// </para>
/// </remarks>
public class PluginVersionTests
{
    /// <summary>
    /// What BTCPay itself will report for this plugin — read the same way BTCPay reads it, through the plugin
    /// type, rather than from the csproj, so this is the shipped number and not the intended one.
    /// </summary>
    private static Version ReportedVersion => new SparkPlugin().Version;

    [Fact]
    public void The_csproj_is_the_version_the_assembly_actually_carries()
    {
        var declared = CsprojVersion();

        // The assembly attribute is always padded to four components, so compare on however many the csproj
        // actually authors: three normally, four for a point release (0.1.4.1). An unauthored revision is
        // padding to strip, never a fourth thing to bump.
        var authored = declared.Count(c => c == '.') + 1;
        Assert.Equal(declared, Components(ReportedVersion, authored));
    }

    [Fact]
    public void The_changelog_documents_the_version_being_built()
    {
        var changelog = File.ReadAllText(Path.Combine(RepoRoot(), "CHANGELOG.md"));

        // The newest entry is the first "## [x.y.z]" heading in the file. Keep-a-Changelog order is
        // newest-first, so anything else means the file has been edited in the wrong place.
        var newest = Regex.Match(changelog, @"^##\s*\[(?<version>\d+\.\d+\.\d+(?:\.\d+)?)\]", RegexOptions.Multiline);
        Assert.True(newest.Success, "CHANGELOG.md has no '## [x.y.z]' heading; the newest release must have one.");

        Assert.Equal(CsprojVersion(), newest.Groups["version"].Value);
    }

    [Fact]
    public void No_second_copy_of_the_version_has_crept_into_the_source()
    {
        // Constants.cs is where a "PluginVersion" constant would land if anyone added one, and it already holds
        // two *other* version constants (the BTCPay support floor and the built-against release), which is
        // exactly the confusion this guards against: neither of those is the plugin's own version and neither
        // may be bumped in lockstep with it.
        var constants = File.ReadAllText(
            Path.Combine(RepoRoot(), "BTCPayServer.Plugins.Flint", "Constants.cs"));

        Assert.True(
            !constants.Contains("PluginVersion", StringComparison.Ordinal),
            "Constants.cs declares something called PluginVersion. The plugin's version has one source, "
            + "<Version> in BTCPayServer.Plugins.Flint.csproj, and BTCPay reads it from the built assembly "
            + "rather than from any constant. Delete the constant instead of keeping two numbers in step.");

        Assert.True(
            !constants.Contains(CsprojVersion(), StringComparison.Ordinal),
            $"Constants.cs contains the string \"{CsprojVersion()}\", which is the plugin's current version. "
            + "Either a version literal has been copied in there, or the plugin has reached a version that "
            + "collides with MinBTCPayServerVersion / BuiltAgainstBTCPayServerVersion — those are BTCPay Server "
            + "versions, not this plugin's, and must not be bumped alongside it. Check which it is.");
    }

    private static string CsprojVersion()
    {
        var csproj = File.ReadAllText(Path.Combine(
            RepoRoot(), "BTCPayServer.Plugins.Flint", "BTCPayServer.Plugins.Flint.csproj"));

        var match = Regex.Match(csproj, @"<Version>(?<version>[^<]+)</Version>");
        Assert.True(match.Success, "BTCPayServer.Plugins.Flint.csproj has no <Version> property.");

        var version = match.Groups["version"].Value.Trim();
        // Three components normally; a fourth only for a point release cut from an already-tagged version.
        Assert.Matches(@"^\d+\.\d+\.\d+(\.\d+)?$", version);
        return version;
    }

    private static string Components(Version version, int count) =>
        count >= 4
            ? $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}.{Math.Max(version.Revision, 0)}"
            : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        // The solution file, not LICENSE: LICENSE is copied into the build output so it ships inside the
        // .btcpay, which made it match the bin directory before it matched the repository root.
        while (dir is not null && !File.Exists(Path.Combine(dir, "BTCPayServer.Plugins.Flint.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}
