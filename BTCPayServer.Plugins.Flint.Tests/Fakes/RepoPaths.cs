using System.Runtime.CompilerServices;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// The repository checkout, for the tests that read source the build does not ship: views, the changelog, the
/// csproj, the pinned BTCPay submodule.
/// </summary>
/// <remarks>
/// <para>
/// Found by walking up to the directory holding the solution file, not by counting <c>..</c> segments from the
/// test assembly: the output directory's depth below the project is an MSBuild detail (a RID, a custom
/// <c>OutputPath</c> or an artifacts layout all change it). The solution file rather than <c>LICENSE</c>, because
/// <c>LICENSE</c> is copied into the build output so it ships inside the <c>.btcpay</c>, which made it match the
/// bin directory before the repository root.
/// </para>
/// <para>
/// The walk starts from the test assembly and falls back to this file's compile-time path, so a run whose
/// assembly was copied out of the checkout still finds the sources it was built from.
/// </para>
/// </remarks>
public static class RepoPaths
{
    private const string SolutionFile = "BTCPayServer.Plugins.Flint.slnx";

    private static readonly Lazy<string> LazyRoot = new(() => Locate());

    /// <summary>The repository root: the directory holding <c>BTCPayServer.Plugins.Flint.slnx</c>.</summary>
    public static string Root => LazyRoot.Value;

    /// <summary>The plugin project's directory.</summary>
    public static string Plugin => Path.Combine(Root, "BTCPayServer.Plugins.Flint");

    private static string Locate([CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) })
        {
            for (var dir = start; dir is not null; dir = Directory.GetParent(dir)?.FullName)
            {
                if (File.Exists(Path.Combine(dir, SolutionFile)))
                    return dir;
            }
        }

        throw new InvalidOperationException(
            $"{SolutionFile} not found above {AppContext.BaseDirectory} or {Path.GetDirectoryName(thisFile)}");
    }
}
