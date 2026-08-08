using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Options;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Nothing this plugin writes under BTCPay's data directory is readable by another user on the host.
/// </summary>
/// <remarks>
/// <para>
/// The plugin creates two kinds of directory under <c>&lt;DataDir&gt;/Plugins/Spark</c>: <c>logs</c>, holding
/// the <c>sdk.log</c> the Rust side writes, and one per store, holding the SDK's SQLite wallet state. Both are
/// created with <c>Directory.CreateDirectory</c>, which applies the process umask and yields <c>0755</c> on an
/// ordinary host, and both need the same correction.
/// </para>
/// <para>
/// <b>Only one of them had it.</b> An external audit found the log directory hardened and the storage
/// directory not, and the reason was that the hardening lived privately beside the log path's only caller.
/// Both go through <c>SparkDirectoryPermissions</c> now, and both are asserted here so a third directory
/// added later has an obvious place to be checked rather than an obvious place to be forgotten.
/// </para>
/// </remarks>
public class SparkStorageDirectoryPermissionTests
{
    private const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// A store's SDK storage directory is not readable by other users on the host.
    /// </summary>
    /// <remarks>
    /// The SDK writes <c>storage.sql</c> inside it at whatever mode it pleases and the plugin cannot choose
    /// that; it owns the directory, and a directory without other-execute cannot be traversed to reach the
    /// file inside it. Whether the SDK persists key material there is SDK-internal and unverified — the wallet
    /// state is reason enough on its own.
    /// </remarks>
    [Fact]
    public void A_stores_storage_directory_is_created_owner_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");

        using var dir = new TempDirectory();
        var target = Assert.IsType<SparkStorageTarget.Directory>(Provider(dir.Path).GetTarget("store-a"));

        Assert.Equal(
            FileSparkStorageProvider.GetStorageDirectory(dir.Path, "store-a"), target.Path);
        AssertOwnerOnly(target.Path);
    }

    /// <summary>
    /// The log directory beside it, so the pair cannot drift apart again.
    /// </summary>
    [Fact]
    public void The_log_directory_is_created_owner_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");

        using var dir = new TempDirectory();
        var target = Path.Combine(dir.Path, "logs");

        Assert.True(SparkLogging.Initialise(
            target, new CapturingLogger<SparkStorageDirectoryPermissionTests>(), "info", (_, _, _) => { }));

        AssertOwnerOnly(target);
    }

    /// <summary>
    /// A directory an earlier version of the plugin left world-readable is corrected on the next start.
    /// </summary>
    /// <remarks>
    /// The restriction is applied on every call rather than only when the directory is created, which is the
    /// only thing that helps an install that already exists. A fix that only protected new stores would leave
    /// every wallet running today exactly as exposed as the audit found it.
    /// </remarks>
    [Fact]
    public void An_existing_world_readable_storage_directory_is_hardened_rather_than_left_alone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");

        using var dir = new TempDirectory();
        var path = FileSparkStorageProvider.GetStorageDirectory(dir.Path, "store-b");
        Directory.CreateDirectory(path);

        if (!OperatingSystem.IsWindows())
        {
            // What the umask gave the directories this plugin has already created on live servers.
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        Provider(dir.Path).GetTarget("store-b");

        AssertOwnerOnly(path);
    }

    /// <summary>
    /// A mode that cannot be set is reported, not thrown.
    /// </summary>
    /// <remarks>
    /// The same discipline the log path has always had, and it matters more here: logging is a diagnostic aid,
    /// but storage is how the wallet runs. A filesystem that will not carry the mode, or a container running as
    /// a user that does not own the path, must leave the merchant with a working wallet and a warning — not a
    /// store that refuses to start over a permission it could not tighten.
    /// </remarks>
    [Fact]
    public void A_directory_whose_mode_cannot_be_set_is_logged_rather_than_thrown()
    {
        var log = new CapturingLogger<SparkStorageDirectoryPermissionTests>();

        // Nothing is there to chmod. Any reason the call fails takes the same branch.
        var record = Record.Exception(() => SparkDirectoryPermissions.RestrictToOwner(
            Path.Combine(Path.GetTempPath(), "spark-not-a-directory", Guid.NewGuid().ToString("N")), log));

        Assert.Null(record);

        if (!OperatingSystem.IsWindows())
            Assert.Contains("Could not restrict the Spark directory", log.AllText);
    }

    private static FileSparkStorageProvider Provider(string dataDir) =>
        new(Options.Create(new DataDirectories { DataDir = dataDir }),
            new CapturingLogger<FileSparkStorageProvider>());

    /// <summary>
    /// The platform check is restated inside rather than declared as an attribute, because the analyzer cannot
    /// see through <c>Assert.SkipWhen</c> and this is the form it does understand. Unreachable on Windows either
    /// way, where the plugin makes no claim about directory ACLs.
    /// </summary>
    private static void AssertOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "spark-storage-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
