using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// The plugin's own payment-hash → BTCPay invoice association, written from core's
/// <c>InvoiceNewPaymentDetailsEvent</c> and read as the credit gate­way's fallback.
/// </summary>
/// <remarks>
/// <para>
/// BTCPay's own <c>AddressInvoices</c> table is the authority for the association, and
/// <c>IInvoiceCreditGateway.FindByPaymentHashAsync</c> checks it first. This index exists because that table
/// misses the case this plugin has to handle: core records an LNURL prompt's payment hash there only when
/// LUD-21 is enabled, so a store whose merchant disabled LUD-21 by hand leaves the LNURL half of the
/// association unrecorded in core, and a late payment to a superseded LNURL BOLT11 after a restart would
/// settle in this plugin's records with nothing to tie it to its BTCPay invoice. The event that feeds this
/// index fires on every prompt mint regardless of LUD-21.
/// </para>
/// <para>
/// Write-once per hash, read by payment hash only — a payment hash is unique to the BOLT11 minted for one
/// invoice, so there is no update path and no store-scoped key, exactly as in core's insert-only
/// <c>AddressInvoices</c>.
/// </para>
/// </remarks>
public interface IInvoicePaymentHashIndex
{
    /// <summary>
    /// Records that a BOLT11 with this payment hash was minted for a BTCPay invoice. Idempotent: the first
    /// write for a hash wins, exactly like the core table this mirrors.
    /// </summary>
    /// <param name="entry">
    /// With <see cref="InvoicePaymentHash.PaymentHash"/> lower-case hex, which the implementation enforces by
    /// normalising it before storing.
    /// </param>
    Task RecordAsync(InvoicePaymentHash entry, CancellationToken cancellationToken = default);

    /// <summary>Finds the invoice a payment hash was minted for, or null when it was never observed.</summary>
    /// <param name="paymentHash">Lower-case hex, as <see cref="InvoicePaymentHash.PaymentHash"/> is stored.</param>
    Task<InvoicePaymentHash?> FindByPaymentHashAsync(
        string paymentHash,
        CancellationToken cancellationToken = default);
}
