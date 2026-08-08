using System.Collections.Concurrent;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The wall-clock bound and the rotation that keep a store pass off one of BTCPay's three shared
/// scheduled-task workers.
/// </summary>
/// <remarks>
/// <para>
/// <b>The clock a test moves is the visit's, not the test's.</b> <see cref="SparkStorePassScheduler"/> reads
/// wall clock through <see cref="TimeProvider.GetUtcNow"/>, so a <see cref="StubTimeProvider"/> frozen at one
/// instant means the budget never elapses however many stores are visited. Every budget test here therefore
/// has the <em>visit</em> advance the stub by a stated cost per store, which is what a real slow store does to
/// a real clock and is exactly reproducible. No test in this file sleeps to make a budget expire.
/// </para>
/// <para>
/// The per-store deadline is the one thing that cannot be stubbed: <see cref="SparkDeadline"/> waits on
/// <c>Task.Delay</c>, which does not read a <see cref="TimeProvider"/>. Those tests use a genuinely short real
/// deadline and a visit that blocks on a <see cref="TaskCompletionSource"/> the test releases, so nothing
/// waits on a timer for its result — the timer only has to lose a race against a task that is never going to
/// complete on its own.
/// </para>
/// </remarks>
public class SparkStorePassSchedulerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Never = TimeSpan.FromMinutes(10);

    private static SparkStorePassScheduler Create(
        TimeProvider time,
        TimeSpan? passBudget = null,
        TimeSpan? storeDeadline = null) =>
        new("test", passBudget ?? Never, storeDeadline ?? Never, time, NullLogger.Instance);

    private static void NeverFails(string storeId, Exception ex) =>
        Assert.Fail($"store {storeId} was not expected to fail: {ex}");

    // ---------------------------------------------------------------------------------------------------
    // Rotation
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Rotate_puts_the_stores_in_a_stable_total_order()
    {
        // ConcurrentDictionary.Keys has no defined order, so "where did we get to" is only meaningful against
        // an order the scheduler imposes itself.
        Assert.Equal(
            ["a", "b", "c"],
            SparkStorePassScheduler.Rotate(["c", "a", "b"], cursor: null));
    }

    [Fact]
    public void Rotate_resumes_after_the_cursor_and_wraps()
    {
        Assert.Equal(
            ["c", "d", "a", "b"],
            SparkStorePassScheduler.Rotate(["a", "b", "c", "d"], cursor: "b"));
    }

    [Fact]
    public void Rotate_starts_over_when_the_cursor_was_the_last_store()
    {
        Assert.Equal(
            ["a", "b", "c"],
            SparkStorePassScheduler.Rotate(["a", "b", "c"], cursor: "c"));
    }

    [Fact]
    public void Rotate_survives_a_cursor_naming_a_store_that_has_gone_away()
    {
        // "b" was removed between passes. The rotation resumes at the first store ordered after it, which is
        // where the next unvisited store was going to be regardless.
        Assert.Equal(
            ["c", "d", "a"],
            SparkStorePassScheduler.Rotate(["a", "c", "d"], cursor: "b"));
    }

    // ---------------------------------------------------------------------------------------------------
    // The budget, and the round-robin that keeps it from starving the tail
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_pass_stops_starting_stores_once_its_budget_is_spent()
    {
        var time = new StubTimeProvider(DateTimeOffset.UnixEpoch);
        var log = new WriteLog();
        // Four stores at 10s each against a 25s budget: two are started inside it, the third is refused
        // because by then 20s of a 25s budget is gone and the check happens before a store is started... which
        // still admits the third. The fourth is the one refused. Spelled out rather than left to arithmetic.
        var pass = Create(time, passBudget: TimeSpan.FromSeconds(25));

        var summary = await pass.RunAsync(
            ["s1", "s2", "s3", "s4"],
            (storeId, _) =>
            {
                log.Record($"visit:{storeId}");
                time.Advance(TimeSpan.FromSeconds(10));
                return Task.CompletedTask;
            },
            NeverFails,
            Ct);

        Assert.True(summary.BudgetExhausted);
        Assert.Equal(3, summary.Visited);
        Assert.Equal(4, summary.Total);
        Assert.Equal(["visit:s1", "visit:s2", "visit:s3"], log.Entries);
    }

    [Fact]
    public async Task The_next_pass_resumes_where_the_budget_stopped_the_last_one()
    {
        var time = new StubTimeProvider(DateTimeOffset.UnixEpoch);
        var log = new WriteLog();
        var pass = Create(time, passBudget: TimeSpan.FromSeconds(15));
        string[] stores = ["s1", "s2", "s3", "s4"];

        Task Visit(string storeId, CancellationToken _)
        {
            log.Record(storeId);
            time.Advance(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        }

        await pass.RunAsync(stores, Visit, NeverFails, Ct);
        await pass.RunAsync(stores, Visit, NeverFails, Ct);
        await pass.RunAsync(stores, Visit, NeverFails, Ct);

        // The whole point of the rotation, asserted on the one shared ordered log rather than on four
        // independent per-store counters: every store gets a turn, and s3/s4 are not starved behind s1/s2.
        // Two per pass, in order, wrapping — not "s1, s2" three times over.
        Assert.Equal(["s1", "s2", "s3", "s4", "s1", "s2"], log.Entries);
    }

    [Fact]
    public async Task Without_rotation_the_tail_would_never_be_reached()
    {
        // The negative half of the test above, stated as its own property so that a change removing the
        // rotation fails something that names the consequence. Every pass starts from a fresh scheduler, which
        // is what "no rotation" means; s3 and s4 are never reached.
        var time = new StubTimeProvider(DateTimeOffset.UnixEpoch);
        var log = new WriteLog();
        string[] stores = ["s1", "s2", "s3", "s4"];

        for (var i = 0; i < 3; i++)
        {
            var fresh = Create(time, passBudget: TimeSpan.FromSeconds(15));
            await fresh.RunAsync(stores, (storeId, _) =>
            {
                log.Record(storeId);
                time.Advance(TimeSpan.FromSeconds(10));
                return Task.CompletedTask;
            }, NeverFails, Ct);
        }

        Assert.Equal(["s1", "s2", "s1", "s2", "s1", "s2"], log.Entries);
        Assert.DoesNotContain("s3", log.Entries);
    }

    [Fact]
    public async Task A_budget_too_small_for_even_one_store_still_makes_progress()
    {
        // Degrades to one store per pass rather than to no progress at all. A pass that started nothing would
        // stop the plugin settling invoices entirely, which is worse than being slow.
        var time = new StubTimeProvider(DateTimeOffset.UnixEpoch);
        var log = new WriteLog();
        var pass = Create(time, passBudget: TimeSpan.Zero);
        string[] stores = ["s1", "s2", "s3"];

        Task Visit(string storeId, CancellationToken _)
        {
            log.Record(storeId);
            time.Advance(TimeSpan.FromSeconds(1));
            return Task.CompletedTask;
        }

        var first = await pass.RunAsync(stores, Visit, NeverFails, Ct);
        await pass.RunAsync(stores, Visit, NeverFails, Ct);
        await pass.RunAsync(stores, Visit, NeverFails, Ct);
        await pass.RunAsync(stores, Visit, NeverFails, Ct);

        Assert.Equal(1, first.Visited);
        Assert.True(first.BudgetExhausted);
        // One per pass, rotating, wrapping after three.
        Assert.Equal(["s1", "s2", "s3", "s1"], log.Entries);
    }

    [Fact]
    public async Task A_pass_inside_its_budget_visits_every_store_and_says_so()
    {
        var time = new StubTimeProvider(DateTimeOffset.UnixEpoch);
        var pass = Create(time, passBudget: TimeSpan.FromSeconds(30));

        var summary = await pass.RunAsync(
            ["s1", "s2", "s3"],
            (_, _) =>
            {
                time.Advance(TimeSpan.FromSeconds(1));
                return Task.CompletedTask;
            },
            NeverFails,
            Ct);

        Assert.False(summary.BudgetExhausted);
        Assert.Equal(new SparkPassSummary(3, 3, 0, 0, 0, false), summary);
    }

    [Fact]
    public async Task An_empty_store_list_is_not_a_budget_exhaustion()
    {
        var pass = Create(new StubTimeProvider(DateTimeOffset.UnixEpoch), passBudget: TimeSpan.Zero);

        var summary = await pass.RunAsync([], (_, _) => Task.CompletedTask, NeverFails, Ct);

        Assert.Equal(new SparkPassSummary(0, 0, 0, 0, 0, false), summary);
    }

    // ---------------------------------------------------------------------------------------------------
    // The per-store deadline, and the in-flight mark that stops it accumulating visits
    // ---------------------------------------------------------------------------------------------------

    // Timeout, and it is load-bearing rather than defensive. Reverting the per-store deadline to verify this
    // test catches it makes the visit block forever, and without a bound here that shows up as a wedged test
    // run rather than a failure — which is how a broken guard gets mistaken for a slow machine. The same
    // reasoning is written on SparkServiceHarness.Dispose, and SparkPluginStartupTests applies the same
    // discipline: on this project, a test for a hang is bounded or it is not a test.
    [Fact(Timeout = 30_000)]
    public async Task A_store_that_hangs_is_abandoned_and_the_pass_moves_on()
    {
        // The failure this whole class exists for: SparkSweepEngine makes no deadline-bounded SDK call, so one
        // wallet whose SyncWallet never returns would hold a shared worker for the life of the process and
        // every store behind it would never be swept again.
        var hung = new TaskCompletionSource();
        var log = new WriteLog();
        var pass = Create(
            new StubTimeProvider(DateTimeOffset.UnixEpoch),
            storeDeadline: TimeSpan.FromMilliseconds(50));

        try
        {
            var summary = await pass.RunAsync(
                ["s1", "s2"],
                async (storeId, _) =>
                {
                    log.Record($"enter:{storeId}");
                    if (storeId == "s1")
                        await hung.Task;
                    log.Record($"leave:{storeId}");
                },
                NeverFails,
                Ct);

            Assert.Equal(1, summary.Abandoned);
            Assert.Equal(2, summary.Visited);
            Assert.Equal(0, summary.Failed);
            // s2 ran to completion even though s1 never did. Asserted on the one ordered log: two independent
            // counters would be just as happy if s2 had never been entered at all.
            Assert.Equal(["enter:s1", "enter:s2", "leave:s2"], log.Entries);
            Assert.DoesNotContain("leave:s1", log.Entries);
        }
        finally
        {
            hung.SetResult();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task An_abandoned_store_is_skipped_rather_than_visited_twice()
    {
        // Abandoning the wait never abandons the work — no SDK call can be cancelled — so without the in-flight
        // mark a hung store would accumulate one live visit per pass, forever.
        var hung = new TaskCompletionSource();
        var log = new WriteLog();
        var pass = Create(
            new StubTimeProvider(DateTimeOffset.UnixEpoch),
            storeDeadline: TimeSpan.FromMilliseconds(50));

        async Task Visit(string storeId, CancellationToken _)
        {
            log.Record($"enter:{storeId}");
            if (storeId == "s1")
                await hung.Task;
        }

        try
        {
            await pass.RunAsync(["s1", "s2"], Visit, NeverFails, Ct);
            var second = await pass.RunAsync(["s1", "s2"], Visit, NeverFails, Ct);

            Assert.Equal(1, second.Skipped);
            Assert.Equal(1, second.Visited);
            // s1 entered exactly once across both passes; s2 entered on both.
            Assert.Equal(["enter:s1", "enter:s2", "enter:s2"], log.Entries);
        }
        finally
        {
            hung.SetResult();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task A_store_is_visited_again_once_its_abandoned_visit_finally_finishes()
    {
        // The skip has to be temporary. A mark that outlived the work would silently retire a store from
        // reconciliation and sweeping for the life of the process — a far worse failure than a slow pass.
        var hung = new TaskCompletionSource();
        var log = new WriteLog();
        var pass = Create(
            new StubTimeProvider(DateTimeOffset.UnixEpoch),
            storeDeadline: TimeSpan.FromMilliseconds(50));
        var release = false;

        async Task Visit(string storeId, CancellationToken _)
        {
            log.Record($"enter:{storeId}");
            if (storeId == "s1" && !release)
                await hung.Task;
        }

        await pass.RunAsync(["s1"], Visit, NeverFails, Ct);
        Assert.Equal(["enter:s1"], log.Entries);

        // Let the abandoned visit finish, then wait for the scheduler's finally block to clear the mark. The
        // continuation runs on the thread pool, so this is a real handoff and not a formality.
        release = true;
        hung.SetResult();
        await WaitUntilAsync(async () =>
        {
            var probe = await pass.RunAsync(["s1"], Visit, NeverFails, Ct);
            return probe.Visited == 1;
        });

        Assert.Contains("enter:s1", log.Entries);
        Assert.True(log.Entries.Count(e => e == "enter:s1") >= 2);
    }

    // ---------------------------------------------------------------------------------------------------
    // Failure isolation and cancellation
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_stores_failure_is_reported_and_costs_no_other_store_its_turn()
    {
        var log = new WriteLog();
        var failures = new List<(string StoreId, string Message)>();
        var pass = Create(new StubTimeProvider(DateTimeOffset.UnixEpoch));

        var summary = await pass.RunAsync(
            ["s1", "s2", "s3"],
            (storeId, _) =>
            {
                log.Record(storeId);
                return storeId == "s2"
                    ? Task.FromException(new InvalidOperationException("this wallet is broken"))
                    : Task.CompletedTask;
            },
            (storeId, ex) => failures.Add((storeId, ex.Message)),
            Ct);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(3, summary.Visited);
        Assert.Equal([("s2", "this wallet is broken")], failures);
        Assert.Equal(["s1", "s2", "s3"], log.Entries);
    }

    [Fact]
    public async Task A_failed_store_does_not_stay_marked_in_flight()
    {
        // The mark is cleared in a finally, so a store that throws every pass is retried every pass rather
        // than being skipped forever after its first failure.
        var log = new WriteLog();
        var pass = Create(new StubTimeProvider(DateTimeOffset.UnixEpoch));

        Task Visit(string storeId, CancellationToken _)
        {
            log.Record(storeId);
            return Task.FromException(new InvalidOperationException("broken"));
        }

        await pass.RunAsync(["s1"], Visit, (_, _) => { }, Ct);
        var second = await pass.RunAsync(["s1"], Visit, (_, _) => { }, Ct);

        Assert.Equal(0, second.Skipped);
        Assert.Equal(1, second.Failed);
        Assert.Equal(["s1", "s1"], log.Entries);
    }

    [Fact]
    public async Task Cancellation_stops_the_pass_rather_than_being_reported_as_a_store_failure()
    {
        using var cts = new CancellationTokenSource();
        var log = new WriteLog();
        var pass = Create(new StubTimeProvider(DateTimeOffset.UnixEpoch));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass.RunAsync(
            ["s1", "s2", "s3"],
            async (storeId, token) =>
            {
                log.Record(storeId);
                if (storeId == "s1")
                    await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
            },
            // A shutdown must not be logged to the operator as three broken wallets.
            NeverFails,
            cts.Token));

        Assert.Equal(["s1"], log.Entries);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(10, Ct);
        }

        Assert.Fail("the condition was never met within 5s");
    }
}

/// <summary>
/// The scheduler under the concurrency its real callers produce.
/// </summary>
/// <remarks>
/// <c>SparkService.StartAsync</c> fires its catch-up reconciliation pass on a background task after opening the
/// startup gate, so the scheduled task's own pass can begin while it is still running — on the one scheduler
/// both share. That is not hypothetical and it is not guarded by a lock, deliberately, so it is pinned here.
/// </remarks>
public class SparkStorePassSchedulerConcurrencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_overlapping_passes_never_visit_one_store_at_the_same_time()
    {
        var pass = new SparkStorePassScheduler(
            "test", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10),
            new StubTimeProvider(DateTimeOffset.UnixEpoch), NullLogger.Instance);

        string[] stores = ["s1", "s2", "s3", "s4", "s5", "s6"];
        var concurrent = new ConcurrentDictionary<string, byte>();
        var overlapped = new ConcurrentBag<string>();
        var gate = new TaskCompletionSource();

        async Task Visit(string storeId, CancellationToken token)
        {
            if (!concurrent.TryAdd(storeId, 0))
                overlapped.Add(storeId);
            // Yield generously so the two passes genuinely interleave rather than each running to completion
            // before the other is scheduled.
            await Task.Yield();
            await Task.Delay(1, Ct);
            concurrent.TryRemove(storeId, out _);
        }

        var a = Task.Run(async () =>
        {
            await gate.Task;
            return await pass.RunAsync(stores, Visit, (_, _) => { }, Ct);
        }, Ct);
        var b = Task.Run(async () =>
        {
            await gate.Task;
            return await pass.RunAsync(stores, Visit, (_, _) => { }, Ct);
        }, Ct);

        gate.SetResult();
        var summaries = await Task.WhenAll(a, b);

        Assert.Empty(overlapped);
        // Neither pass faulted, and between them every store was either visited or skipped as already busy —
        // nothing was silently dropped on the floor by the race.
        foreach (var summary in summaries)
            Assert.Equal(stores.Length, summary.Visited + summary.Skipped);
    }
}
