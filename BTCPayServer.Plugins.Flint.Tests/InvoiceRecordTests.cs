using BTCPayServer.Plugins.Flint.Data;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The status an invoice record reports to BTCPay: <see cref="InvoiceRecord.EffectiveStatus"/>.
/// </summary>
/// <remarks>
/// Scope note, because it would otherwise be misleading: the record's state transitions — settling,
/// cancelling, the credit and abandon stamps — belong to <see cref="IInvoiceRecordStore"/>, not to this type.
/// <see cref="EfInvoiceRecordStore"/> expresses them as conditional SQL, and they are asserted against both it
/// and the in-memory store in <see cref="InvoiceRecordStoreContractTests"/>. What is left here is what only this
/// type can answer: how a persisted status plus the clock map to what BTCPay is told. The states are set
/// directly because that mapping is all these tests are about.
/// </remarks>
public class InvoiceRecordTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static InvoiceRecord Record(InvoiceRecordStatus status) => new()
    {
        PaymentHash = new string('a', 64),
        StoreId = "store-1",
        Bolt11 = "lnbcrt1",
        AmountMsat = 100_000,
        CreatedAt = Created,
        ExpiresAt = Created.AddHours(1),
        Status = status
    };

    [Fact]
    public void A_cancelled_invoice_reports_unpaid_until_it_settles()
    {
        // Cancellation marks the invoice locally but cannot withdraw it from the service provider, so it
        // stays payable. Reporting it expired would make BTCPay's listener drop it — the listener that
        // would deliver the late payment's credit — so it must read unpaid until a payment settles it.
        var record = Record(InvoiceRecordStatus.Expired);
        Assert.Equal(InvoiceRecordStatus.Unpaid, record.EffectiveStatus(record.ExpiresAt.AddDays(1)));

        record.Status = InvoiceRecordStatus.Paid;
        Assert.Equal(InvoiceRecordStatus.Paid, record.EffectiveStatus(record.ExpiresAt.AddYears(1)));
    }

    [Fact]
    public void A_naturally_expired_invoice_reports_expired_without_being_persisted_as_expired()
    {
        // Natural expiry is computed, never written: the persisted Expired status means "cancelled", and the
        // SSP will still accept a payment after expiry, which the store must be able to settle
        // (InvoiceRecordStoreContractTests.An_expired_but_unpaid_invoice_can_still_settle).
        var record = Record(InvoiceRecordStatus.Unpaid);

        Assert.Equal(InvoiceRecordStatus.Expired, record.EffectiveStatus(record.ExpiresAt.AddMinutes(30)));
        Assert.Equal(InvoiceRecordStatus.Unpaid, record.Status);
    }

    [Fact]
    public void A_paid_invoice_never_reports_as_expired()
    {
        var record = Record(InvoiceRecordStatus.Paid);

        Assert.Equal(InvoiceRecordStatus.Paid, record.EffectiveStatus(record.ExpiresAt.AddYears(1)));
    }
}
