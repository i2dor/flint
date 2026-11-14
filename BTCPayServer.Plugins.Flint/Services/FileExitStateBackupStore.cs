using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Exit-state backups as owner-only files per store under <c>&lt;DataDir&gt;/Plugins/Flint/exit-state/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The layout, per store</b> — every name starts with the store id and a dot, so one store's files are
/// found by prefix and never another's:
/// </para>
/// <list type="bullet">
/// <item><description><c>&lt;storeId&gt;.txt</c> — the automatic backup (the wallet's own latest export).</description></item>
/// <item><description><c>&lt;storeId&gt;.stamp.json</c> — its <see cref="ExitStateBackupStamp"/>.</description></item>
/// <item><description><c>&lt;storeId&gt;.pending-&lt;time&gt;-&lt;random&gt;.txt</c> — a backup waiting to be imported.</description></item>
/// <item><description><c>&lt;storeId&gt;.other-wallet-…</c>, <c>.removed-…</c>, <c>.foreign-…</c> — backups kept
/// aside, named for why; nothing in the plugin reads, replaces or removes them again. They are the
/// operator's to keep or delete.</description></item>
/// </list>
/// <para>
/// <b>A file, deliberately, not a column.</b> The backup is a multi-megabyte secret, and the store's
/// settings blob is deserialized on every settings read — a several-megabyte value in that column would be
/// carried through the settings cache, cloned on every read, and re-serialized on every save, for a blob
/// nothing reads except on connect. On disk it is read once and written atomically, and a settings write
/// that races a backup can neither lose nor half-overwrite it.
/// </para>
/// <para>
/// <b>A sibling of the SDK's per-store directory, not a child of it.</b> The SDK's directory is handed to
/// the SDK, so the plugin cannot assume it stays stable in shape or existence, and a file the plugin owns
/// next to one it does not is one it can create, restrict and clean up on its own terms.
/// </para>
/// <para>
/// <b>The write is <see cref="File.Move(string,string,bool)"/> over a temporary, always.</b> A
/// half-written backup that imports as a corrupt one is worse than no write at all: the operator would
/// believe their exit data was stored while it could never be restored. The rename is the only way to make
/// "either the old backup or the new one, never a mixture" true.
/// </para>
/// <para>
/// <b>One writer at a time per store, and every writer its own temporary.</b> The rename is atomic, but it
/// only publishes whatever the temporary holds — and a fixed temporary name shared by two concurrent writers
/// (the scheduled pass and the page's export, say) let both write into one file, so the rename could
/// publish a mixture of the two while the caller that finished last believed its own bytes were stored.
/// Each write therefore goes to a uniquely named temporary, under a per-store lock that also covers the
/// delete, so what a completed call reports is what is on disk.
/// </para>
/// <para>
/// <b>Nothing here interprets the content.</b> It is read and written as one string and never parsed,
/// validated or trimmed — see <see cref="IExitStateBackupStore"/> for why that is a hard rule.
/// </para>
/// </remarks>
public sealed class FileExitStateBackupStore : IExitStateBackupStore
{
    /// <summary>The directory every store's backup file lives in.</summary>
    private const string Subdirectory = "exit-state";

    /// <summary>Suffix of every in-flight temporary; see <see cref="SweepAbandonedTemporaries"/>.</summary>
    private const string TemporarySuffix = ".tmp";

    /// <summary>How old a temporary must be before a later write treats it as a crashed write's debris.</summary>
    private static readonly TimeSpan AbandonedTemporaryAge = TimeSpan.FromHours(1);

    /// <summary>The name part between the store id and the timestamp of a queued backup.</summary>
    private const string PendingMarker = ".pending-";

    /// <summary>The timestamp every generated name carries: sortable, UTC, and free of separators.</summary>
    private const string NameTimeFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    /// <summary>
    /// The only shape a pending id may have. Ids come back from a form, so anything else is refused before
    /// it is ever joined onto a path.
    /// </summary>
    private static readonly Regex PendingId = new(
        @"^\d{8}T\d{9}Z-[0-9a-f]{8}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly ILogger<FileExitStateBackupStore> _logger;

    /// <summary>Serialises every write and delete per store; see the type remarks.</summary>
    private readonly KeyedAsyncLock _locks = new();

    /// <summary>
    /// Stores whose files have had their modes checked this process. Once per store, because the check is
    /// only for files an earlier build left behind — everything this build writes is owner-only already.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _modesChecked = new(StringComparer.Ordinal);

    public FileExitStateBackupStore(
        IOptions<DataDirectories> dataDirectories,
        ILogger<FileExitStateBackupStore> logger)
    {
        _dataDirectories = dataDirectories;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> ReadAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(storeId);
        TightenExistingModes(storeId);

        // Absent is a fact and returns null; a directory that cannot be read or a file that cannot be
        // opened throws, because a caller that treated "unreadable" as "none stored" would clear a
        // merchant's only exit data on a transient error. Absent is learned from the open itself, not
        // from an Exists check before it: a clear landing between the two would otherwise turn "none
        // stored" into a FileNotFoundException for a caller that did nothing wrong.
        return await ReadIfPresentAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<DateTimeOffset?> TakenAtAsync(string storeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(WrittenAt(PathFor(storeId)));

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>The previous wallet's backup is kept, not replaced.</b> When the stored backup's stamp names a
    /// different wallet identity from this write's, the store is running a different seed than the one that
    /// wrote it — a re-provision — and that file is the old wallet's only device-proof exit data. It is moved
    /// to an <c>other-wallet</c> name first. An unknown identity on either side (an earlier build's file, an
    /// identity read that failed) is not evidence of a different wallet, so it is replaced as before.
    /// </para>
    /// <para>
    /// <b>The stamp is written before the content, deliberately.</b> A crash between the two then leaves a
    /// stamp whose digest does not match the file, which the connect reads as "not known to be held" and
    /// answers with one import of the file — harmless. The other order could leave a new file under the
    /// previous content's stamp, and a digest that happened to be absent would lose the identity that decides
    /// whether a later wallet's write sets the file aside.
    /// </para>
    /// </remarks>
    public async Task WriteAsync(
        string storeId, string backup, string? walletIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentNullException.ThrowIfNull(backup);

        var path = PathFor(storeId);
        using var held = await _locks.AcquireAsync(storeId, cancellationToken).ConfigureAwait(false);
        EnsureDirectory();
        SweepAbandonedTemporaries(storeId);

        var previous = await ReadStampUnlockedAsync(storeId, cancellationToken).ConfigureAwait(false);
        if (previous?.WalletIdentity is { } previousIdentity
            && walletIdentity is not null
            && !string.Equals(previousIdentity, walletIdentity, StringComparison.Ordinal)
            && File.Exists(path))
        {
            var aside = MoveAside(storeId, path, ExitStateBackupSetAside.OtherWallet);
            _logger.LogWarning(
                "Store {StoreId}: its stored exit-state backup was written by a different wallet than the one "
                + "this store now runs, so it was kept aside as {File} instead of being replaced. It is the "
                + "previous wallet's exit data; keep it for as long as that wallet's seed may still hold funds",
                storeId, aside);
        }

        await WriteAtomicallyAsync(
                StampPathFor(storeId),
                JsonSerializer.Serialize(new StampFile(ExitStateBackupStamp.HashOf(backup), walletIdentity)),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteAtomicallyAsync(path, backup, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(storeId);
        using var held = await _locks.AcquireAsync(storeId, cancellationToken).ConfigureAwait(false);
        DeleteIfPresent(StampPathFor(storeId));
        return DeleteIfPresent(path);
    }

    /// <inheritdoc />
    public async Task<ExitStateBackupStamp?> ReadStampAsync(string storeId, CancellationToken cancellationToken = default)
    {
        ValidateStoreId(storeId);
        return await ReadStampUnlockedAsync(storeId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> StampAsync(
        string storeId, string backup, string? walletIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var path = PathFor(storeId);
        using var held = await _locks.AcquireAsync(storeId, cancellationToken).ConfigureAwait(false);

        // Under the lock and against the file as it is now, not as the caller read it: a write that landed
        // since would otherwise be described by a stamp for the bytes it replaced.
        var current = await ReadIfPresentAsync(path, cancellationToken).ConfigureAwait(false);
        if (current is null || !string.Equals(current, backup, StringComparison.Ordinal))
            return false;

        await WriteAtomicallyAsync(
                StampPathFor(storeId),
                JsonSerializer.Serialize(new StampFile(ExitStateBackupStamp.HashOf(backup), walletIdentity)),
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<string?> SetAsideAsync(
        string storeId, ExitStateBackupSetAside reason, CancellationToken cancellationToken = default)
    {
        var path = PathFor(storeId);
        using var held = await _locks.AcquireAsync(storeId, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(path))
            return null;

        var aside = MoveAside(storeId, path, reason);
        DeleteIfPresent(StampPathFor(storeId));
        return aside;
    }

    /// <inheritdoc />
    public async Task<string> KeepAsideAsync(
        string storeId, string backup, ExitStateBackupSetAside reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ValidateStoreId(storeId);
        using var held = await _locks.AcquireAsync(storeId, cancellationToken).ConfigureAwait(false);
        EnsureDirectory();

        var name = AsideName(storeId, reason);
        await WriteAtomicallyAsync(Path.Combine(StorageDirectory(), name), backup, cancellationToken)
            .ConfigureAwait(false);
        return name;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PendingExitStateBackup>> ListPendingAsync(
        string storeId, CancellationToken cancellationToken = default)
    {
        ValidateStoreId(storeId);
        TightenExistingModes(storeId);
        return Task.FromResult(ListPendingUnlocked(storeId));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Identical content already queued answers that entry's id instead of a second copy: a paste submitted
    /// twice, or a failed import at every connect, must not grow the queue by a multi-megabyte file each time.
    /// </remarks>
    public async Task<string> AddPendingAsync(string storeId, string backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ValidateStoreId(storeId);
        using var held = await _locks.AcquireAsync(storeId, cancellationToken).ConfigureAwait(false);
        EnsureDirectory();
        SweepAbandonedTemporaries(storeId);

        foreach (var entry in ListPendingUnlocked(storeId))
        {
            var queued = await ReadIfPresentAsync(PendingPathFor(storeId, entry.Id), cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(queued, backup, StringComparison.Ordinal))
                return entry.Id;
        }

        var id = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow.ToString(NameTimeFormat, CultureInfo.InvariantCulture)}-{RandomSuffix(8)}");
        await WriteAtomicallyAsync(PendingPathFor(storeId, id), backup, cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc />
    public async Task<string?> ReadPendingAsync(string storeId, string id, CancellationToken cancellationToken = default)
    {
        TightenExistingModes(storeId);
        return await ReadIfPresentAsync(PendingPathFor(storeId, id), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Stream?> OpenReadPendingAsync(string storeId, string id, CancellationToken cancellationToken = default)
    {
        TightenExistingModes(storeId);
        return Task.FromResult(OpenIfPresent(PendingPathFor(storeId, id)));
    }

    /// <inheritdoc />
    public async Task<bool> DeletePendingAsync(string storeId, string id, CancellationToken cancellationToken = default)
    {
        var path = PendingPathFor(storeId, id);
        using var held = await _locks.AcquireAsync(storeId, cancellationToken).ConfigureAwait(false);
        return DeleteIfPresent(path);
    }

    /// <inheritdoc />
    public async Task<string?> SetAsidePendingAsync(
        string storeId, string id, ExitStateBackupSetAside reason, CancellationToken cancellationToken = default)
    {
        var path = PendingPathFor(storeId, id);
        using var held = await _locks.AcquireAsync(storeId, cancellationToken).ConfigureAwait(false);
        return File.Exists(path) ? MoveAside(storeId, path, reason) : null;
    }

    /// <inheritdoc />
    public Task<Stream?> OpenReadAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(storeId);
        TightenExistingModes(storeId);

        // Absent answers null, as ReadAsync does, and a file that cannot be opened throws for the same
        // reason: the caller redirects on "none stored" and reports "unreadable", and fusing the two
        // would tell an operator who pressed Download that they have no backup. Opening is a handle, not
        // the content, so there is nothing to await.
        return Task.FromResult<Stream?>(OpenIfPresent(path));
    }

    /// <summary>
    /// The directory this store owns: a sibling of the SDK's per-store storage, created and restricted
    /// exactly as that one is.
    /// </summary>
    internal string StorageDirectory() => Path.Combine(
        _dataDirectories.Value.DataDir, "Plugins", Constants.WorkDirName, Subdirectory);

    /// <summary>One store's automatic backup file.</summary>
    internal string PathFor(string storeId)
    {
        ValidateStoreId(storeId);
        return Path.Combine(StorageDirectory(), storeId + ".txt");
    }

    /// <summary>The stamp beside a store's automatic backup.</summary>
    internal string StampPathFor(string storeId)
    {
        ValidateStoreId(storeId);
        return Path.Combine(StorageDirectory(), storeId + ".stamp.json");
    }

    /// <summary>One queued backup's file. Refuses an id that is not one this class generated.</summary>
    internal string PendingPathFor(string storeId, string id)
    {
        ValidateStoreId(storeId);
        if (string.IsNullOrEmpty(id) || !PendingId.IsMatch(id))
            throw new ArgumentException("Not a pending exit-state backup id.", nameof(id));

        return Path.Combine(StorageDirectory(), storeId + PendingMarker + id + ".txt");
    }

    /// <summary>The queue, oldest first. Ids sort by time because they start with it.</summary>
    private IReadOnlyList<PendingExitStateBackup> ListPendingUnlocked(string storeId)
    {
        var directory = StorageDirectory();
        if (!Directory.Exists(directory))
            return [];

        var prefix = storeId + PendingMarker;
        var entries = new List<PendingExitStateBackup>();
        foreach (var file in Directory.EnumerateFiles(directory, prefix + "*.txt"))
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".txt", StringComparison.Ordinal))
                continue;

            var id = name[prefix.Length..^".txt".Length];
            if (!PendingId.IsMatch(id))
                continue;

            var info = new FileInfo(file);
            if (!info.Exists)
                continue;

            var storedAt = DateTime.TryParseExact(
                id[..id.IndexOf('-', StringComparison.Ordinal)], NameTimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? new DateTimeOffset(parsed, TimeSpan.Zero)
                : new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            entries.Add(new PendingExitStateBackup(id, storedAt, info.Length));
        }

        return entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>The stamp, or null when there is none or it cannot be read — unknown provenance, never a throw.</summary>
    private async Task<ExitStateBackupStamp?> ReadStampUnlockedAsync(string storeId, CancellationToken cancellationToken)
    {
        try
        {
            var text = await ReadIfPresentAsync(StampPathFor(storeId), cancellationToken).ConfigureAwait(false);
            if (text is null)
                return null;

            var stamp = JsonSerializer.Deserialize<StampFile>(text);
            return stamp is { Sha256.Length: > 0 }
                ? new ExitStateBackupStamp(stamp.Sha256, stamp.WalletIdentity)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: the stamp beside its exit-state backup could not be read, so the backup is "
                + "treated as one of unknown provenance", storeId);
            return null;
        }
    }

    /// <summary>
    /// Renames a file to a set-aside name and answers that name. Caller holds the store's lock.
    /// </summary>
    private string MoveAside(string storeId, string path, ExitStateBackupSetAside reason)
    {
        var name = AsideName(storeId, reason);
        File.Move(path, Path.Combine(StorageDirectory(), name), overwrite: false);
        return name;
    }

    /// <summary>A fresh set-aside name: the store, why, when, and a random suffix so two in one millisecond differ.</summary>
    private static string AsideName(string storeId, ExitStateBackupSetAside reason)
    {
        var slug = reason switch
        {
            ExitStateBackupSetAside.OtherWallet => "other-wallet",
            ExitStateBackupSetAside.Removed => "removed",
            ExitStateBackupSetAside.Foreign => "foreign",
            _ => "kept"
        };

        return string.Create(CultureInfo.InvariantCulture,
            $"{storeId}.{slug}-{DateTime.UtcNow.ToString(NameTimeFormat, CultureInfo.InvariantCulture)}-{RandomSuffix(4)}.txt");
    }

    private static string RandomSuffix(int hexDigits) =>
        Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(hexDigits / 2));

    /// <summary>The stamp file's shape. Property names are the on-disk format.</summary>
    private sealed record StampFile(string Sha256, string? WalletIdentity);

    /// <summary>
    /// Refuses a store id that could place a file outside the owner-only directory.
    /// </summary>
    /// <remarks>
    /// Every path in the plugin's own layout is built from a store id BTCPay generated. The id is not
    /// attacker-controlled — it is a store's own identifier, resolved from an authorised request — but a
    /// store id containing a path separator would place the file outside the owner-only directory this
    /// class exists to keep it in, so it is refused rather than reasoned about.
    /// </remarks>
    private static void ValidateStoreId(string storeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        if (storeId.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || storeId.Contains(Path.DirectorySeparatorChar)
            || storeId.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                $"A store id cannot be used as a file name because it contains a path separator: {storeId}",
                nameof(storeId));
        }
    }

    /// <summary>A file's text, or null when it is absent — learned from the open, never a check before it.</summary>
    private static async Task<string?> ReadIfPresentAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// A shared read handle on a file, or null when it is absent — learned from the open, never a check
    /// before it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FileShare.Read</c>: opening a download must not exclude anything else from the file — a second
    /// download, or a <see cref="ReadAsync"/> comparing a paste — and no writer ever opens the target,
    /// because a write reaches it only by renaming a temporary over it. That replacement is exactly the
    /// concurrent write this share pattern tolerates: on POSIX a rename swaps the directory entry while an
    /// already-open handle keeps reading the old inode to a clean end, so a pass that refreshes the backup
    /// halfway through a download serves one whole file or the other, never a mixture, and never a sharing
    /// error. <c>FileShare.Delete</c> is the same promise on Windows, where a rename over — or a clear of — a
    /// file somebody holds open is otherwise refused outright.
    /// </para>
    /// <para>
    /// Absent is learned from the open itself: a clear that lands between an Exists check and the open
    /// used to surface as a <see cref="FileNotFoundException"/> out of a download.
    /// </para>
    /// </remarks>
    private static Stream? OpenIfPresent(string path)
    {
        try
        {
            return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>A file's last write, UTC, or null when it is absent — including absent by the time it was asked.</summary>
    /// <remarks>
    /// The filesystem's write time is the answer, UTC so the offset is unambiguous. What a reader wants from
    /// it is "how stale is this?", and only a UTC stamp can be compared against a store's own monotonic pass
    /// times without knowing the host's zone. Read through one <see cref="FileInfo"/> rather than an Exists
    /// check and a separate time read: a file removed between the two reports the platform's 1601 sentinel
    /// as its write time, which would render as a backup taken four centuries ago.
    /// </remarks>
    private static DateTimeOffset? WrittenAt(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null;
    }

    /// <summary>Deletes a file, answering whether there was one. Caller holds the store's lock.</summary>
    private static bool DeleteIfPresent(string path)
    {
        if (!File.Exists(path))
            return false;

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> through a uniquely named, owner-only
    /// temporary renamed over the target. Caller holds the store's lock.
    /// </summary>
    private async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        // Unique per call, never a fixed "<path>.tmp": see the type remarks for the mixture a shared name
        // allowed. The lock already excludes this process's other writers; the unique name is what keeps a
        // second process pointed at the same data directory out of this call's temporary.
        var temporary = $"{path}.{Guid.NewGuid():N}{TemporarySuffix}";

        // Written to a sibling rather than in place, then renamed over the target. The overwrite flag is
        // what makes the move a replace: without it the second backup for a store would throw on the
        // existing file.
        //
        // The guard covers the write as well as the rename, because a half-written backup is debris
        // whichever of the two failed: a full disk, an IO error mid-write, or a cancellation part way
        // through a multi-megabyte blob all leave a partial copy of the secret behind, and nothing else
        // ever removes it. The target is only ever reached by the rename, so a failed pass here leaves the
        // backup that was stored before it exactly as it was — old or new, never a mixture.
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);

            // Owner-only before it is ever a stored backup, never afterwards: the rename carries the
            // temp's mode onto the target, so this is the only moment the mode can be set without a
            // window in which the file sits under its final name at the umask — readable by every
            // account on the host for as long as a correction takes. The 0700 directory is the first
            // line of defence for this blob; a file that is itself 0600 means a second one even against
            // a reader that reaches the directory some other way — the same account running BTCPay under
            // a different path, an operator's own backup tool reading the tree.
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // The temp has no value once the write or the replace failed, and leaving it beside every
            // store's real backup is how a directory of secrets accumulates debris.
            TryDelete(temporary);
            throw;
        }
    }

    private void EnsureDirectory()
    {
        var directory = StorageDirectory();

        // Owner-only from the first instant it exists, never created at the umask and restricted
        // afterwards — the same pairing FileSparkStorageProvider.GetTarget applies to the SDK's directory,
        // so a backup is never momentarily world-readable in the window before a chmod.
        SparkDirectoryPermissions.CreateOwnerOnly(directory);
        SparkDirectoryPermissions.RestrictToOwner(directory, _logger);
    }

    /// <summary>
    /// Removes this store's temporaries left by a write that never finished — a process killed mid-write.
    /// </summary>
    /// <remarks>
    /// Caller holds the store's lock, so no temporary of this process is in flight for the store; the age
    /// threshold is what spares one belonging to another process on the same data directory. Housekeeping,
    /// and never a reason to fail the write it runs ahead of.
    /// </remarks>
    private void SweepAbandonedTemporaries(string storeId)
    {
        try
        {
            var cutoff = DateTime.UtcNow - AbandonedTemporaryAge;
            foreach (var temporary in Directory.EnumerateFiles(
                         StorageDirectory(), storeId + ".*" + TemporarySuffix))
            {
                if (File.GetLastWriteTimeUtc(temporary) < cutoff)
                    TryDelete(temporary);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Store {StoreId}: could not look for abandoned temporary exit-state backups", storeId);
        }
    }

    /// <summary>
    /// Brings a store's backup files (and the directory) to owner-only if an earlier build left them wider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every write this build makes is owner-only from the temporary onward, but a file an earlier build
    /// wrote keeps its mode until its next <em>changed</em> write — which for an idle wallet may be never,
    /// because an unchanged export is not rewritten. A file readable by the whole host inside a directory
    /// that is not is still a secret one directory permission away from disclosure, so the first read of
    /// each store in a process checks every file of that store, and tightens any with group or other bits.
    /// </para>
    /// <para>
    /// Best effort and never throws: a filesystem that carries no Unix modes is a weaker host, not a reason
    /// to refuse to read the backup. Once per store per process, because a mode this process set stays set.
    /// </para>
    /// </remarks>
    private void TightenExistingModes(string storeId)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || !_modesChecked.TryAdd(storeId, 0))
            return;

        try
        {
            var directory = StorageDirectory();
            if (!Directory.Exists(directory))
                return;

            SparkDirectoryPermissions.RestrictToOwner(directory, _logger);

            const UnixFileMode wider =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

            foreach (var file in Directory.EnumerateFiles(directory, storeId + ".*"))
            {
                if ((File.GetUnixFileMode(file) & wider) == 0)
                    continue;

                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                _logger.LogInformation(
                    "Store {StoreId}: restricted {File} to its owner; an earlier version of the plugin had "
                    + "left it readable by other accounts on this host",
                    storeId, Path.GetFileName(file));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: could not restrict its exit-state backup files to their owner", storeId);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            // Nothing names the content: the temp holds a backup, and a failure to delete it is the
            // operator's cleanup problem, not a reason to put the secret in a log line.
            _logger.LogWarning(ex,
                "Could not delete the temporary exit-state backup at {Path}", path);
        }
    }
}

/// <summary>
/// An async mutual-exclusion lock per key, for the exit-state backup seams.
/// </summary>
/// <remarks>
/// One <see cref="SemaphoreSlim"/> per key for the life of the process, deliberately never removed: the keys
/// are store ids, of which a server has a handful, and removing an entry safely while another caller may be
/// about to wait on it is a protocol of its own for a saving of a few bytes per store.
/// </remarks>
internal sealed class KeyedAsyncLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>Waits for the key's lock; disposing the result releases it.</summary>
    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Releaser(SemaphoreSlim gate) => _gate = gate;

        // Idempotent: a double dispose must not release a lock some other caller now holds.
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
