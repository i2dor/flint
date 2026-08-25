using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The BTCPay invoice a BOLT11 was minted for, as found from the BOLT11's payment hash.
/// </summary>
/// <param name="InvoiceId">BTCPay's own invoice id.</param>
/// <param name="StoreId">
/// The store BTCPay says owns that invoice. Compared against the store the settlement arrived on before
/// anything is credited — see <see cref="SparkInvoiceCreditor"/>.
/// </param>
/// <param name="PaymentMethodId">
/// The payment method the hash was indexed under, as its string form (<c>BTC-LN</c> or <c>BTC-LNURL</c>). A
/// string rather than BTCPay's <c>PaymentMethodId</c> so the credit decision — and its tests — need no BTCPay
/// types; the gateway parses it back on the way in.
/// </param>
/// <param name="AlreadyHasPayment">
/// True when the invoice already carries a payment for this hash, which is the normal case: BTCPay's own
/// Lightning listener usually gets there first.
/// </param>
public sealed record SparkInvoiceCreditMatch(
    string InvoiceId,
    string StoreId,
    string PaymentMethodId,
    bool AlreadyHasPayment);

/// <summary>One settlement, in the shape BTCPay needs to record a payment for it.</summary>
/// <param name="Preimage">
/// Lower-case hex, or null when the service provider never reported one. Validated against the payment hash
/// before it is stored; an unverifiable preimage is dropped rather than recorded.
/// </param>
public sealed record SparkInvoiceCreditRequest(
    string InvoiceId,
    string PaymentMethodId,
    string PaymentHash,
    string Bolt11,
    long AmountReceivedMsat,
    string? Preimage,
    DateTimeOffset PaidAt);

/// <summary>What happened when a settlement was offered to a BTCPay invoice.</summary>
public enum SparkInvoiceCreditOutcome
{
    /// <summary>
    /// This call put the payment on the invoice. The one outcome that publishes an invoice event.
    /// </summary>
    CreditedNow,

    /// <summary>
    /// BTCPay already had a payment with this id on this invoice — its own listener won, or a previous
    /// attempt did. Nothing was written and nothing was published, and the merchant's invoice is credited
    /// exactly once either way.
    /// </summary>
    AlreadyRecorded,

    /// <summary>
    /// There is no BTCPay invoice for this payment hash. Either its row has not been written yet (BTCPay
    /// indexes the hash just after <c>CreateInvoice</c> returns) or this BOLT11 was never minted for a BTCPay
    /// invoice at all. Retryable, but not forever.
    /// </summary>
    InvoiceGone,

    /// <summary>
    /// The invoice exists but carries no payment prompt for that payment method, so BTCPay cannot hold a
    /// payment against it. Not retryable — a prompt never disappears from an invoice's blob, so a second
    /// attempt would fail identically.
    /// </summary>
    PromptMissing
}

/// <summary>
/// The seam through which this plugin puts a settlement onto the BTCPay invoice its BOLT11 was minted for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the plugin needs this at all, rather than leaving it to BTCPay.</b> BTCPay's Lightning listener
/// matches an incoming notification by id against a set built from each invoice's <em>current</em> payment
/// prompt. Replace BOLT11 X with BOLT11 Y — which BTCPay does whenever an LNURL invoice is re-quoted — and X
/// leaves that set; after a restart it is never re-added, because the set is rebuilt from the prompts that
/// exist now. X remains payable on the Spark service provider, which has no cancellation primitive, so a
/// payment to X lands in the merchant's wallet with nothing left watching for it. Notifying a listener that
/// is not listening cannot fix that; writing the payment onto the invoice can.
/// </para>
/// <para>
/// <b>What makes that safe.</b> BTCPay retains the mint-time association permanently: the payment hash of
/// every prompt it issues is written into its <c>AddressInvoices</c> table, insert-only, and never removed
/// even when the prompt is superseded. That table is the authority this seam reads — not a guess, and not
/// state this plugin keeps. And the payment id used to credit is the payment hash, which is precisely the id
/// BTCPay's own listener would use, so the two collide on the <c>Payments</c> primary key: whichever gets
/// there first records the money, the other is told it already exists, and the invoice is credited exactly
/// once.
/// </para>
/// <para>
/// An interface so the credit decision in <see cref="SparkInvoiceCreditor"/> — including its cross-store
/// refusal — is unit-testable without a BTCPay host and a Postgres database. The production implementation is
/// <see cref="BTCPayInvoiceCreditGateway"/>.
/// </para>
/// </remarks>
public interface IInvoiceCreditGateway
{
    /// <summary>
    /// Finds the BTCPay invoice a payment hash was minted for, or null when BTCPay has no record of it.
    /// </summary>
    /// <param name="paymentHash">Lower-case hex, as BTCPay indexed it.</param>
    Task<SparkInvoiceCreditMatch?> FindByPaymentHashAsync(
        string paymentHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a settled payment against a BTCPay invoice, and announces it only if this call is what
    /// recorded it.
    /// </summary>
    Task<SparkInvoiceCreditOutcome> AddSettledPaymentAsync(
        SparkInvoiceCreditRequest request,
        CancellationToken cancellationToken = default);
}
