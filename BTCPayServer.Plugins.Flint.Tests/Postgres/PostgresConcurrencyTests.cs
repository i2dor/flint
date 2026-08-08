using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests.Postgres;

/// <summary>
/// The compare-and-set guarantees, proven by racing real callers against a real database.
/// </summary>
/// <remarks>
/// The in-memory store is single-threaded by construction, so it cannot demonstrate these. And they are the
/// properties everything else depends on: "exactly one caller is told Settled" is what makes a duplicated
/// settlement event, a <c>GetInvoice</c> lookup and a reconciliation pass safe to run concurrently.
/// </remarks>
[Trait("Category", "Postgres")]
[Collection(PostgresTestDatabase.CollectionName)]
public class PostgresConcurrencyTests
{
    private const string StoreId = "store-1";

    private readonly PostgresTestDatabase _database;

    public PostgresConcurrencyTests(PostgresTestDatabase database) => _database = database;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<EfInvoiceRecordStore> CreateStoreAsync() =>
        new EfInvoiceRecordStore(await _database.CreateFactoryAsync());

    private static InvoiceRecord NewRecord(string paymentHash) => new()
    {
        PaymentHash = paymentHash,
        StoreId = StoreId,
        Bolt11 = "lnbcrt-one",
        AmountMsat = 100_000,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        Status = InvoiceRecordStatus.Unpaid
    };

    [Fact]
    public async Task Exactly_one_of_many_racing_settle_callers_is_told_Settled()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(PaymentFixture.PaymentHash), Ct);
        const int racers = 16;

        // A barrier so the callers genuinely overlap rather than queueing behind each other.
        using var start = new SemaphoreSlim(0, racers);
        var tasks = Enumerable.Range(0, racers).Select(async i =>
        {
            await start.WaitAsync(Ct);
            return await store.SettleAsync(
                StoreId,
                PaymentFixture.PaymentHash,
                $"sdk-{i}",
                100_000,
                PaymentFixture.Preimage,
                DateTimeOffset.UtcNow,
                Ct);
        }).ToArray();

        start.Release(racers);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Outcome is InvoiceSettlementOutcome.Settled));
        Assert.Equal(racers - 1, results.Count(r => r.Outcome is InvoiceSettlementOutcome.AlreadySettled));
        // Every caller gets the row back, and they all agree on the amount that was credited.
        Assert.All(results, r => Assert.NotNull(r.Record));
        Assert.Single(results.Select(r => r.Record!.AmountReceivedMsat).Distinct());

        var final = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.Equal(InvoiceRecordStatus.Paid, final!.Status);
        Assert.Equal(100_000, final.AmountReceivedMsat);
    }

    [Fact]
    public async Task Exactly_one_of_many_racing_cancel_callers_is_told_true()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(PaymentFixture.PaymentHash), Ct);
        const int racers = 16;

        using var start = new SemaphoreSlim(0, racers);
        var tasks = Enumerable.Range(0, racers).Select(async _ =>
        {
            await start.WaitAsync(Ct);
            return await store.CancelAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        }).ToArray();

        start.Release(racers);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(cancelled => cancelled));
    }

    [Fact]
    public async Task A_settle_racing_a_cancel_never_produces_a_paid_invoice_that_reads_as_cancelled()
    {
        // Run repeatedly because the interleaving is what is under test. Either order is a valid outcome; what
        // must never happen is a row that was settled and then silently reverted to cancelled, which is what a
        // read-modify-write cancel would produce.
        var store = await CreateStoreAsync();

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var hash = attempt.ToString("x64");
            await store.AddAsync(NewRecord(hash), Ct);

            var settle = Task.Run(() => store.SettleAsync(
                StoreId, hash, "sdk-1", 100_000, PaymentFixture.Preimage, DateTimeOffset.UtcNow, Ct), Ct);
            var cancel = Task.Run(() => store.CancelAsync(StoreId, hash, Ct), Ct);

            var settleResult = await settle;
            var cancelled = await cancel;
            var final = await store.GetAsync(StoreId, hash, Ct);

            Assert.NotNull(final);
            // Exactly one of the two won, and the stored row agrees with whichever it was.
            if (settleResult.Outcome is InvoiceSettlementOutcome.Settled)
            {
                Assert.False(cancelled, "a cancel must not succeed against an invoice that settled");
                Assert.Equal(InvoiceRecordStatus.Paid, final.Status);
                Assert.Equal(100_000, final.AmountReceivedMsat);
            }
            else
            {
                Assert.Equal(InvoiceSettlementOutcome.RefusedCancelled, settleResult.Outcome);
                Assert.True(cancelled);
                Assert.Equal(InvoiceRecordStatus.Expired, final.Status);
                Assert.Null(final.AmountReceivedMsat);
            }
        }
    }

    [Fact]
    public async Task Exactly_one_of_many_racing_SDK_payment_id_writers_wins()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(PaymentFixture.PaymentHash), Ct);
        const int racers = 16;

        using var start = new SemaphoreSlim(0, racers);
        var tasks = Enumerable.Range(0, racers).Select(async i =>
        {
            await start.WaitAsync(Ct);
            return await store.TryRecordSdkPaymentIdAsync(StoreId, PaymentFixture.PaymentHash, $"sdk-{i}", Ct);
        }).ToArray();

        start.Release(racers);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(recorded => recorded));
    }

    [Fact]
    public async Task The_outgoing_payment_store_lets_exactly_one_caller_report_a_payment()
    {
        // The guard that stops a second payout naming an already-paid invoice from being marked Completed.
        var store = new EfOutgoingPaymentStore(await _database.CreateFactoryAsync());
        var now = DateTimeOffset.UtcNow;

        var first = await store.RegisterAttemptAsync(
            StoreId, PaymentFixture.PaymentHash, "key-1", "lnbcrt-one", now, Ct);
        Assert.Equal(1, first.AttemptCount);
        Assert.Null(first.ReportedAt);

        var second = await store.RegisterAttemptAsync(
            StoreId, PaymentFixture.PaymentHash, "key-1", "lnbcrt-one", now, Ct);
        Assert.Equal(2, second.AttemptCount);
        // The row is created once; a repeat attempt must not reset its history. Compared at microsecond
        // resolution because `timestamptz` stores microseconds: `first` came straight back from the insert with
        // full .NET tick precision, while `second` was read back from Postgres and has been truncated.
        Assert.Equal(Truncate(first.FirstAttemptAt), Truncate(second.FirstAttemptAt));

        const int racers = 8;
        using var start = new SemaphoreSlim(0, racers);
        var tasks = Enumerable.Range(0, racers).Select(async _ =>
        {
            await start.WaitAsync(Ct);
            return await store.TryMarkReportedAsync(StoreId, PaymentFixture.PaymentHash, DateTimeOffset.UtcNow, Ct);
        }).ToArray();

        start.Release(racers);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(reported => reported));
    }

    [Fact]
    public async Task Racing_registrations_of_one_invoice_converge_on_a_single_row()
    {
        var store = new EfOutgoingPaymentStore(await _database.CreateFactoryAsync());
        const int racers = 12;

        using var start = new SemaphoreSlim(0, racers);
        var tasks = Enumerable.Range(0, racers).Select(async _ =>
        {
            await start.WaitAsync(Ct);
            return await store.RegisterAttemptAsync(
                StoreId, PaymentFixture.PaymentHash, "key-1", "lnbcrt-one", DateTimeOffset.UtcNow, Ct);
        }).ToArray();

        start.Release(racers);
        var records = await Task.WhenAll(tasks);

        // The insert race is resolved by the primary key rather than by throwing at the caller.
        Assert.All(records, r => Assert.Equal(PaymentFixture.PaymentHash, r.PaymentHash));
        // Truncated for the same reason: exactly one caller inserted, and every other caller read that row
        // back through `timestamptz`, which keeps microseconds rather than .NET's 100-nanosecond ticks.
        Assert.Single(records.Select(r => Truncate(r.FirstAttemptAt)).Distinct());
    }

    [Fact]
    public async Task Exactly_one_of_many_racing_callers_may_resolve_a_sweep()
    {
        // The guard that makes the crash-recovery walk safe to run alongside a manual sweep and a retry: only the
        // caller told true may act on the outcome, and a sweep must not be able to be reported twice.
        var store = new EfSweepRecordStore(await _database.CreateFactoryAsync());
        const string key = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e20";
        await store.AddAsync(NewSweep(key), Ct);
        const int racers = 16;

        using var start = new SemaphoreSlim(0, racers);
        var tasks = Enumerable.Range(0, racers).Select(async i =>
        {
            await start.WaitAsync(Ct);
            return await store.TryResolveAsync(
                StoreId, key, [SweepRecordStatus.Pending],
                new SweepResolution(SweepRecordStatus.Sent, 2_190, $"txid-{i}", null, DateTimeOffset.UtcNow),
                Ct);
        }).ToArray();

        start.Release(racers);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(resolved => resolved));

        var final = await store.GetAsync(StoreId, key, Ct);
        Assert.Equal(SweepRecordStatus.Sent, final!.Status);
        // One winner, so exactly one txid — not a blend of two writes.
        Assert.StartsWith("txid-", final.TxId);
    }

    [Fact]
    public async Task Racing_inserts_of_one_idempotency_key_cannot_both_succeed()
    {
        // The primary key is what makes "one sweep per key" a database guarantee. Two rows for one key would mean
        // two sends described by one record, which is the state the whole design exists to prevent.
        var store = new EfSweepRecordStore(await _database.CreateFactoryAsync());
        const string key = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e21";
        const int racers = 8;

        using var start = new SemaphoreSlim(0, racers);
        var tasks = Enumerable.Range(0, racers).Select(async _ =>
        {
            await start.WaitAsync(Ct);
            try
            {
                await store.AddAsync(NewSweep(key), Ct);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }).ToArray();

        start.Release(racers);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(inserted => inserted));
        Assert.Equal(1, await store.CountAsync(StoreId, Ct));
    }

    [Fact]
    public async Task A_confirmation_racing_a_failure_leaves_one_coherent_outcome()
    {
        // Run repeatedly because the interleaving is what is under test. Either order is a valid outcome; what must
        // never happen is a row whose status came from one writer and whose txid came from the other's null.
        var store = new EfSweepRecordStore(await _database.CreateFactoryAsync());

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var key = $"5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e{attempt:x2}";
            await store.AddAsync(NewSweep(key), Ct);

            var now = DateTimeOffset.UtcNow;
            var confirm = Task.Run(() => store.TryResolveAsync(
                StoreId, key, [SweepRecordStatus.Pending],
                new SweepResolution(SweepRecordStatus.Confirmed, 2_190, "txid-confirmed", null, now), Ct), Ct);
            var fail = Task.Run(() => store.TryResolveAsync(
                StoreId, key, [SweepRecordStatus.Pending],
                new SweepResolution(SweepRecordStatus.Failed, null, null, "it failed", now), Ct), Ct);

            var confirmed = await confirm;
            var failed = await fail;
            var final = await store.GetAsync(StoreId, key, Ct);

            Assert.NotNull(final);
            Assert.True(confirmed ^ failed, "exactly one of the two writers must be told it resolved the sweep");

            if (confirmed)
            {
                Assert.Equal(SweepRecordStatus.Confirmed, final.Status);
                Assert.Equal("txid-confirmed", final.TxId);
                Assert.Equal(2_190, final.FeeSats);
                Assert.Null(final.Error);
            }
            else
            {
                Assert.Equal(SweepRecordStatus.Failed, final.Status);
                Assert.Equal("it failed", final.Error);
                Assert.Null(final.TxId);
            }
        }
    }

    private static SweepRecord NewSweep(string key) => new()
    {
        IdempotencyKey = key,
        StoreId = StoreId,
        DestinationAddress = "bcrt1qtxwcjjvf4ny9wsw9emgnpazey2vde3xhnyqpw0",
        DestinationMode = SweepDestinationMode.StoreWallet,
        AmountSats = 450_000,
        FeesIncluded = true,
        ConfirmationSpeed = SweepConfirmationSpeed.Medium,
        QuotedFeeSats = 2_190,
        BalanceAtDecisionSats = 500_000,
        Trigger = SweepTrigger.Automatic,
        Status = SweepRecordStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Drops sub-microsecond precision, which Postgres <c>timestamptz</c> does not store.
    /// </summary>
    /// <remarks>
    /// Without this, comparing a value that went straight out of an insert against the same value read back is a
    /// coin flip on whether the original tick count happened to be microsecond-aligned.
    /// </remarks>
    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % (TimeSpan.TicksPerMillisecond / 1000), value.Offset);
}
