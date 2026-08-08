using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// What this wallet knows about its attempts to pay one BOLT11 invoice.
/// </summary>
/// <remarks>
/// <para>
/// Exists because BTCPay's <c>ILightningClient.Pay</c> boundary carries no payout id, so the plugin cannot
/// otherwise tell "BTCPay is retrying the payout it already asked me about" from "BTCPay is asking me to
/// discharge a second, different obligation that happens to name the same invoice".
/// </para>
/// <para>
/// That second case is reachable in BTCPay v2.4.1. Its duplicate-destination guard
/// (<c>PullPaymentHostedService.HandleCreatePayout</c>) only blocks a claim while another payout for the same
/// payment hash is in <c>AwaitingApproval</c>, <c>AwaitingPayment</c> or <c>InProgress</c> — it explicitly
/// allows a fresh claim once the earlier payout is <c>Completed</c> or <c>Cancelled</c>, is not scoped by
/// store, and is backed by a non-unique index. And a crash between <c>Pay</c> returning and BTCPay persisting
/// the proof leaves the payout <c>InProgress</c> with no proof, which
/// <c>LightningPendingPayoutListener</c> turns into <c>Cancelled</c> without ever asking the node — freeing
/// the invoice for exactly such a re-claim.
/// </para>
/// <para>
/// A BOLT11 invoice can only be paid once, so two obligations naming one invoice cannot both be discharged.
/// Reporting success twice would mark both payouts <c>Completed</c> with only one payment made. This record is
/// what lets the second claim be refused instead.
/// </para>
/// </remarks>
public class OutgoingPaymentRecord
{
    /// <summary>Payment hash of the invoice being paid, lower-case hex. Part of the composite primary key.</summary>
    public string PaymentHash { get; set; } = null!;

    /// <summary>
    /// Store that asked for the payment. Part of the composite primary key, because two stores on one server can
    /// each legitimately be asked to pay the same BOLT11 invoice.
    /// </summary>
    public string StoreId { get; set; } = null!;

    /// <summary>
    /// The UUID handed to the SDK, which the SDK adopts as its own <c>Payment.id</c>.
    /// </summary>
    public string IdempotencyKey { get; set; } = null!;

    /// <summary>The invoice, for the audit trail and for the sweep/payout history UI.</summary>
    public string Bolt11 { get; set; } = null!;

    public DateTimeOffset FirstAttemptAt { get; set; }

    /// <summary>How many times BTCPay has asked this wallet to pay this invoice.</summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// When this wallet first told BTCPay the payment had been made, or might have been.
    /// </summary>
    /// <remarks>
    /// Set for an outcome of "sent" or "may have been sent", never for a definite refusal — a refusal leaves
    /// this null so the next attempt proceeds normally. Once set, a later claim naming the same invoice is
    /// refused.
    /// </remarks>
    public DateTimeOffset? ReportedAt { get; set; }
}

/// <summary>Durable record of this wallet's outgoing-payment claims.</summary>
public interface IOutgoingPaymentStore
{
    /// <summary>
    /// Records that BTCPay has asked for this invoice to be paid, and returns what was already known.
    /// </summary>
    Task<OutgoingPaymentRecord> RegisterAttemptAsync(
        string storeId,
        string paymentHash,
        string idempotencyKey,
        string bolt11,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the right to report this payment as sent. Returns true only for the first caller.
    /// </summary>
    Task<bool> TryMarkReportedAsync(
        string storeId,
        string paymentHash,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
