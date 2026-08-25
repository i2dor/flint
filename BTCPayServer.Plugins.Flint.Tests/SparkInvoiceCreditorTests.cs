using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Which BTCPay invoice a recorded settlement is credited to, and when it is refused.
/// </summary>
/// <remarks>
/// <para>
/// The decision this pins is the one that closes the superseded-BOLT11 hole: BTCPay's Lightning listener
/// watches only each invoice's current payment prompt, so a payment to a replaced BOLT11 after a restart
/// settles in this plugin and reaches no invoice unless it is routed there deliberately. The end-to-end proof
/// of that lives in <see cref="SparkSupersededInvoiceCreditTests"/>; what is here is the mapping from each
/// answer BTCPay can give to what this plugin does about it — including the two answers that must <b>not</b>
/// mark the credit done, because doing so would abandon real money.
/// </para>
/// <para>
/// Driven against <see cref="FakeInvoiceCreditGateway"/>, which models the two properties of BTCPay's schema
/// the design rests on: an insert-only payment-hash index, and a payments primary key that refuses a duplicate.
/// </para>
/// </remarks>
public class SparkInvoiceCreditorTests
{
    private const string StoreId = "store-1";
    private const string OtherStoreId = "store-2";
    private const string InvoiceId = "btcpay-invoice-1";

    private static readonly string Hash = PaymentFixture.PaymentHash;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (SparkInvoiceCreditor Creditor, InMemoryInvoiceRecordStore Store,
        FakeInvoiceCreditGateway Credits, CapturingLogger<SparkInvoiceCreditor> Log) Create()
    {
        var store = new InMemoryInvoiceRecordStore();
        var credits = new FakeInvoiceCreditGateway();
        var log = new CapturingLogger<SparkInvoiceCreditor>();
        return (new SparkInvoiceCreditor(credits, store, log), store, credits, log);
    }

    /// <summary>A settled record, as the settlement path leaves one.</summary>
    private static InvoiceRecord Settled(
        InMemoryInvoiceRecordStore store,
        string? paymentHash = null,
        string storeId = StoreId,
        DateTimeOffset? settledAt = null)
    {
        var record = new InvoiceRecord
        {
            PaymentHash = paymentHash ?? Hash,
            StoreId = storeId,
            Bolt11 = "lnbcrt-one",
            AmountMsat = 100_000,
            AmountReceivedMsat = 100_000,
            SdkPaymentId = "sdk-1",
            Preimage = PaymentFixture.Preimage,
            CreatedAt = (settledAt ?? DateTimeOffset.UtcNow).AddMinutes(-5),
            ExpiresAt = (settledAt ?? DateTimeOffset.UtcNow).AddHours(1),
            SettledAt = settledAt ?? DateTimeOffset.UtcNow,
            Status = InvoiceRecordStatus.Paid
        };
        store.Seed(record);
        return record;
    }

    [Fact]
    public async Task A_settlement_BTCPay_never_saw_is_credited_and_marked()
    {
        // The case the whole design exists for: BTCPay issued this BOLT11, then stopped watching it, and the
        // payment arrived anyway.
        var (creditor, store, credits, _) = Create();
        var record = Settled(store);
        credits.Mint(Hash, InvoiceId, StoreId);

        Assert.Equal(SparkInvoiceCreditResult.Credited, await creditor.CreditAsync(record, Ct));

        var credit = Assert.Single(credits.Credits);
        Assert.Equal(InvoiceId, credit.InvoiceId);
        Assert.Equal(Hash, credit.PaymentHash);
        // What arrived, never what was invoiced.
        Assert.Equal(100_000, credit.AmountReceivedMsat);
        Assert.Equal(PaymentFixture.Preimage, credit.Preimage);
        Assert.NotNull(store.Records[Hash].CreditedAt);
    }

    [Fact]
    public async Task A_settlement_BTCPay_already_holds_is_marked_without_a_second_insert()
    {
        // The ordinary path, and the one that runs on almost every checkout: core's listener was watching and
        // credited it. Marking it here is what stops every later pass asking again.
        var (creditor, store, credits, _) = Create();
        var record = Settled(store);
        credits.Mint(Hash, InvoiceId, StoreId).CreditedByBTCPay(Hash);

        Assert.Equal(SparkInvoiceCreditResult.AlreadyRecorded, await creditor.CreditAsync(record, Ct));

        Assert.Empty(credits.Attempts);
        Assert.Empty(credits.Credits);
        Assert.NotNull(store.Records[Hash].CreditedAt);
    }

    [Fact]
    public async Task Crediting_twice_records_the_payment_once()
    {
        // Exactly-once, asserted through the mechanism that provides it rather than around it: the second
        // attempt is refused by the payments primary key, not by this plugin remembering it tried.
        var (creditor, store, credits, _) = Create();
        var record = Settled(store);
        credits.Mint(Hash, InvoiceId, StoreId);

        Assert.Equal(SparkInvoiceCreditResult.Credited, await creditor.CreditAsync(record, Ct));

        // As a fresh pass would see it: the stamp is on the row, so the credit is not attempted again at all.
        Assert.Equal(SparkInvoiceCreditResult.AlreadyCredited,
            await creditor.CreditAsync(store.Records[Hash], Ct));

        // And even with the stamp cleared — a crash between the insert and the mark — the insert is refused.
        store.Records[Hash].CreditedAt = null;
        Assert.Equal(SparkInvoiceCreditResult.AlreadyRecorded,
            await creditor.CreditAsync(store.Records[Hash], Ct));

        Assert.Single(credits.Credits);
        Assert.Single(credits.CreditsFor(InvoiceId));
    }

    [Fact]
    public async Task Another_stores_invoice_is_never_credited()
    {
        // The security boundary. BTCPay's index is keyed on the payment hash alone, so a hash that resolved to
        // another store's invoice would credit that store for money that arrived in this one's wallet. Nothing
        // in the normal flow produces it, which is why it is checked rather than assumed — and nothing is
        // marked, so if the mismatch is ever explained the credit is still owed.
        var (creditor, store, credits, log) = Create();
        var record = Settled(store);
        credits.Mint(Hash, InvoiceId, OtherStoreId);

        Assert.Equal(SparkInvoiceCreditResult.RefusedCrossStore, await creditor.CreditAsync(record, Ct));

        Assert.Empty(credits.Attempts);
        Assert.Null(store.Records[Hash].CreditedAt);
        Assert.Contains("Refusing to credit", log.AllText);
        Assert.Contains(OtherStoreId, log.AllText);
    }

    [Fact]
    public async Task A_hash_BTCPay_has_not_indexed_yet_is_left_for_the_next_pass()
    {
        // BTCPay writes its payment-hash row just after CreateInvoice returns, so a payment that lands in that
        // window finds nothing. Leaving the record uncredited *is* the retry queue.
        var (creditor, store, credits, _) = Create();
        var record = Settled(store);

        Assert.Equal(SparkInvoiceCreditResult.Deferred, await creditor.CreditAsync(record, Ct));
        Assert.Null(store.Records[Hash].CreditedAt);

        // …and the next pass, once BTCPay has caught up, credits it.
        credits.Mint(Hash, InvoiceId, StoreId);
        Assert.Equal(SparkInvoiceCreditResult.Credited, await creditor.CreditAsync(record, Ct));
        Assert.NotNull(store.Records[Hash].CreditedAt);
    }

    [Fact]
    public async Task A_settlement_past_the_retry_horizon_stops_being_retried_and_says_so()
    {
        // A BOLT11 that was never issued for a BTCPay invoice — minted through this plugin's own API, say —
        // can never be credited. It is reported once at operator level rather than retried forever, and it is
        // deliberately not marked credited: it was not.
        var (creditor, store, credits, log) = Create();
        var record = Settled(store, settledAt: DateTimeOffset.UtcNow - SparkInvoiceCreditor.CreditRetryHorizon
            - TimeSpan.FromDays(1));

        Assert.Equal(SparkInvoiceCreditResult.Abandoned, await creditor.CreditAsync(record, Ct));

        Assert.Empty(credits.Credits);
        // The two stamps say different things and only one of them is true here: the money is in the wallet and
        // no BTCPay invoice records it, so claiming a credit would misreport the merchant's position.
        Assert.Null(store.Records[Hash].CreditedAt);
        Assert.NotNull(store.Records[Hash].CreditAbandonedAt);
        Assert.Contains("no longer be retried", log.AllText);
        // With the figures a human needs to find the money by hand.
        Assert.Contains(Hash, log.AllText);
        Assert.Contains("100", log.AllText);
    }

    [Fact]
    public async Task A_settlement_already_given_up_on_is_not_reported_again()
    {
        // The other half of "reported once". The stamp is what removes the record from the walk's listing, but a
        // caller holding the record — the settlement path does — must also stop, and stop silently: repeating an
        // operator warning on every pass for the life of the server buries it.
        var (creditor, store, credits, log) = Create();
        var record = Settled(store, settledAt: DateTimeOffset.UtcNow - SparkInvoiceCreditor.CreditRetryHorizon
            - TimeSpan.FromDays(1));

        Assert.Equal(SparkInvoiceCreditResult.Abandoned, await creditor.CreditAsync(record, Ct));
        var lookups = credits.Lookups;
        var reported = log.Lines.Count;

        Assert.Equal(SparkInvoiceCreditResult.AlreadyAbandoned, await creditor.CreditAsync(record, Ct));

        // Not even asked about again, and not logged about again.
        Assert.Equal(lookups, credits.Lookups);
        Assert.Equal(reported, log.Lines.Count);
    }

    [Fact]
    public async Task An_invoice_that_cannot_hold_the_payment_is_marked_and_reported()
    {
        // A payment prompt is never removed from an invoice's blob, so retrying cannot change this answer.
        // Stamped terminal to stop an endless retry — as abandoned rather than credited, because the money is
        // not on the invoice — and logged loudly because only a human can reconcile it.
        var (creditor, store, credits, log) = Create();
        var record = Settled(store);
        credits.Mint(Hash, InvoiceId, StoreId);
        credits.PromptMissingFor.Add(Hash);

        Assert.Equal(SparkInvoiceCreditResult.Unrecordable, await creditor.CreditAsync(record, Ct));

        Assert.Empty(credits.Credits);
        Assert.Null(store.Records[Hash].CreditedAt);
        Assert.NotNull(store.Records[Hash].CreditAbandonedAt);
        Assert.Contains("cannot be credited automatically", log.AllText);
    }

    [Fact]
    public async Task The_preimage_reaches_the_payment_prompt_of_the_bolt11_that_was_paid()
    {
        // Not decoration. LUD-21 verify serves proof-of-payment out of the *prompt*, not out of the payment
        // row, and this plugin forces LUD-21 on for every store it provisions. Core's own listener performs
        // this backfill only when its own insert wins — and this path usually wins the race from it — so
        // skipping it would have cost every ordinary Flint checkout its proof-of-payment.
        var (creditor, store, credits, _) = Create();
        var record = Settled(store);
        credits.Mint(Hash, InvoiceId, StoreId);

        Assert.Equal(SparkInvoiceCreditResult.Credited, await creditor.CreditAsync(record, Ct));

        Assert.Equal(PaymentFixture.Preimage, credits.PromptPreimageFor(InvoiceId));
    }

    [Fact]
    public async Task Crediting_a_superseded_bolt11_leaves_the_replacements_prompt_alone()
    {
        // The other side of the same guard, and the reason it is a guard rather than an unconditional copy.
        // BTCPay's hash index still points X at this invoice, but the invoice's prompt now offers replacement
        // Y — whose preimage this is not. Stamping it there would have LUD-21 verify hand a payer a proof of
        // payment for an invoice they never paid.
        var (creditor, store, credits, _) = Create();
        var record = Settled(store);
        credits.Mint(Hash, InvoiceId, StoreId);
        credits.Mint(PaymentFixture.OtherPaymentHash, InvoiceId, StoreId);
        Assert.Equal(PaymentFixture.OtherPaymentHash, credits.PromptPaymentHashFor(InvoiceId));

        Assert.Equal(SparkInvoiceCreditResult.Credited, await creditor.CreditAsync(record, Ct));

        // The payment is on the invoice…
        Assert.Equal(Hash, Assert.Single(credits.Credits).PaymentHash);
        // …and the replacement's prompt is untouched.
        Assert.Null(credits.PromptPreimageFor(InvoiceId));
    }

    [Fact]
    public async Task A_gateway_failure_leaves_the_credit_owed_rather_than_throwing()
    {
        // Both callers are paths that must not be derailed: the settlement is already committed and its
        // notification already published by the time this runs, and the reconciliation pass has other stores
        // behind it. So a BTCPay database that is briefly unreachable costs a pass, not a settlement.
        var (creditor, store, credits, log) = Create();
        var record = Settled(store);
        credits.Mint(Hash, InvoiceId, StoreId);
        credits.FailWith = new InvalidOperationException("BTCPay's database is unreachable");

        Assert.Equal(SparkInvoiceCreditResult.Failed, await creditor.CreditAsync(record, Ct));
        Assert.Null(store.Records[Hash].CreditedAt);
        Assert.Contains("will be retried", log.AllText);

        credits.FailWith = null;
        Assert.Equal(SparkInvoiceCreditResult.Credited, await creditor.CreditAsync(record, Ct));
    }

    [Fact]
    public async Task An_unsettled_record_is_refused_outright()
    {
        // A caller that got here with an unpaid row has a bug, and the safe answer is to do nothing loudly:
        // marking anything would suppress the credit of the payment that may still arrive.
        var (creditor, store, _, _) = Create();
        var record = Settled(store);
        record.Status = InvoiceRecordStatus.Unpaid;

        await Assert.ThrowsAsync<ArgumentException>(() => creditor.CreditAsync(record, Ct));
    }

    [Fact]
    public async Task An_LNURL_indexed_hash_is_credited_under_the_payment_method_it_was_found_on()
    {
        // BTCPay indexes an LNURL prompt's hash under BTC-LNURL, not BTC-LN, and the payments primary key is
        // (id, payment method). Crediting under the wrong one would insert a second payment for the same money
        // rather than colliding with core's.
        var (creditor, store, credits, _) = Create();
        var record = Settled(store);
        credits.Mint(Hash, InvoiceId, StoreId, FakeInvoiceCreditGateway.LnurlPaymentMethodId);

        Assert.Equal(SparkInvoiceCreditResult.Credited, await creditor.CreditAsync(record, Ct));

        Assert.Equal(
            FakeInvoiceCreditGateway.LnurlPaymentMethodId, Assert.Single(credits.Credits).PaymentMethodId);
    }
}
