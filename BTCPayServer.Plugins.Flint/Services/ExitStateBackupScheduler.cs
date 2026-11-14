using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Decides when a store's automatic exit-state backup is due, and remembers what was last written.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure decision logic, and deliberately singleton-wide rather than per pass.</b> Every method takes
/// its <c>now</c> as an argument and touches no clock, no file and no SDK, so the cadence rules below are
/// testable without a wallet, a timer or a filesystem. It is one instance for the process because the
/// pending-request marks arrive on per-store event-consumer loops and are consumed by the scheduled pass;
/// two instances would mean the pass never sees the requests.
/// </para>
/// <para>
/// <b>Two rules, because two different failures are real.</b> <see cref="DebounceInterval"/> answers
/// "money just moved, take a fresh copy — but not forty"; <see cref="SafetyNetInterval"/> answers "the
/// events said nothing, take one anyway". Either one alone is wrong: see each field.
/// </para>
/// <para>
/// <b>The content hash is what keeps the writes rare.</b> Two passes over a quiet wallet export byte-for-byte
/// identical state, and a multi-megabyte write to say "nothing changed" is a write that can fail, can be
/// interrupted, and earns nothing. The scheduler holds the hash of the last content written per store —
/// never the content, which is a secret — and reports whether a fresh export matches it. On restart it
/// starts empty, and the caller seeds it from the stored file (via <see cref="NoteStoredContent"/>); the
/// scheduler never reads the file itself, because deciding what is stored is the store seam's job.
/// </para>
/// </remarks>
public sealed class ExitStateBackupScheduler
{
    /// <summary>
    /// How long a refresh request waits before the next pass may act on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every event that requests a refresh arrives as a stream, not a singleton: an on-chain deposit
    /// burst lands as a <c>ClaimedDeposits</c> per claim plus a payment event per credit, and the SDK's
    /// <c>UnilateralExitStateChanged</c> fires per leaf whose exit data it completes or rebuilds — which
    /// the SDK's own background leaf optimisation does in rounds, independent of anything the merchant did.
    /// Every export is a live SDK call producing a multi-megabyte blob and an equally large write, so
    /// reacting to each event individually would turn a deposit burst into N full exports of state that
    /// differs, at most, by the last claim — and would do it on the store's own event loop's time budget.
    /// </para>
    /// <para>
    /// Two minutes, and it is a floor rather than a guess: the burst is over in seconds, and the exit data
    /// is only worth its latest bytes to an operator who is already in the worst week of this wallet's
    /// life. A later event during the wait does not push the deadline — the coalescing is what this is
    /// for, and a refresh that slides would starve under exactly the busy wallet that needs backups most.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan DebounceInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The longest a store may go without a backup pass, whatever the event stream said.
    /// </summary>
    /// <remarks>
    /// This is the safety net only an hour because the request channel above cannot be trusted to carry a
    /// request at all: the SDK event channel is bounded, its listener reports drops rather than
    /// backpressuring, and the codebase already documents the stream being unreliable in both directions —
    /// a completed receive has been observed emitting only <c>PaymentPending</c>. A refresh mark that was
    /// never delivered must not mean a store silently never gets another backup. One hour also bounds how
    /// far a backup can lag the money: at most an hour's leaves exist in the wallet with no copy anywhere
    /// that survives the device.
    /// </remarks>
    public static readonly TimeSpan SafetyNetInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// One store's marks. Immutable; the dictionary swaps whole records under
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/>'s own atomicity.
    /// </summary>
    /// <param name="RequestedAt">When an unserved refresh arrived; null when none is pending.</param>
    /// <param name="LastPassAt">When a pass last reported on this store's state, taken or skipped.</param>
    /// <param name="ContentHash">
    /// Hash of the last content believed stored, or null while nothing is believed stored — including the
    /// state right after a restart, before the caller has seeded it.
    /// </param>
    /// <param name="Generation">
    /// How many refresh requests this store has ever received. Moves on every request, pending or not, so
    /// a pass can tell whether one arrived while it was exporting — see <see cref="BeginPass"/>.
    /// </param>
    private sealed record Marks(
        DateTimeOffset? RequestedAt,
        DateTimeOffset? LastPassAt,
        string? ContentHash,
        long Generation = 0);

    private readonly ConcurrentDictionary<string, Marks> _marks = new();

    /// <summary>
    /// Records that something changed and a fresh backup will be worth taking soon.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never blocks and never throws: it is called from a store's event-consumer path, where the rule for
    /// anything reached from an SDK callback is that it may not fail or stall. There is deliberately no
    /// argument check that could throw — a degenerate store id is dropped, because losing the mark is a
    /// missed backup and throwing would cost the store's whole event loop, which is the worse half.
    /// </para>
    /// <para>
    /// An empty request slot records <paramref name="storeId"/> with <c>now</c>; a pending one is left at
    /// its original time. Overwriting a pending time with a later one is what a <em>throttle</em> does and
    /// would let a steady stream of events defer the backup forever; this is a debounce that coalesces.
    /// </para>
    /// </remarks>
    public void RequestRefresh(string storeId)
    {
        if (string.IsNullOrEmpty(storeId))
            return;

        _marks.AddOrUpdate(
            storeId,
            _ => new Marks(DateTimeOffset.UtcNow, null, null, 1),
            (_, current) => current.RequestedAt is null
                ? current with { RequestedAt = DateTimeOffset.UtcNow, Generation = current.Generation + 1 }
                : current with { Generation = current.Generation + 1 });
    }

    /// <summary>
    /// Marks the start of a pass over one store, before its export: what the pass will be able to serve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A request that arrives during an export is not served by it.</b> The export reads the wallet at
    /// some moment inside the call, and an event the SDK emitted after that moment describes a change the
    /// export may not contain — the refresh it asked for is still owed. Clearing every request when the pass
    /// ends, as this class once did, dropped exactly those: an exit-state change landing during a
    /// multi-second export left the backup stale until the safety net, an hour later.
    /// </para>
    /// <para>
    /// The token carries the request generation at this moment, and the real wall clock, because that is
    /// the clock <see cref="RequestRefresh"/> stamps with: <see cref="MarkTaken"/> and
    /// <see cref="MarkSkipped"/> clear the pending request only when no request arrived after this call,
    /// and otherwise leave it pending from no later than this moment — never later than the request
    /// itself, so the debounce it waits out is never longer than its own.
    /// </para>
    /// </remarks>
    public ExitStateBackupPass BeginPass(string storeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        var generation = _marks.TryGetValue(storeId, out var marks) ? marks.Generation : 0;
        return new ExitStateBackupPass(generation, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Whether a backup is due for a store: a request has been pending for at least
    /// <see cref="DebounceInterval"/>, or nothing has been taken for at least <see cref="SafetyNetInterval"/>.
    /// </summary>
    /// <remarks>
    /// A store the scheduler has never seen is due immediately — a wallet that has been up since before
    /// this process started has no backup, and waiting a further safety-net interval after every restart
    /// for the one thing restarts are a risk to would leave the window open exactly where it matters.
    /// </remarks>
    public bool ShouldTake(string storeId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        if (!_marks.TryGetValue(storeId, out var marks))
            return true;

        if (marks.RequestedAt is { } requested && now - requested >= DebounceInterval)
            return true;

        return marks.LastPassAt is not { } last || now - last >= SafetyNetInterval;
    }

    /// <summary>
    /// When a pending refresh request arrived, or null when none is.
    /// </summary>
    /// <remarks>
    /// Exists for the same reason every other decision method takes its <c>now</c>: the one clock this
    /// class reads on its own is the real one inside <see cref="RequestRefresh"/>, and a caller — today,
    /// the tests — that wants to reason about the debounce needs the moment that was actually recorded,
    /// not a second, differently-timed reading of the wall. It is also the direct read of the coalescing
    /// rule: two requests, one timestamp.
    /// </remarks>
    public DateTimeOffset? PendingSince(string storeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        return _marks.TryGetValue(storeId, out var marks) ? marks.RequestedAt : null;
    }

    /// <summary>
    /// Records that a backup was taken: serves the requests that predate <paramref name="pass"/> and
    /// restarts the safety net.
    /// </summary>
    public void MarkTaken(string storeId, DateTimeOffset now, ExitStateBackupPass pass) =>
        MarkPass(storeId, now, pass);

    /// <summary>
    /// Records that a pass reported on this store's state and found the stored copy unchanged, so nothing
    /// was written. Serves the requests that predate <paramref name="pass"/> and restarts the safety net,
    /// exactly like a take — the pass happened and the state is known current as of its export.
    /// </summary>
    public void MarkSkipped(string storeId, DateTimeOffset now, ExitStateBackupPass pass) =>
        MarkPass(storeId, now, pass);

    /// <summary>
    /// Records that a pass ran and learned nothing worth acting on: the safety net restarts, and
    /// nothing else moves — a pending request stays pending, and no stored content is noted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the pass an empty export produces: the wallet was asked and said nothing, so the pass has
    /// no report on the state to serve a request with, and no bytes to update a belief about the file
    /// with. Recording it anyway is the point — a wallet with no exit state yet has a null
    /// <c>LastPassAt</c>, which reads as due on every pass, so an unrecorded empty export leaves the
    /// task spending a live SDK call on that store every minute, forever. With the pass on the record,
    /// the next ask comes at <see cref="SafetyNetInterval"/>: the cadence an answering-nothing wallet
    /// deserves.
    /// </para>
    /// <para>
    /// <see cref="MarkSkipped"/> is the contrast, and the reason this is not the same call: a skip is a
    /// report on the wallet's state — the export came back and matched what is stored — so it serves
    /// the pending request. An idle pass is no such report, and a request a real event earned stays
    /// owed until a pass can serve it.
    /// </para>
    /// </remarks>
    public void MarkIdlePass(string storeId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        _marks.AddOrUpdate(
            storeId,
            _ => new Marks(null, now, null),
            (_, current) => current with { LastPassAt = now });
    }

    /// <summary>
    /// Whether the scheduler knows what content is believed stored for a store.
    /// </summary>
    /// <remarks>
    /// False right after a restart — the file may hold a year of backups while the scheduler holds nothing.
    /// The caller uses this to seed from the file (<see cref="NoteStoredContent"/>) before it asks
    /// <see cref="ContentUnchanged"/> whether a fresh export is worth writing.
    /// </remarks>
    public bool KnowsStoredContent(string storeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        return _marks.TryGetValue(storeId, out var marks) && marks.ContentHash is not null;
    }

    /// <summary>
    /// Records the bytes the caller believes are now stored, hashing them and keeping only the hash.
    /// </summary>
    /// <remarks>
    /// Called by the caller with the file's content when seeding after a restart, and again with the
    /// content it just wrote after a successful write. Null content means "the file is absent", which is
    /// not the same as unknown: a subsequent export of anything is a change worth writing.
    /// </remarks>
    public void NoteStoredContent(string storeId, string? content)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var hash = content is null ? null : Hash(content);
        _marks.AddOrUpdate(
            storeId,
            _ => new Marks(null, null, hash),
            (_, current) => current with { ContentHash = hash });
    }

    /// <summary>
    /// Whether a fresh export is byte-identical to the content believed stored.
    /// </summary>
    /// <remarks>
    /// False when the scheduler does not know what is stored — unknown is not "unchanged", and a caller
    /// that treated it as unchanged would never write anything on a freshly restarted server.
    /// </remarks>
    public bool ContentUnchanged(string storeId, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentNullException.ThrowIfNull(content);

        return _marks.TryGetValue(storeId, out var marks)
               && marks.ContentHash is { } hash
               && string.Equals(hash, Hash(content), StringComparison.Ordinal);
    }

    private void MarkPass(string storeId, DateTimeOffset now, ExitStateBackupPass pass)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        _marks.AddOrUpdate(
            storeId,
            _ => new Marks(null, now, null),
            (_, current) => current.Generation == pass.Generation
                ? current with { RequestedAt = null, LastPassAt = now }
                // A request arrived after the export began, so it is still owed. Pending from the pass's
                // start at the latest: a request that was already pending before the pass (and got
                // coalesced with the new one) must not keep its old time, or the next pass would take it
                // immediately with no debounce at all; and one that arrived during the pass keeps its own.
                : current with
                {
                    RequestedAt = current.RequestedAt is { } requested && requested > pass.StartedAt
                        ? requested
                        : pass.StartedAt,
                    LastPassAt = now
                });
    }

    /// <summary>
    /// A digest of the content, compared for equality only. Never logged: it authenticates a secret, and
    /// a hash of a low-entropy value would not stay one.
    /// </summary>
    private static string Hash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}

/// <summary>
/// What one pass over one store can serve: the request generation and the wall-clock moment its export began.
/// </summary>
/// <remarks>Opaque to callers; see <see cref="ExitStateBackupScheduler.BeginPass"/>.</remarks>
public readonly record struct ExitStateBackupPass(long Generation, DateTimeOffset StartedAt);
