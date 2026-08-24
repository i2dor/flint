using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using BTCPayServer.Plugins.Flint.Tests.Postgres;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The <see cref="IInvoiceRecordStore"/> contract, asserted against every implementation.
/// </summary>
/// <remarks>
/// <para>
/// Written as a contract rather than as tests of one class because the client and service are tested against
/// the in-memory implementation, and those tests only mean something if the real one behaves identically. An
/// earlier revision had the state machine unit-tested in isolation while the production store had no coverage
/// at all — which is how it shipped with an explicit transaction that threw on every call under BTCPay's
/// retry-enabled context, making settlement impossible.
/// </para>
/// <para>
/// The Postgres subclass is skipped unless <c>SPARK_POSTGRES_TESTS</c> holds a connection string.
/// </para>
/// </remarks>
public abstract class InvoiceRecordStoreContractTests
{
    private const string StoreId = "store-1";
    private const string OtherStoreId = "store-2";

    protected abstract Task<IInvoiceRecordStore> CreateStoreAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InvoiceRecord NewRecord(
        string? paymentHash = null,
        string storeId = StoreId,
        InvoiceRecordStatus status = InvoiceRecordStatus.Unpaid,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null) => new()
    {
        PaymentHash = paymentHash ?? PaymentFixture.PaymentHash,
        StoreId = storeId,
        Bolt11 = "lnbcrt-one",
        AmountMsat = 100_000,
        Description = "order 42",
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
        ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
        Status = status
    };

    [Fact]
    public async Task An_added_record_is_readable_within_its_store_only()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);

        var mine = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.NotNull(mine);
        Assert.Equal("order 42", mine.Description);
        Assert.Equal(InvoiceRecordStatus.Unpaid, mine.Status);

        // Scoping is not cosmetic: it is what stops one store resolving another store's invoice.
        Assert.Null(await store.GetAsync(OtherStoreId, PaymentFixture.PaymentHash, Ct));
    }

    [Fact]
    public async Task Adding_the_same_payment_hash_twice_fails()
    {
        // A payment-hash collision would mean the service provider reused a hash. It must never be papered
        // over by overwriting the first invoice.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);

        await Assert.ThrowsAnyAsync<Exception>(() => store.AddAsync(NewRecord(), Ct));
    }

    [Fact]
    public async Task Settling_an_unpaid_record_reports_Settled_and_records_the_received_amount()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);
        var settledAt = DateTimeOffset.UtcNow;

        var result = await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 250_000, PaymentFixture.Preimage, settledAt, Ct);

        Assert.Equal(InvoiceSettlementOutcome.Settled, result.Outcome);
        Assert.NotNull(result.Record);
        Assert.Equal(InvoiceRecordStatus.Paid, result.Record.Status);
        Assert.Equal("sdk-1", result.Record.SdkPaymentId);
        // Overpaid on purpose: the received amount must not be replaced by the invoiced one.
        Assert.Equal(250_000, result.Record.AmountReceivedMsat);
        Assert.Equal(100_000, result.Record.AmountMsat);
        Assert.Equal(PaymentFixture.Preimage, result.Record.Preimage);
        Assert.NotNull(result.Record.SettledAt);
    }

    [Fact]
    public async Task Settling_twice_reports_AlreadySettled_and_keeps_the_first_observation()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);
        var first = DateTimeOffset.UtcNow.AddMinutes(-5);
        await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, PaymentFixture.Preimage, first, Ct);

        var second = await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-2", 999_999, "00", DateTimeOffset.UtcNow, Ct);

        Assert.Equal(InvoiceSettlementOutcome.AlreadySettled, second.Outcome);
        Assert.NotNull(second.Record);
        Assert.Equal("sdk-1", second.Record.SdkPaymentId);
        Assert.Equal(100_000, second.Record.AmountReceivedMsat);
        Assert.Equal(PaymentFixture.Preimage, second.Record.Preimage);
    }

    [Fact]
    public async Task Settling_backfills_a_preimage_that_was_missing()
    {
        // The event that settled the invoice may have carried no preimage while a later read does.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);
        await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);

        var second = await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, PaymentFixture.Preimage,
            DateTimeOffset.UtcNow, Ct);

        Assert.Equal(InvoiceSettlementOutcome.AlreadySettled, second.Outcome);
        Assert.Equal(PaymentFixture.Preimage, second.Record!.Preimage);
        var reread = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.Equal(PaymentFixture.Preimage, reread!.Preimage);
    }

    [Fact]
    public async Task Settling_an_unknown_hash_reports_NotFound()
    {
        var store = await CreateStoreAsync();

        var result = await store.SettleAsync(
            StoreId, PaymentFixture.OtherPaymentHash, "sdk-1", 1000, null, DateTimeOffset.UtcNow, Ct);

        Assert.Equal(InvoiceSettlementOutcome.NotFound, result.Outcome);
        Assert.Null(result.Record);
    }

    [Fact]
    public async Task Settling_another_stores_invoice_reports_NotFound()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);

        var result = await store.SettleAsync(
            OtherStoreId, PaymentFixture.PaymentHash, "sdk-1", 1000, null, DateTimeOffset.UtcNow, Ct);

        Assert.Equal(InvoiceSettlementOutcome.NotFound, result.Outcome);
        // And the real invoice is untouched.
        var mine = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.Equal(InvoiceRecordStatus.Unpaid, mine!.Status);
    }

    [Fact]
    public async Task Settling_a_cancelled_invoice_credits_it_like_any_other()
    {
        // A cancelled invoice is still payable on the service provider — Spark has no way to withdraw it —
        // so a late payment must still settle and credit the invoice it was minted for. Refusing would
        // leave real money unattributed in the wallet balance with the BTCPay invoice unpaid.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);
        Assert.True(await store.CancelAsync(StoreId, PaymentFixture.PaymentHash, Ct));

        var result = await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, PaymentFixture.Preimage,
            DateTimeOffset.UtcNow, Ct);

        Assert.Equal(InvoiceSettlementOutcome.Settled, result.Outcome);
        var reread = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.Equal(InvoiceRecordStatus.Paid, reread!.Status);
        Assert.Equal(100_000, reread.AmountReceivedMsat);
        Assert.Equal("sdk-1", reread.SdkPaymentId);
        Assert.Equal(PaymentFixture.Preimage, reread.Preimage);
        Assert.NotNull(reread.SettledAt);
    }

    [Fact]
    public async Task Cancelling_reports_true_only_for_the_call_that_changed_the_status()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);

        Assert.True(await store.CancelAsync(StoreId, PaymentFixture.PaymentHash, Ct));
        Assert.False(await store.CancelAsync(StoreId, PaymentFixture.PaymentHash, Ct));
    }

    [Fact]
    public async Task Cancelling_a_settled_invoice_is_refused_and_cannot_un_settle_it()
    {
        // The race this guards is routine: BTCPay cancels a superseded LNURL invoice at exactly the moment it
        // may be settling. A read-modify-write here would overwrite the settlement and lose a real payment.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);
        await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, PaymentFixture.Preimage,
            DateTimeOffset.UtcNow, Ct);

        Assert.False(await store.CancelAsync(StoreId, PaymentFixture.PaymentHash, Ct));

        var reread = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.Equal(InvoiceRecordStatus.Paid, reread!.Status);
        Assert.Equal(100_000, reread.AmountReceivedMsat);
    }

    [Fact]
    public async Task Cancelling_an_unknown_or_foreign_invoice_reports_false()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);

        Assert.False(await store.CancelAsync(StoreId, PaymentFixture.OtherPaymentHash, Ct));
        Assert.False(await store.CancelAsync(OtherStoreId, PaymentFixture.PaymentHash, Ct));
    }

    [Fact]
    public async Task Recording_an_SDK_payment_id_succeeds_once_and_only_until_settled()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);

        Assert.True(await store.TryRecordSdkPaymentIdAsync(StoreId, PaymentFixture.PaymentHash, "sdk-1", Ct));
        var reread = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.Equal("sdk-1", reread!.SdkPaymentId);

        // Never overwritten: for a self-payment the second id would be the wrong leg's.
        Assert.False(await store.TryRecordSdkPaymentIdAsync(StoreId, PaymentFixture.PaymentHash, "sdk-2", Ct));
        reread = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.Equal("sdk-1", reread!.SdkPaymentId);
    }

    [Fact]
    public async Task Recording_an_SDK_payment_id_still_works_after_the_invoice_is_cancelled()
    {
        // A pending event can arrive after BTCPay cancelled a superseded invoice. Recording the id keeps the
        // settlement's point lookup working for exactly the late payment a cancelled invoice can still
        // receive — without it, that payment is found only by a history scan.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);
        Assert.True(await store.CancelAsync(StoreId, PaymentFixture.PaymentHash, Ct));

        Assert.True(await store.TryRecordSdkPaymentIdAsync(StoreId, PaymentFixture.PaymentHash, "sdk-1", Ct));
        var reread = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.Equal("sdk-1", reread!.SdkPaymentId);
    }

    [Fact]
    public async Task Recording_an_SDK_payment_id_is_refused_once_settled()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(PaymentFixture.OtherPaymentHash), Ct);
        await store.SettleAsync(
            StoreId, PaymentFixture.OtherPaymentHash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);

        Assert.False(
            await store.TryRecordSdkPaymentIdAsync(StoreId, PaymentFixture.OtherPaymentHash, "sdk-2", Ct));
    }

    [Fact]
    public async Task Listing_returns_a_stores_invoices_newest_first()
    {
        var store = await CreateStoreAsync();
        var older = NewRecord(PaymentFixture.PaymentHash, createdAt: DateTimeOffset.UtcNow.AddMinutes(-30));
        var newer = NewRecord(PaymentFixture.OtherPaymentHash, createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        await store.AddAsync(older, Ct);
        await store.AddAsync(newer, Ct);

        var listed = await store.ListAsync(StoreId, pendingOnly: false, offset: 0, limit: 10, Ct);

        Assert.Equal([PaymentFixture.OtherPaymentHash, PaymentFixture.PaymentHash],
            listed.Select(r => r.PaymentHash).ToArray());
    }

    [Fact]
    public async Task Listing_pending_only_excludes_paid_cancelled_and_naturally_expired_invoices()
    {
        // This is the query the reconciliation task walks, so what it excludes decides what can never be
        // recovered. Natural expiry is excluded because such an invoice is beyond BTCPay's interest; cancelled
        // and paid are excluded because there is nothing left to resolve.
        var store = await CreateStoreAsync();
        var pending = NewRecord(PaymentFixture.PaymentHash);
        var expired = NewRecord(
            PaymentFixture.OtherPaymentHash,
            createdAt: DateTimeOffset.UtcNow.AddDays(-2),
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        var paidHash = new string('c', 64);
        var paid = NewRecord(paidHash);
        var cancelledHash = new string('d', 64);
        var cancelled = NewRecord(cancelledHash);

        await store.AddAsync(pending, Ct);
        await store.AddAsync(expired, Ct);
        await store.AddAsync(paid, Ct);
        await store.AddAsync(cancelled, Ct);
        await store.SettleAsync(StoreId, paidHash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);
        await store.CancelAsync(StoreId, cancelledHash, Ct);

        var listed = await store.ListAsync(StoreId, pendingOnly: true, offset: 0, limit: 50, Ct);

        Assert.Equal(PaymentFixture.PaymentHash, Assert.Single(listed).PaymentHash);
    }

    [Fact]
    public async Task Listing_pages_with_offset_and_limit()
    {
        var store = await CreateStoreAsync();
        for (var i = 0; i < 5; i++)
        {
            await store.AddAsync(
                NewRecord(i.ToString("x64"), createdAt: DateTimeOffset.UtcNow.AddMinutes(-i)), Ct);
        }

        var firstPage = await store.ListAsync(StoreId, pendingOnly: false, offset: 0, limit: 2, Ct);
        var secondPage = await store.ListAsync(StoreId, pendingOnly: false, offset: 2, limit: 2, Ct);

        Assert.Equal(2, firstPage.Count);
        Assert.Equal(2, secondPage.Count);
        Assert.Empty(firstPage.Select(r => r.PaymentHash).Intersect(secondPage.Select(r => r.PaymentHash)));
    }

    [Fact]
    public async Task Listing_does_not_leak_another_stores_invoices()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(PaymentFixture.PaymentHash), Ct);
        await store.AddAsync(NewRecord(PaymentFixture.OtherPaymentHash, storeId: OtherStoreId), Ct);

        var listed = await store.ListAsync(StoreId, pendingOnly: false, offset: 0, limit: 50, Ct);

        Assert.Equal(PaymentFixture.PaymentHash, Assert.Single(listed).PaymentHash);
    }

    [Fact]
    public async Task An_expired_but_unpaid_invoice_can_still_settle()
    {
        // The capability the computed-expiry design exists to preserve, asserted at the store boundary rather
        // than only through the non-production in-memory state machine. The service provider accepts a late
        // payment and Spark cannot stop it, so refusing it here would leave real money unattributed.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(
            createdAt: DateTimeOffset.UtcNow.AddHours(-2),
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-30)), Ct);

        var result = await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, PaymentFixture.Preimage,
            DateTimeOffset.UtcNow, Ct);

        Assert.Equal(InvoiceSettlementOutcome.Settled, result.Outcome);
        Assert.Equal(InvoiceRecordStatus.Paid, result.Record!.Status);
    }

    [Fact]
    public async Task Reconciliation_returns_settleable_invoices_oldest_first()
    {
        // Oldest-first, unlike ListAsync: the oldest are closest to falling out of the reconciliation window
        // entirely, so they are the ones that must not starve behind newer arrivals.
        var store = await CreateStoreAsync();
        var oldest = NewRecord("a".PadLeft(64, '0'), createdAt: DateTimeOffset.UtcNow.AddMinutes(-30));
        var middle = NewRecord("b".PadLeft(64, '0'), createdAt: DateTimeOffset.UtcNow.AddMinutes(-20));
        var newest = NewRecord("c".PadLeft(64, '0'), createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        await store.AddAsync(newest, Ct);
        await store.AddAsync(oldest, Ct);
        await store.AddAsync(middle, Ct);

        var listed = await store.ListForReconciliationAsync(
            StoreId, DateTimeOffset.UtcNow.AddHours(-1), after: null, limit: 10, Ct);

        Assert.Equal(
            [oldest.PaymentHash, middle.PaymentHash, newest.PaymentHash],
            listed.Select(r => r.PaymentHash).ToArray());
    }

    [Fact]
    public async Task Reconciliation_includes_recently_expired_invoices_but_not_older_ones()
    {
        var store = await CreateStoreAsync();
        var justExpired = NewRecord(
            PaymentFixture.PaymentHash,
            createdAt: DateTimeOffset.UtcNow.AddHours(-1),
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var longExpired = NewRecord(
            PaymentFixture.OtherPaymentHash,
            createdAt: DateTimeOffset.UtcNow.AddDays(-2),
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        await store.AddAsync(justExpired, Ct);
        await store.AddAsync(longExpired, Ct);

        var listed = await store.ListForReconciliationAsync(
            StoreId, DateTimeOffset.UtcNow.AddHours(-1), after: null, limit: 10, Ct);

        // Recently expired is still worth re-checking; a day-old one is a deliberate bound, not an oversight.
        Assert.Equal(justExpired.PaymentHash, Assert.Single(listed).PaymentHash);
    }

    [Fact]
    public async Task Reconciliation_excludes_paid_but_still_rechecks_cancelled_invoices()
    {
        // Paid is terminal. A cancelled invoice, by contrast, is still payable on the service provider, so
        // it must stay in the reconciliation set — a late payment of a cancelled invoice is exactly what the
        // reconciliation pass exists to catch when the SDK's completion event is dropped.
        var store = await CreateStoreAsync();
        var paidHash = "d".PadLeft(64, '0');
        var cancelledHash = "e".PadLeft(64, '0');
        await store.AddAsync(NewRecord(paidHash), Ct);
        await store.AddAsync(NewRecord(cancelledHash), Ct);
        await store.SettleAsync(StoreId, paidHash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);
        await store.CancelAsync(StoreId, cancelledHash, Ct);

        var listed = await store.ListForReconciliationAsync(
            StoreId, DateTimeOffset.UtcNow.AddHours(-1), after: null, limit: 10, Ct);

        Assert.Equal(cancelledHash, Assert.Single(listed).PaymentHash);
    }

    [Fact]
    public async Task Reconciliation_pages_by_keyset_and_is_stable_when_a_record_settles_mid_walk()
    {
        // Keyset, not offset. Settling a record removes it from this query's result set, so an offset-based
        // second page would skip whatever had shifted into the vacated slot.
        var store = await CreateStoreAsync();
        var hashes = Enumerable.Range(0, 6).Select(i => i.ToString("x64")).ToArray();
        for (var i = 0; i < hashes.Length; i++)
            await store.AddAsync(NewRecord(hashes[i], createdAt: DateTimeOffset.UtcNow.AddMinutes(-30 + i)), Ct);

        var firstPage = await store.ListForReconciliationAsync(
            StoreId, DateTimeOffset.UtcNow.AddHours(-1), after: null, limit: 2, Ct);
        Assert.Equal([hashes[0], hashes[1]], firstPage.Select(r => r.PaymentHash).ToArray());

        // Settle both of the first page, exactly as a reconciliation pass would.
        foreach (var record in firstPage)
        {
            await store.SettleAsync(
                StoreId, record.PaymentHash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);
        }

        var cursor = new InvoiceReconciliationCursor(firstPage[^1].CreatedAt, firstPage[^1].PaymentHash);
        var secondPage = await store.ListForReconciliationAsync(
            StoreId, DateTimeOffset.UtcNow.AddHours(-1), cursor, limit: 2, Ct);

        // Nothing skipped: the walk continues from where it was, not from a shifted offset.
        Assert.Equal([hashes[2], hashes[3]], secondPage.Select(r => r.PaymentHash).ToArray());
    }

    [Fact]
    public async Task Reconciliation_breaks_ties_on_identical_creation_times()
    {
        // Two invoices minted in the same tick must not sit either side of a page boundary forever, one of them
        // never examined. The cursor therefore carries the payment hash as well as the timestamp.
        var store = await CreateStoreAsync();
        var sameInstant = DateTimeOffset.UtcNow.AddMinutes(-5);
        var first = NewRecord("1".PadLeft(64, '0'), createdAt: sameInstant);
        var second = NewRecord("2".PadLeft(64, '0'), createdAt: sameInstant);
        await store.AddAsync(second, Ct);
        await store.AddAsync(first, Ct);

        var page = await store.ListForReconciliationAsync(
            StoreId, DateTimeOffset.UtcNow.AddHours(-1), after: null, limit: 1, Ct);
        var cursor = new InvoiceReconciliationCursor(page[0].CreatedAt, page[0].PaymentHash);
        var next = await store.ListForReconciliationAsync(
            StoreId, DateTimeOffset.UtcNow.AddHours(-1), cursor, limit: 1, Ct);

        Assert.Equal(first.PaymentHash, page[0].PaymentHash);
        Assert.Equal(second.PaymentHash, Assert.Single(next).PaymentHash);
    }

    [Fact]
    public async Task Reconciliation_does_not_leak_another_stores_invoices()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(PaymentFixture.PaymentHash), Ct);
        await store.AddAsync(NewRecord(PaymentFixture.OtherPaymentHash, storeId: OtherStoreId), Ct);

        var listed = await store.ListForReconciliationAsync(
            StoreId, DateTimeOffset.UtcNow.AddHours(-1), after: null, limit: 10, Ct);

        Assert.Equal(PaymentFixture.PaymentHash, Assert.Single(listed).PaymentHash);
    }
}

/// <summary>The contract against the in-memory implementation used by the rest of the suite.</summary>
public class InMemoryInvoiceRecordStoreTests : InvoiceRecordStoreContractTests
{
    protected override Task<IInvoiceRecordStore> CreateStoreAsync() =>
        Task.FromResult<IInvoiceRecordStore>(new InMemoryInvoiceRecordStore());
}

/// <summary>
/// The same contract against the production EF store and a real Postgres database.
/// </summary>
/// <remarks>
/// Opt-in via <c>SPARK_POSTGRES_TESTS</c> (a connection string). This is the suite that would have caught the
/// transaction-under-retry defect, because it exercises the real context factory — retry strategy and all.
/// </remarks>
[Trait("Category", "Postgres")]
[Collection(PostgresTestDatabase.CollectionName)]
public class PostgresInvoiceRecordStoreTests : InvoiceRecordStoreContractTests
{
    private readonly PostgresTestDatabase _database;

    public PostgresInvoiceRecordStoreTests(PostgresTestDatabase database) => _database = database;

    protected override async Task<IInvoiceRecordStore> CreateStoreAsync() =>
        new EfInvoiceRecordStore(await _database.CreateFactoryAsync());
}

/// <summary>Guards the shared preimage/payment-hash fixture itself.</summary>
public class PaymentFixtureTests
{
    [Fact]
    public void The_preimage_hashes_to_the_payment_hash()
    {
        // Compared against a literal produced out of band rather than against another call to the same hash
        // function, which would agree with itself no matter what. BTCPay recomputes sha256(preimage) and
        // silently discards a mismatch, so a broken pair here would make every settlement assertion hollow.
        Assert.Equal(PaymentFixture.KnownPaymentHashVector, PaymentFixture.PaymentHash);
        Assert.NotEqual(PaymentFixture.PaymentHash, PaymentFixture.OtherPaymentHash);
    }
}
