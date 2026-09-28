using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The stored unilateral-exit state backups of a store, each read and written as one opaque string.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for:</b> the transactions an exit is built from live only in the SDK's local storage.
/// While the Spark operators are reachable they can be fetched again; when that storage is gone and the
/// operators are not, they cannot be recovered from anywhere and the leaves they cover can no longer be
/// exited. The blobs this store keeps are the copies that survive the device.
/// </para>
/// <para>
/// <b>Two kinds of backup, kept apart, because they are owed different things.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// The <b>automatic</b> backup is this wallet's own latest export, taken by <see cref="ExitStateBackupTask"/>
/// (or the page's Export button). It is a copy of what the wallet already holds, so each new export may
/// replace it — that is the point of it. It carries a <see cref="ExitStateBackupStamp"/>: a digest of its
/// content and the identity of the wallet that wrote it, which is how the connect knows not to import the
/// wallet's own export back into it, and how a different wallet's first write knows to keep the previous
/// wallet's backup aside instead of overwriting it.
/// </description></item>
/// <item><description>
/// A <b>pending</b> backup is one the wallet has not yet been shown to hold: a blob an operator pasted, or a
/// stored backup whose import at connect failed. It is kept in a slot of its own that no automatic pass
/// ever writes, and it stays until an import of it has returned — only then is it removed (or, when every
/// leaf in it belonged to another wallet, kept aside under a name that says so). Before this split there
/// was one file, and the next automatic pass — due within minutes of a paste, and at once after a restart —
/// replaced a pasted backup with the wallet's own export before anything had imported it.
/// </description></item>
/// </list>
/// <para>
/// <b>They are secrets and every implementation must treat them as such.</b> A backup carries every leaf of
/// the wallet and the transactions under them, which discloses the balance, how it is split, and what the
/// wallet has received and spent. It must never be logged, echoed in an exception message, or returned to a
/// page — an implementation may log the store id, a file name and a length, and nothing of the content.
/// </para>
/// <para>
/// <b>Content is <em>not</em> validated on the way in, anywhere in this plugin.</b> The encoding is the SDK's
/// own and the SDK is the only thing that can judge it; a check invented here would reject a valid backup
/// from a future SDK, and a rejected backup is a lost exit. The only bound any caller applies is a length cap
/// against a wrong paste.
/// </para>
/// <para>
/// This is the only seam through which a backup is read or written. Nothing else opens these files —
/// including nothing that parses them.
/// </para>
/// </remarks>
public interface IExitStateBackupStore
{
    /// <summary>
    /// The automatic backup for a store, or null when none is stored.
    /// </summary>
    /// <remarks>
    /// A genuine IO failure is not swallowed into a null: a read that cannot be answered is a different
    /// fact from one that answered "absent", and the caller decides what to do with it.
    /// </remarks>
    Task<string?> ReadAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the automatic backup as a readable stream, or null when none is stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The read for a caller that only moves the content.</b> A download wants the file's bytes on a
    /// response and never looks at them; answering it with <see cref="ReadAsync"/> plus an encoding of the
    /// result puts a multi-megabyte secret in memory twice to produce a single pass-through copy.
    /// </para>
    /// <para>
    /// Like <see cref="ReadAsync"/>, an open that cannot be answered is not swallowed into a null:
    /// "absent" is a fact the caller redirects with; "unreadable" is a fault the caller surfaces.
    /// </para>
    /// </remarks>
    Task<Stream?> OpenReadAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// When the automatic backup was written, or null when none is stored.
    /// </summary>
    Task<DateTimeOffset?> TakenAtAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the wallet's own export as the automatic backup, atomically replacing the previous one, and
    /// stamps it with <paramref name="walletIdentity"/>.
    /// </summary>
    /// <param name="walletIdentity">
    /// The identity public key of the wallet that exported <paramref name="backup"/>, or null when it could
    /// not be read.
    /// </param>
    Task WriteAsync(string storeId, string backup, string? walletIdentity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the automatic backup and its stamp. Returns whether there was one to remove.
    /// </summary>
    Task<bool> DeleteAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the automatic backup was stamped with when it was written, or null when it carries no stamp —
    /// written by an earlier build, placed by hand, or no backup at all.
    /// </summary>
    /// <remarks>A stamp that cannot be read or parsed answers null: an unknown provenance, never a throw.</remarks>
    Task<ExitStateBackupStamp?> ReadStampAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the wallet with <paramref name="walletIdentity"/> now holds the automatic backup's
    /// content — after an import of it returned — so the next connect does not import it again.
    /// </summary>
    /// <param name="backup">
    /// The content the caller imported. The stamp is written only when the stored file still holds exactly
    /// this, because a stamp describing different bytes is a claim about a file that is not there.
    /// </param>
    /// <returns>Whether the stamp was written.</returns>
    Task<bool> StampAsync(
        string storeId, string backup, string? walletIdentity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the automatic backup aside under a name that records why, so nothing later replaces it.
    /// Returns the file name it now has, or null when there was none.
    /// </summary>
    Task<string?> SetAsideAsync(
        string storeId, ExitStateBackupSetAside reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps <paramref name="backup"/> aside under a name that records why — a blob with no slot of its own
    /// that must not be lost, such as the deprecated settings copy of a wallet the store no longer runs.
    /// Returns the file name.
    /// </summary>
    Task<string> KeepAsideAsync(
        string storeId, string backup, ExitStateBackupSetAside reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every backup waiting to be imported into this store's wallet, oldest first.
    /// </summary>
    Task<IReadOnlyList<PendingExitStateBackup>> ListPendingAsync(
        string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues <paramref name="backup"/> for import, unless an identical one is already queued. Returns the
    /// id of the queued entry either way.
    /// </summary>
    Task<string> AddPendingAsync(string storeId, string backup, CancellationToken cancellationToken = default);

    /// <summary>One queued backup's content, or null when it is no longer queued.</summary>
    Task<string?> ReadPendingAsync(string storeId, string id, CancellationToken cancellationToken = default);

    /// <summary>One queued backup as a stream, or null when it is no longer queued.</summary>
    Task<Stream?> OpenReadPendingAsync(string storeId, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a queued backup — only ever after an import of it returned, or on the operator's clear.
    /// Returns whether it was there.
    /// </summary>
    Task<bool> DeletePendingAsync(string storeId, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a queued backup aside under a name that records why. Returns the new file name, or null when it
    /// was no longer queued.
    /// </summary>
    Task<string?> SetAsidePendingAsync(
        string storeId, string id, ExitStateBackupSetAside reason, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where an automatic backup came from: a digest of its content and the wallet that wrote it.
/// </summary>
/// <param name="ContentSha256">
/// Lower-case hex SHA-256 of the backup's UTF-8 bytes. Compared for equality only and never logged: it
/// authenticates a secret. Stored in a file beside the backup, inside the same owner-only directory.
/// </param>
/// <param name="WalletIdentity">
/// The SDK identity public key of the wallet that exported (or imported) this content, or null when it was
/// not known at write time. Public information — the SDK logs it, and so does the connect.
/// </param>
public sealed record ExitStateBackupStamp(string ContentSha256, string? WalletIdentity)
{
    /// <summary>The digest a stamp records for <paramref name="content"/>.</summary>
    public static string HashOf(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>Whether this stamp describes exactly <paramref name="content"/>.</summary>
    public bool Describes(string content) =>
        string.Equals(ContentSha256, HashOf(content), StringComparison.Ordinal);
}

/// <summary>One backup waiting to be imported.</summary>
/// <param name="Id">Opaque, and only ever handed back to the same store.</param>
/// <param name="StoredAt">When it was queued.</param>
/// <param name="Length">Its size in bytes on disk — for the page, never its content.</param>
public sealed record PendingExitStateBackup(string Id, DateTimeOffset StoredAt, long Length);

/// <summary>Why a backup was moved aside rather than replaced or removed.</summary>
public enum ExitStateBackupSetAside
{
    /// <summary>A different wallet now runs on this store (a new seed), and this was the previous one's.</summary>
    OtherWallet,

    /// <summary>Flint was removed from the store; kept because the seed may still control the funds.</summary>
    Removed,

    /// <summary>An import of it returned with every leaf belonging to another wallet.</summary>
    Foreign
}
