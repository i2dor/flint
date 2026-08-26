using System;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// The plugin's own record of which BTCPay invoice a Lightning payment hash was minted for.
/// </summary>
/// <remarks>
/// <para>
/// This is the same association BTCPay keeps in its <c>AddressInvoices</c> table — insert-only, keyed on the
/// payment hash of the prompt as it was minted, so a superseded BOLT11 keeps pointing at the invoice it was
/// issued for. This table is a <em>second copy</em> of the half of that association this plugin credits
/// against, kept for one reason: BTCPay writes an <c>AddressInvoices</c> row for an LNURL prompt only when
/// LUD-21 is enabled, so a merchant who disables LUD-21 by hand removes the LNURL half of the association
/// from core's tables. The plugin still observes every prompt mint through core's
/// <c>InvoiceNewPaymentDetailsEvent</c> — which fires whether or not LUD-21 is on — and records it here, so
/// a late payment to a superseded LNURL BOLT11 remains attributable after a restart even when the merchant
/// turned LUD-21 off. See <see cref="Services.SparkInvoicePaymentHashIndexer"/> for the writer and
/// <c>BTCPayInvoiceCreditGateway.FindByPaymentHashAsync</c> for the reader.
/// </para>
/// <para>
/// The row is written once per prompt and never updated: a payment hash is unique to the BOLT11 minted for
/// one invoice, so the same hash can never map to a different invoice. Re-quoting an LNURL invoice replaces
/// the prompt but mints a <em>new</em> hash, which is a new row.
/// </para>
/// <para>
/// All hex values are stored lower-cased and must be normalised on the way in; BTCPay is not consistent
/// about case and the primary-key comparison is case-sensitive.
/// </para>
/// </remarks>
public class InvoicePaymentHash
{
    /// <summary>The payment hash of the BOLT11 a prompt offered, lower-case hex. Primary key.</summary>
    public string PaymentHash { get; set; } = null!;

    /// <summary>The BTCPay invoice the BOLT11 was minted for.</summary>
    public string InvoiceId { get; set; } = null!;

    /// <summary>
    /// The payment method the prompt was minted under, as its string form (<c>BTC-LN</c> or
    /// <c>BTC-LNURL</c>) — the form BTCPay's credit gateway parses back on the way in.
    /// </summary>
    public string PaymentMethodId { get; set; } = null!;

    /// <summary>When this association was first observed.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{PaymentMethodId}: {PaymentHash} was minted for BTCPay invoice {InvoiceId}";
}
