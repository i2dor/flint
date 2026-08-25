using BTCPayServer.Plugins.Flint.Data;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The reference semantics of the invoice-record state machine.
/// </summary>
/// <remarks>
/// Scope note, because it would otherwise be misleading: production settlement does <b>not</b> go through
/// <see cref="InvoiceRecord.TrySettle"/> — <see cref="EfInvoiceRecordStore"/> expresses the same transitions as
/// conditional SQL, and the in-memory store used by the rest of this suite delegates here. The transitions
/// themselves are therefore asserted against both implementations in
/// <see cref="InvoiceRecordStoreContractTests"/>, and what is left here is what only this type can answer:
/// the computed-expiry semantics, and the argument guards.
/// </remarks>
public class InvoiceRecordTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static InvoiceRecord Unpaid(long? amountMsat = 100_000) => new()
    {
        PaymentHash = new string('a', 64),
        StoreId = "store-1",
        Bolt11 = "lnbcrt1",
        AmountMsat = amountMsat,
        CreatedAt = Created,
        ExpiresAt = Created.AddHours(1),
        Status = InvoiceRecordStatus.Unpaid
    };

    [Fact]
    public void Cancelling_twice_reports_that_only_the_first_call_changed_anything()
    {
        // "Did anything change", not "is it cancelled now". The store's conditional UPDATE evaluates exactly
        // this predicate (WHERE Status = Unpaid), so the two must agree or the in-memory and Postgres
        // implementations would diverge and every client-level test built on the former would be misleading.
        var record = Unpaid();

        Assert.True(record.TryCancel());
        Assert.False(record.TryCancel());
        Assert.Equal(InvoiceRecordStatus.Expired, record.Status);
    }

    [Fact]
    public void A_cancelled_invoice_reports_unpaid_until_it_settles()
    {
        // Cancellation marks the invoice locally but cannot withdraw it from the service provider, so it
        // stays payable. Reporting it expired would make BTCPay's listener drop it — the listener that
        // would deliver the late payment's credit — so it must read unpaid until a payment settles it.
        var record = Unpaid();
        Assert.True(record.TryCancel());

        Assert.Equal(InvoiceRecordStatus.Expired, record.Status);
        Assert.Equal(InvoiceRecordStatus.Unpaid, record.EffectiveStatus(record.ExpiresAt.AddDays(1)));

        Assert.Equal(InvoiceSettlementOutcome.Settled, record.TrySettle("sdk-1", 100_000, null, Created));
        Assert.Equal(InvoiceRecordStatus.Paid, record.Status);
        Assert.Equal(InvoiceRecordStatus.Paid, record.EffectiveStatus(record.ExpiresAt.AddYears(1)));
    }

    [Fact]
    public void A_naturally_expired_invoice_still_settles()
    {
        // The SSP will accept a payment after expiry and Spark has no way to stop it. Dropping that payment
        // silently would lose the merchant money; BTCPay handles a late payment on its own.
        var record = Unpaid();
        var late = record.ExpiresAt.AddMinutes(30);

        Assert.Equal(InvoiceRecordStatus.Expired, record.EffectiveStatus(late));
        Assert.Equal(InvoiceSettlementOutcome.Settled, record.TrySettle("sdk-1", 100_000, null, late));
        Assert.Equal(InvoiceRecordStatus.Paid, record.Status);
    }

    [Fact]
    public void A_paid_invoice_never_reports_as_expired()
    {
        var record = Unpaid();
        record.TrySettle("sdk-1", 100_000, null, Created);

        Assert.Equal(InvoiceRecordStatus.Paid, record.EffectiveStatus(record.ExpiresAt.AddYears(1)));
    }

    [Fact]
    public void Only_the_first_call_marks_a_settlement_credited()
    {
        // "Did anything change", the same predicate the store's conditional UPDATE evaluates
        // (WHERE Status = Paid AND CreditedAt IS NULL). The timestamp records when the merchant's BTCPay
        // invoice was actually credited, so a second caller must not move it.
        var record = Unpaid();
        record.TrySettle("sdk-1", 100_000, null, Created);
        var first = Created.AddMinutes(1);

        Assert.True(record.TryMarkCredited(first));
        Assert.False(record.TryMarkCredited(Created.AddHours(5)));
        Assert.Equal(first, record.CreditedAt);
    }

    [Fact]
    public void An_unsettled_invoice_cannot_be_marked_credited()
    {
        // The interlock that stops a credit being suppressed before it is owed: if an unpaid row could be
        // stamped, the payment that later arrived would be settled and then never routed to BTCPay, because
        // every retry pass would consider the credit already done.
        var unpaid = Unpaid();
        Assert.False(unpaid.TryMarkCredited(Created));
        Assert.Null(unpaid.CreditedAt);

        // Including a cancelled one, which is still payable on the service provider.
        var cancelled = Unpaid();
        Assert.True(cancelled.TryCancel());
        Assert.False(cancelled.TryMarkCredited(Created));
        Assert.Null(cancelled.CreditedAt);
    }

    [Fact]
    public void Only_the_first_call_marks_a_settlement_abandoned()
    {
        // Same shape as the credit stamp, and the reason the return value is used rather than ignored: the
        // operator warning about money that no BTCPay invoice accounts for is emitted by whoever wins this
        // compare-and-set, so a second winner would mean a second report — on every pass, forever.
        var record = Unpaid();
        record.TrySettle("sdk-1", 100_000, null, Created);
        var first = Created.AddMinutes(1);

        Assert.True(record.TryMarkCreditAbandoned(first));
        Assert.False(record.TryMarkCreditAbandoned(Created.AddHours(5)));
        Assert.Equal(first, record.CreditAbandonedAt);
    }

    [Fact]
    public void An_unsettled_invoice_cannot_be_marked_abandoned()
    {
        // The same interlock as the credit stamp: an unpaid row has no settlement to give up on, and stamping
        // one would permanently suppress the credit of the payment that later arrives.
        var unpaid = Unpaid();
        Assert.False(unpaid.TryMarkCreditAbandoned(Created));
        Assert.Null(unpaid.CreditAbandonedAt);

        var cancelled = Unpaid();
        Assert.True(cancelled.TryCancel());
        Assert.False(cancelled.TryMarkCreditAbandoned(Created));
        Assert.Null(cancelled.CreditAbandonedAt);
    }

    [Fact]
    public void A_credited_settlement_is_never_relabelled_abandoned()
    {
        // The race the two stamps are guarded against. Two passes can be examining the same row; if one credits
        // it while the other is about to give up on it, the row has to keep saying the money arrived — otherwise
        // the operator is sent looking for funds that were in fact accounted for.
        var record = Unpaid();
        record.TrySettle("sdk-1", 100_000, null, Created);
        Assert.True(record.TryMarkCredited(Created.AddMinutes(1)));

        Assert.False(record.TryMarkCreditAbandoned(Created.AddMinutes(2)));
        Assert.Null(record.CreditAbandonedAt);
        Assert.NotNull(record.CreditedAt);
    }

    [Fact]
    public void Settling_rejects_nonsensical_input()
    {
        Assert.Throws<ArgumentException>(() => Unpaid().TrySettle("", 1, null, Created));
        Assert.Throws<ArgumentOutOfRangeException>(() => Unpaid().TrySettle("sdk-1", -1, null, Created));
    }
}
