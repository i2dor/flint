using System;
using System.IO;
using Breez.Sdk.Spark;
using BTCPayServer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// Where one store's SDK state lives.
/// </summary>
/// <remarks>
/// Two shapes, because the SDK offers two and they are wired up differently on
/// <c>SdkBuilder</c>: <see cref="Directory"/> maps to <c>WithDefaultStorage</c> (a SQLite file) and
/// <see cref="Backend"/> to <c>WithStorageBackend</c> (Postgres, MySQL, or a custom implementation).
/// </remarks>
public abstract record SparkStorageTarget
{
    /// <summary>A SQLite file under the given directory. The proven default.</summary>
    public sealed record Directory(string Path) : SparkStorageTarget;

    /// <summary>
    /// An SDK <c>StorageBackend</c>, created lazily because it is a native handle.
    /// </summary>
    public sealed record Backend(Func<StorageBackend> Factory) : SparkStorageTarget;

    private SparkStorageTarget()
    {
    }
}

/// <summary>
/// Chooses the storage target for a store's SDK instance.
/// </summary>
/// <remarks>
/// <para>
/// A seam so a Postgres backend can be swapped in without touching any caller. BTCPay already runs
/// Postgres, and the SDK's <c>WithPostgresBackend</c> is first class and multi-tenant by design (every
/// <c>brz_*</c> table carries a <c>user_id</c>), which would remove the per-store SQLite file — and
/// with it the non-WAL journal mode, the filesystem permissions and the need for a persistent volume.
/// </para>
/// <para>
/// The MVP nevertheless defaults to the file backend: that is the path the spike exercised end to end,
/// whereas the Postgres backend only got as far as a clean connection failure, and
/// <c>runMigration = true</c> means the SDK would create 25 tables of its own inside BTCPay's database.
/// Switching is a matter of registering a different implementation of this interface.
/// </para>
/// </remarks>
public interface ISparkStorageProvider
{
    SparkStorageTarget GetTarget(string storeId);
}

/// <summary>
/// Per-store SQLite storage under <c>&lt;DataDir&gt;/Plugins/Spark/&lt;storeId&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Per-store directories are not strictly required — the SDK namespaces its own files by network and
/// identity hash (<c>&lt;dir&gt;/regtest/&lt;8-hex&gt;/storage.sql</c>), so two seeds can share a
/// directory safely — but they keep one store's state trivially identifiable, and they bound the
/// damage if the SDK's namespacing ever changes.
/// </para>
/// <para>
/// The directory is created owner-only. What the SDK persists in <c>storage.sql</c> is its own business and
/// this plugin has not verified whether key material is among it; the wallet state alone — balances, payment
/// history, the wallet's identity — is not something another local user on a shared host should be able to
/// read. The sibling <c>logs</c> directory has been hardened the same way since it was introduced, and an
/// external audit found that storage had been left out; both now go through
/// <see cref="SparkDirectoryPermissions.RestrictToOwner"/> so the two cannot drift apart again.
/// </para>
/// </remarks>
public class FileSparkStorageProvider : ISparkStorageProvider
{
    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly ILogger<FileSparkStorageProvider> _logger;

    public FileSparkStorageProvider(
        IOptions<DataDirectories> dataDirectories,
        ILogger<FileSparkStorageProvider> logger)
    {
        _dataDirectories = dataDirectories;
        _logger = logger;
    }

    public SparkStorageTarget GetTarget(string storeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        var path = GetStorageDirectory(_dataDirectories.Value.DataDir, storeId);
        // Owner-only from the first instant it exists — never created at the umask and restricted afterwards.
        SparkDirectoryPermissions.CreateOwnerOnly(path);
        // Never throws: a host whose filesystem will not take the mode is a weaker host, not a reason to
        // refuse to run the merchant's wallet. Applied whether or not this call created the directory, which
        // is what hardens an install laid down by an earlier version of the plugin.
        SparkDirectoryPermissions.RestrictToOwner(path, _logger);
        return new SparkStorageTarget.Directory(path);
    }

    /// <summary>
    /// The storage directory for a store. Static so callers that only need the path (diagnostics, and
    /// the eventual store-removal cleanup) do not have to create it as a side effect.
    /// </summary>
    public static string GetStorageDirectory(string dataDir, string storeId) =>
        Path.Combine(dataDir, "Plugins", Constants.WorkDirName, storeId);
}
