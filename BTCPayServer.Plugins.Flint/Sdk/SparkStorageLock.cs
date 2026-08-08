using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// An exclusive claim on one store's SDK storage directory, held for as long as its wallet is running.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this defends against.</b> <c>SparkService</c>'s single-instance-per-wallet guard is a dictionary in
/// memory, so it is per <em>process</em>. Two BTCPay instances sharing one data directory — a blue/green
/// deployment mid-swap, a second container started against the same volume, an operator running a second
/// server "just to check something" — each pass their own guard and each start an SDK instance on the same
/// seed against the <b>same non-WAL SQLite file</b>. The SDK does not prevent that: a second connect on an
/// already-connected directory was measured succeeding in 2 ms, with no error and no warning. The result is
/// not merely the duplicate sweep the documentation used to describe; it is two writers on one journal-mode database
/// holding the record of a wallet with money on it.
/// </para>
/// <para>
/// So the plugin takes the lock the SDK does not. On both Unix and Windows a <see cref="FileStream"/> opened
/// with <see cref="FileShare.None"/> is an exclusive, kernel-held claim that is released when the process
/// exits — including when it is killed, which a lock file holding a pid would not be. It is also honoured
/// between two file descriptors <em>within</em> one process, which is what makes the guard testable without
/// spawning a second BTCPay.
/// </para>
/// <para>
/// <b>What it does not cover.</b> The claim is on the storage directory, so it catches exactly the case that
/// corrupts storage: two instances on one directory. Two BTCPay servers with <em>separate</em> data
/// directories configured with the same seed are still two live instances on one wallet — a duplicate-sweep
/// and lost-write hazard rather than a corruption one — and nothing local can see that. Nor does it cover a
/// storage provider that is not a directory at all; see <c>ISparkStorageProvider</c>.
/// </para>
/// </remarks>
internal sealed class SparkStorageLock : IDisposable
{
    /// <summary>
    /// Name of the lock file inside a store's storage directory.
    /// </summary>
    /// <remarks>
    /// Dot-prefixed and outside the SDK's own <c>&lt;network&gt;/&lt;identity&gt;/</c> namespacing, so it can
    /// never collide with anything the SDK writes, and a lock taken before the wallet is connected does not
    /// depend on knowing the identity the seed derives to.
    /// </remarks>
    internal const string FileName = ".spark-instance.lock";

    private readonly FileStream _stream;

    private SparkStorageLock(FileStream stream, string path)
    {
        _stream = stream;
        Path = path;
    }

    /// <summary>Absolute path of the lock file, for diagnostics.</summary>
    public string Path { get; }

    /// <summary>
    /// Claims <paramref name="directory"/>, creating it if needed.
    /// </summary>
    /// <param name="reason">
    /// Merchant-facing explanation when the claim failed. Null on success.
    /// </param>
    /// <returns>The held lock, or null when another process holds it or it could not be taken.</returns>
    public static SparkStorageLock? TryAcquire(string directory, out string? reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        var path = System.IO.Path.Combine(directory, FileName);
        reason = null;
        try
        {
            Directory.CreateDirectory(directory);

            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);

            try
            {
                // Written for a human reading the directory, never read back by anything: while the lock is
                // held no other process can open the file to read it, and once it is released the contents are
                // stale by definition. Truncated first so a shorter line cannot leave a longer one's tail.
                stream.SetLength(0);
                var note = string.Create(CultureInfo.InvariantCulture,
                    $"held by pid {Environment.ProcessId} ({Process.GetCurrentProcess().ProcessName}) since {DateTimeOffset.UtcNow:O}\n");
                stream.Write(Encoding.UTF8.GetBytes(note));
                stream.Flush();
            }
            catch (IOException)
            {
                // The note is a courtesy; failing to write it is not a reason to refuse to start a wallet.
            }

            return new SparkStorageLock(stream, path);
        }
        catch (IOException)
        {
            // The expected failure: another process holds the file. Deliberately not distinguished from other
            // IO errors on the open, because the safe response to both is the same — do not start a second
            // instance on storage this process cannot prove it owns.
            reason =
                "Another process is already using this store's Spark wallet storage. Two BTCPay instances "
                + "sharing one data directory would corrupt the wallet's database, so this store's wallet was "
                + "not started here. Run one BTCPay instance against this data directory, or give this one its "
                + "own.";
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            reason =
                "This store's Spark wallet storage could not be claimed, so the wallet was not started: "
                + ex.Message;
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            _stream.Dispose();
        }
        catch (IOException)
        {
            // Releasing a claim must never be the thing that fails a teardown.
        }

        // The file itself is deliberately left behind. Deleting it races: another process may already have
        // opened and locked the same path, and unlinking it would leave two processes each holding an
        // exclusive claim on a different inode under one name — which is precisely the state this exists to
        // prevent. It is a few dozen bytes.
    }
}
