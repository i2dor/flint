using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>What one pass over the configured stores actually managed to do.</summary>
/// <param name="Total">Stores the pass was offered.</param>
/// <param name="Visited">Stores whose visit was started, whether or not it finished in time.</param>
/// <param name="Abandoned">Visits still running when their per-store deadline passed.</param>
/// <param name="Skipped">Stores passed over because their previous visit is still running.</param>
/// <param name="Failed">Visits that threw.</param>
/// <param name="BudgetExhausted">Whether the pass stopped early because it ran out of wall clock.</param>
public sealed record SparkPassSummary(
    int Total,
    int Visited,
    int Abandoned,
    int Skipped,
    int Failed,
    bool BudgetExhausted);

/// <summary>
/// Walks the configured stores under a wall-clock budget, resuming next pass where this one stopped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a budget is needed at all, and why the per-call deadlines already in the plugin are not one.</b>
/// Both of the plugin's scheduled tasks enumerate every configured store on every pass, and BTCPay runs
/// scheduled tasks on a pool of exactly <b>three</b> workers shared by every scheduled task on the server
/// (<c>PeriodicTaskLauncherHostedService.StartAsync</c> launches <c>Enumerable.Range(0, 3)</c> loops, and each
/// loop <em>awaits</em> <c>IPeriodicTask.Do</c> before releasing its worker). Core registers eight such tasks —
/// rates every 30 s, fee estimation every 3 min, pending transactions, webhook cleanup and the rest — and this
/// plugin adds two. So a pass that runs long does not merely delay itself: it holds a third of the server's
/// scheduled-task capacity while it does, and two long Spark passes hold two thirds of it.
/// </para>
/// <para>
/// The per-call deadlines (<see cref="Constants.SdkCallDeadline"/>, applied inside the settlement reconciler
/// and the startup connect) bound one SDK call. They do not bound a pass, and the arithmetic is not close:
/// reconciliation examines up to 1,000 invoices per store and each one may cost a point lookup plus ten pages,
/// every one of them entitled to its own 30 s. The sweep pass is worse — <c>SparkSweepEngine</c> makes no
/// deadline-bounded SDK call at all, so one wallet whose <c>SyncWallet</c> never returns holds a shared worker
/// for the life of the process. That is precisely the failure class that deadlocked BTCPay's startup in
/// PR #6 — an unbounded await on a path with no timeout of its own — moved off the startup path and onto a
/// shared one.
/// </para>
/// <para>
/// <b>What this fixes, and what it deliberately does not.</b> A pass is bounded by
/// <c>passBudget + storeDeadline</c>: the budget decides whether another store may be <em>started</em>, and the
/// per-store deadline bounds the one that is running. It cannot bound anything more tightly than that, because
/// no SDK call can be cancelled — abandoning a visit abandons the wait and never the work, exactly as
/// <see cref="SparkDeadline"/> does. So an abandoned store's visit is still running afterwards, which is what
/// <see cref="_inFlight"/> is for: the next pass skips that store rather than starting a second visit on top of
/// the first. Every operation both callers perform is idempotent and re-entrant-safe on its own
/// (settlement through a compare-and-set, sweeps through the engine's own single-flight and a persisted
/// idempotency key), so the skip is a courtesy against wasted work rather than the correctness guarantee.
/// </para>
/// <para>
/// <b>Round-robin, because a budget without one is a starvation machine.</b> Stores are visited in a fixed
/// total order — ordinal by store id, since <c>ConcurrentDictionary.Keys</c> has no defined order and would
/// otherwise make "where did we get to" meaningless — rotated so each pass begins just after the last store the
/// previous pass considered. Without that, a budget would visit the same prefix forever and the tail of the
/// list would never be reconciled or swept at all; and even <em>without</em> a budget, today's fixed order
/// means one slow store permanently delays every store behind it. At least one store is always started,
/// however small the budget, so a pathologically tight budget degrades to one store per pass rather than to no
/// progress.
/// </para>
/// <para>
/// The clock is read through <see cref="TimeProvider.GetUtcNow"/> rather than a monotonic timestamp so that a
/// test can move it. The cost is that a wall-clock jump can end a pass early, which is harmless: the next pass
/// resumes from where it stopped.
/// </para>
/// </remarks>
public sealed class SparkStorePassScheduler
{
    private readonly string _what;
    private readonly TimeSpan _passBudget;
    private readonly TimeSpan _storeDeadline;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Stores whose visit from an earlier pass has not finished. Not a correctness guard — see the class
    /// remarks — but without it a store that hangs would accumulate one abandoned visit per pass forever.
    /// </summary>
    /// <remarks>
    /// It also carries the one case where two passes genuinely overlap: <c>SparkService.StartAsync</c> fires its
    /// catch-up reconciliation pass on a background task <em>after</em> opening the startup gate, so the
    /// scheduled task's first pass can begin while it is still running. Both share this scheduler, and the
    /// second pass skips whatever the first is still holding rather than starting a second visit on it.
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    /// <summary>
    /// The last store this scheduler considered. The next pass starts at the first store ordered after it.
    /// </summary>
    /// <remarks>
    /// A store id rather than an index, because the set of stores changes between passes: an index would move
    /// under a store being added or removed and silently re-order the rotation.
    /// <para>
    /// <c>volatile</c> rather than locked, because of the overlapping passes described on <see cref="_inFlight"/>.
    /// A reference assignment is already atomic, so the race cannot tear the field; what the annotation buys is
    /// that the later pass reads the earlier one's write rather than a stale value. Deliberately not serialised
    /// under a lock: a lock would make the scheduled pass wait for the startup pass, which is the opposite of
    /// what a budget is for. The worst outcome of the race is that one store is visited on both passes — which
    /// <see cref="_inFlight"/> already prevents from overlapping, and which every operation behind this
    /// scheduler is idempotent under anyway.
    /// </para>
    /// </remarks>
    private volatile string? _cursor;

    public SparkStorePassScheduler(
        string what,
        TimeSpan passBudget,
        TimeSpan storeDeadline,
        TimeProvider timeProvider,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(what);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _what = what;
        _passBudget = passBudget;
        _storeDeadline = storeDeadline;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs <paramref name="visit"/> over as many of <paramref name="storeIds"/> as the budget allows.
    /// </summary>
    /// <param name="onStoreFailed">
    /// Called with anything a visit throws, so each caller keeps its own wording. A visit that throws does not
    /// stop the pass.
    /// </param>
    public async Task<SparkPassSummary> RunAsync(
        IReadOnlyCollection<string> storeIds,
        Func<string, CancellationToken, Task> visit,
        Action<string, Exception> onStoreFailed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storeIds);
        ArgumentNullException.ThrowIfNull(visit);
        ArgumentNullException.ThrowIfNull(onStoreFailed);

        var order = Rotate(storeIds, _cursor);
        var startedAt = _timeProvider.GetUtcNow();
        var visited = 0;
        var abandoned = 0;
        var skipped = 0;
        var failed = 0;
        var exhausted = false;

        foreach (var storeId in order)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Checked before starting a store and never in the middle of one, because the work cannot be
            // interrupted. The first started store bypasses it so that no budget, however tight, can reduce the
            // pass to doing nothing at all.
            if (visited > 0 && _timeProvider.GetUtcNow() - startedAt >= _passBudget)
            {
                exhausted = true;
                break;
            }

            // Considered, so the next pass moves on regardless of what happens below.
            //
            // Deliberately before the skip check rather than after it, but note honestly that the two
            // placements are not distinguishable from outside this class and no test pins the difference: a
            // skipped store does no work and logs nothing, so whichever of it or its predecessor the cursor
            // names, the next pass still covers every store. It is here because "considered" is the honest
            // meaning of the cursor, not because moving it would break something.
            _cursor = storeId;

            if (!_inFlight.TryAdd(storeId, 0))
            {
                skipped++;
                _logger.LogDebug(
                    "Store {StoreId}: its previous Spark {What} pass has not finished, so this one skipped it",
                    storeId, _what);
                continue;
            }

            visited++;

            try
            {
                var finished = await SparkDeadline.OrTimeoutAsync(
                        VisitAsync(storeId, visit, cancellationToken),
                        _storeDeadline,
                        () => _logger.LogWarning(
                            "Store {StoreId}: its Spark {What} pass exceeded {Seconds}s, so this pass stopped "
                            + "waiting on it and moved to the next store. The work itself cannot be cancelled "
                            + "and is still running; this store will be skipped until it finishes",
                            storeId, _what, _storeDeadline.TotalSeconds),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!finished)
                    abandoned++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Reported by the caller in its own words, and never fatal to the pass: one store's failure must
                // not cost every store behind it its turn. A visit abandoned above and faulting later is not
                // seen here — SparkDeadline observes it — which is the price of not waiting.
                failed++;
                onStoreFailed(storeId, ex);
            }
        }

        if (exhausted)
        {
            _logger.LogInformation(
                "The Spark {What} pass reached {Visited} of {Total} store(s) within its {Seconds}s budget. The "
                + "rest are not skipped — the next pass resumes after store {Cursor} — but if this repeats, the "
                + "pass is no longer keeping up with the number of configured stores",
                _what, visited, storeIds.Count, _passBudget.TotalSeconds, _cursor);
        }

        return new SparkPassSummary(storeIds.Count, visited, abandoned, skipped, failed, exhausted);
    }

    /// <summary>
    /// Runs one store's visit, always clearing its in-flight mark when the visit actually finishes.
    /// </summary>
    /// <remarks>
    /// The clearing is on the work task and not around the <c>await</c> of it in <see cref="RunAsync"/>, because
    /// that await may be abandoned: a store must stay marked in-flight for exactly as long as its visit is
    /// actually running, which is a question only the task itself can answer. A visit's exception is propagated
    /// rather than swallowed here — <see cref="RunAsync"/> is what decides whether the caller hears about it,
    /// and it only can if the exception reaches it.
    /// </remarks>
    private async Task VisitAsync(
        string storeId,
        Func<string, CancellationToken, Task> visit,
        CancellationToken cancellationToken)
    {
        try
        {
            await visit(storeId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(storeId, out _);
        }
    }

    /// <summary>
    /// The stores in a stable total order, beginning just after <paramref name="cursor"/>.
    /// </summary>
    /// <remarks>
    /// A cursor naming a store that has since been removed still works: the rotation resumes at the first store
    /// ordered after it, which is where the next unvisited store was going to be anyway.
    /// </remarks>
    internal static IReadOnlyList<string> Rotate(IReadOnlyCollection<string> storeIds, string? cursor)
    {
        ArgumentNullException.ThrowIfNull(storeIds);

        var ordered = storeIds.OrderBy(id => id, StringComparer.Ordinal).ToList();
        if (cursor is null || ordered.Count == 0)
            return ordered;

        var resumeAt = ordered.FindIndex(id => string.CompareOrdinal(id, cursor) > 0);
        if (resumeAt <= 0)
            return ordered;

        return [.. ordered[resumeAt..], .. ordered[..resumeAt]];
    }
}
