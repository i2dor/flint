using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Services;
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

    // -------------------------------------------------------------------------------------------------------
    // Crediting: whether a settlement has reached the BTCPay invoice its BOLT11 was minted for
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Marking_credited_succeeds_once_and_keeps_the_first_timestamp()
    {
        // Two callers genuinely race here — the settlement path and the reconciliation pass both attempt the
        // credit for the same row — and only one of them may be told it stamped it. The timestamp is when the
        // merchant's invoice was credited, not when a pass last looked.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);
        await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);
        var first = DateTimeOffset.UtcNow;

        Assert.True(await store.MarkCreditedAsync(StoreId, PaymentFixture.PaymentHash, first, Ct));
        Assert.False(await store.MarkCreditedAsync(
            StoreId, PaymentFixture.PaymentHash, DateTimeOffset.UtcNow.AddHours(1), Ct));

        var reread = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.NotNull(reread!.CreditedAt);
        Assert.Equal(first.ToUnixTimeMilliseconds(), reread.CreditedAt!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Marking_an_unsettled_invoice_credited_is_refused()
    {
        // The interlock. Stamping an unpaid row would tell every later pass the credit is done, and the payment
        // that eventually arrives would settle here and never reach the merchant's BTCPay invoice. A cancelled
        // invoice is refused for the same reason: on Spark it is still payable.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);
        Assert.False(await store.MarkCreditedAsync(
            StoreId, PaymentFixture.PaymentHash, DateTimeOffset.UtcNow, Ct));

        Assert.True(await store.CancelAsync(StoreId, PaymentFixture.PaymentHash, Ct));
        Assert.False(await store.MarkCreditedAsync(
            StoreId, PaymentFixture.PaymentHash, DateTimeOffset.UtcNow, Ct));

        var reread = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.Null(reread!.CreditedAt);
    }

    [Fact]
    public async Task Marking_credited_is_scoped_to_the_owning_store()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);
        await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);

        Assert.False(await store.MarkCreditedAsync(
            OtherStoreId, PaymentFixture.PaymentHash, DateTimeOffset.UtcNow, Ct));
        Assert.False(await store.MarkCreditedAsync(
            StoreId, PaymentFixture.OtherPaymentHash, DateTimeOffset.UtcNow, Ct));

        var reread = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.Null(reread!.CreditedAt);
    }

    [Fact]
    public async Task A_settlement_stays_uncredited_until_it_is_marked()
    {
        // This is the retry queue, so what it contains decides what can still be recovered. A settled row is in
        // it from the moment it settles until the credit lands — including one settled long after expiry, which
        // is exactly the case BTCPay's listener is no longer watching for.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(
            createdAt: DateTimeOffset.UtcNow.AddHours(-5),
            expiresAt: DateTimeOffset.UtcNow.AddHours(-4)), Ct);
        await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);

        var pending = await store.ListUncreditedAsync(
            StoreId, DateTimeOffset.UtcNow.AddDays(-7), after: null, limit: 10, Ct);
        Assert.Equal(PaymentFixture.PaymentHash, Assert.Single(pending).PaymentHash);

        Assert.True(await store.MarkCreditedAsync(
            StoreId, PaymentFixture.PaymentHash, DateTimeOffset.UtcNow, Ct));

        Assert.Empty(await store.ListUncreditedAsync(
            StoreId, DateTimeOffset.UtcNow.AddDays(-7), after: null, limit: 10, Ct));
    }

    [Fact]
    public async Task Uncredited_excludes_invoices_that_never_settled()
    {
        // An unpaid or cancelled invoice has no settlement to credit. Including one would have every pass
        // attempt a credit for money that has not arrived.
        var store = await CreateStoreAsync();
        var cancelledHash = "e".PadLeft(64, '0');
        await store.AddAsync(NewRecord(), Ct);
        await store.AddAsync(NewRecord(cancelledHash), Ct);
        await store.CancelAsync(StoreId, cancelledHash, Ct);

        Assert.Empty(await store.ListUncreditedAsync(
            StoreId, DateTimeOffset.UtcNow.AddDays(-7), after: null, limit: 10, Ct));
    }

    [Fact]
    public async Task Uncredited_stops_at_the_listing_bound()
    {
        // The bound on the size of the set, not on the retry: a settlement old enough that even the abandoned
        // report has had its chance is no longer worth a query on every pass for the life of the server. The
        // caller passes SparkInvoiceCreditor.ListableFrom here, which is deliberately older than the retry
        // horizon — see the interface's remarks for why those two must not be the same value.
        var store = await CreateStoreAsync();
        var recentHash = PaymentFixture.PaymentHash;
        var ancientHash = PaymentFixture.OtherPaymentHash;
        await store.AddAsync(NewRecord(recentHash), Ct);
        await store.AddAsync(NewRecord(ancientHash, createdAt: DateTimeOffset.UtcNow.AddDays(-30)), Ct);
        await store.SettleAsync(StoreId, recentHash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);
        await store.SettleAsync(
            StoreId, ancientHash, "sdk-2", 100_000, null, DateTimeOffset.UtcNow.AddDays(-30), Ct);

        var listed = await store.ListUncreditedAsync(
            StoreId, DateTimeOffset.UtcNow.AddDays(-7), after: null, limit: 10, Ct);

        Assert.Equal(recentHash, Assert.Single(listed).PaymentHash);
    }

    [Fact]
    public async Task Uncredited_still_lists_a_settlement_past_the_retry_horizon()
    {
        // The regression this exists for. A record has to stay listed *after* the credit retry horizon passes,
        // or no pass can ever classify it as abandoned: it would leave the walk at the instant it became
        // eligible to be reported, so the operator warning would never fire and the row would sit with both
        // credit columns null forever — indistinguishable from one still in flight.
        var store = await CreateStoreAsync();
        var settledAt = DateTimeOffset.UtcNow - SparkInvoiceCreditor.CreditRetryHorizon - TimeSpan.FromDays(1);
        await store.AddAsync(NewRecord(createdAt: settledAt.AddMinutes(-5)), Ct);
        await store.SettleAsync(StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, null, settledAt, Ct);

        var listed = await store.ListUncreditedAsync(
            StoreId,
            SparkInvoiceCreditor.ListableFrom(DateTimeOffset.UtcNow),
            after: null,
            limit: 10,
            Ct);

        Assert.Equal(PaymentFixture.PaymentHash, Assert.Single(listed).PaymentHash);
    }

    [Fact]
    public async Task An_abandoned_settlement_leaves_the_uncredited_set_without_claiming_a_credit()
    {
        // The terminal marker, and the reason it is a column of its own. Stamping CreditedAt would have removed
        // the record from the walk too — and told every later reader that the merchant had been paid on an
        // invoice that in fact records nothing.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);
        await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);

        Assert.True(await store.MarkCreditAbandonedAsync(
            StoreId, PaymentFixture.PaymentHash, DateTimeOffset.UtcNow, Ct));

        Assert.Empty(await store.ListUncreditedAsync(
            StoreId, DateTimeOffset.UtcNow.AddDays(-14), after: null, limit: 10, Ct));

        var record = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.NotNull(record);
        Assert.Null(record.CreditedAt);
        Assert.NotNull(record.CreditAbandonedAt);
    }

    [Fact]
    public async Task Abandoning_a_settlement_twice_stamps_it_once()
    {
        // What makes the operator report exactly-once: the caller logs only when this compare-and-set says it
        // was the one that stamped the row.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);
        await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);
        var first = DateTimeOffset.UtcNow.AddMinutes(-5);

        Assert.True(await store.MarkCreditAbandonedAsync(StoreId, PaymentFixture.PaymentHash, first, Ct));
        Assert.False(await store.MarkCreditAbandonedAsync(
            StoreId, PaymentFixture.PaymentHash, DateTimeOffset.UtcNow, Ct));

        var record = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.NotNull(record?.CreditAbandonedAt);
        Assert.Equal(first.ToUnixTimeMilliseconds(), record.CreditAbandonedAt.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task An_already_credited_settlement_is_never_labelled_abandoned()
    {
        // The race that matters. Two passes can be examining the same row; if one credits it while the other is
        // about to give up on it, the row must keep saying the money arrived. Otherwise the operator is told to
        // go looking for funds that were in fact accounted for.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);
        await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);
        Assert.True(await store.MarkCreditedAsync(
            StoreId, PaymentFixture.PaymentHash, DateTimeOffset.UtcNow, Ct));

        Assert.False(await store.MarkCreditAbandonedAsync(
            StoreId, PaymentFixture.PaymentHash, DateTimeOffset.UtcNow, Ct));

        var record = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.NotNull(record?.CreditedAt);
        Assert.Null(record.CreditAbandonedAt);
    }

    [Fact]
    public async Task An_unsettled_invoice_is_never_labelled_abandoned()
    {
        // Same interlock as the credit stamp, and for the same reason: an unpaid row has no settlement to give
        // up on, and stamping one would permanently suppress the credit of the payment that later arrives.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);

        Assert.False(await store.MarkCreditAbandonedAsync(
            StoreId, PaymentFixture.PaymentHash, DateTimeOffset.UtcNow, Ct));
    }

    [Fact]
    public async Task Abandoning_is_scoped_to_the_store_that_owns_the_settlement()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(), Ct);
        await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);

        Assert.False(await store.MarkCreditAbandonedAsync(
            OtherStoreId, PaymentFixture.PaymentHash, DateTimeOffset.UtcNow, Ct));

        var record = await store.GetAsync(StoreId, PaymentFixture.PaymentHash, Ct);
        Assert.Null(record?.CreditAbandonedAt);
    }

    [Fact]
    public async Task The_stores_awaiting_a_credit_are_listed_without_needing_a_running_wallet()
    {
        // What lets the credit walk reach a store whose Spark connection is broken. Crediting touches only
        // BTCPay's tables and these rows, so the store id is the only thing the walk needs — and this is where
        // it comes from, rather than from the set of live SDK instances the settlement walk is limited to.
        var store = await CreateStoreAsync();
        var credited = "0".PadLeft(64, 'a');
        await store.AddAsync(NewRecord(PaymentFixture.PaymentHash), Ct);
        await store.AddAsync(NewRecord(PaymentFixture.OtherPaymentHash, storeId: OtherStoreId), Ct);
        await store.AddAsync(NewRecord(credited, storeId: "store-3"), Ct);
        foreach (var (hash, owner) in new[]
                 {
                     (PaymentFixture.PaymentHash, StoreId),
                     (PaymentFixture.OtherPaymentHash, OtherStoreId),
                     (credited, "store-3")
                 })
        {
            await store.SettleAsync(owner, hash, $"sdk-{hash[..4]}", 100_000, null, DateTimeOffset.UtcNow, Ct);
        }

        // One of the three has been resolved, so its store is no longer awaiting anything.
        Assert.True(await store.MarkCreditedAsync("store-3", credited, DateTimeOffset.UtcNow, Ct));

        var storeIds = await store.ListStoreIdsAwaitingCreditAsync(
            DateTimeOffset.UtcNow.AddDays(-14), limit: 10, Ct);

        Assert.Equal(2, storeIds.Count);
        Assert.Contains(StoreId, storeIds);
        Assert.Contains(OtherStoreId, storeIds);
    }

    [Fact]
    public async Task A_store_is_listed_once_however_many_settlements_it_is_owed()
    {
        // Distinct, because the caller feeds this to a store-pass scheduler that walks ids: a repeated id would
        // be a repeated visit, and the visit's own paging already covers every one of the store's rows.
        var store = await CreateStoreAsync();
        var hashes = Enumerable.Range(0, 3).Select(i => i.ToString("x64")).ToArray();
        foreach (var hash in hashes)
        {
            await store.AddAsync(NewRecord(hash), Ct);
            await store.SettleAsync(StoreId, hash, $"sdk-{hash[..4]}", 100_000, null, DateTimeOffset.UtcNow, Ct);
        }

        var storeIds = await store.ListStoreIdsAwaitingCreditAsync(
            DateTimeOffset.UtcNow.AddDays(-14), limit: 10, Ct);

        Assert.Equal(StoreId, Assert.Single(storeIds));
    }

    [Fact]
    public async Task The_stores_awaiting_a_credit_exclude_resolved_and_aged_out_settlements()
    {
        // The same predicate as the per-store listing, asserted separately because the two are different
        // queries: an abandoned or aged-out row must not keep pulling its store into every pass.
        var store = await CreateStoreAsync();
        var abandoned = PaymentFixture.PaymentHash;
        var ancient = PaymentFixture.OtherPaymentHash;
        await store.AddAsync(NewRecord(abandoned), Ct);
        await store.AddAsync(NewRecord(ancient, storeId: OtherStoreId,
            createdAt: DateTimeOffset.UtcNow.AddDays(-30)), Ct);
        await store.SettleAsync(StoreId, abandoned, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);
        await store.SettleAsync(
            OtherStoreId, ancient, "sdk-2", 100_000, null, DateTimeOffset.UtcNow.AddDays(-30), Ct);
        Assert.True(await store.MarkCreditAbandonedAsync(StoreId, abandoned, DateTimeOffset.UtcNow, Ct));

        Assert.Empty(await store.ListStoreIdsAwaitingCreditAsync(
            DateTimeOffset.UtcNow.AddDays(-14), limit: 10, Ct));
    }

    [Fact]
    public async Task Uncredited_pages_by_keyset_oldest_first_and_is_stable_when_a_record_is_credited()
    {
        // Keyset for the same reason the reconciliation walk uses one: crediting a record removes it from this
        // result set, so an offset-based second page would skip whatever shifted into the vacated slot. Oldest
        // first because the oldest settlement is the closest to its retry horizon.
        var store = await CreateStoreAsync();
        var hashes = Enumerable.Range(0, 4).Select(i => i.ToString("x64")).ToArray();
        for (var i = 0; i < hashes.Length; i++)
        {
            await store.AddAsync(NewRecord(hashes[i], createdAt: DateTimeOffset.UtcNow.AddMinutes(-30 + i)), Ct);
            await store.SettleAsync(StoreId, hashes[i], $"sdk-{i}", 100_000, null, DateTimeOffset.UtcNow, Ct);
        }

        var firstPage = await store.ListUncreditedAsync(
            StoreId, DateTimeOffset.UtcNow.AddDays(-7), after: null, limit: 2, Ct);
        Assert.Equal([hashes[0], hashes[1]], firstPage.Select(r => r.PaymentHash).ToArray());

        foreach (var record in firstPage)
            await store.MarkCreditedAsync(StoreId, record.PaymentHash, DateTimeOffset.UtcNow, Ct);

        var cursor = new InvoiceReconciliationCursor(firstPage[^1].CreatedAt, firstPage[^1].PaymentHash);
        var secondPage = await store.ListUncreditedAsync(
            StoreId, DateTimeOffset.UtcNow.AddDays(-7), cursor, limit: 2, Ct);

        Assert.Equal([hashes[2], hashes[3]], secondPage.Select(r => r.PaymentHash).ToArray());
    }

    [Fact]
    public async Task Uncredited_does_not_leak_another_stores_settlements()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord(PaymentFixture.PaymentHash), Ct);
        await store.AddAsync(NewRecord(PaymentFixture.OtherPaymentHash, storeId: OtherStoreId), Ct);
        await store.SettleAsync(
            StoreId, PaymentFixture.PaymentHash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);
        await store.SettleAsync(
            OtherStoreId, PaymentFixture.OtherPaymentHash, "sdk-2", 100_000, null, DateTimeOffset.UtcNow, Ct);

        var listed = await store.ListUncreditedAsync(
            StoreId, DateTimeOffset.UtcNow.AddDays(-7), after: null, limit: 10, Ct);

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
