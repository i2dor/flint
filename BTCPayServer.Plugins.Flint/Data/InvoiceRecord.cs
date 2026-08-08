using System;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// Durable record of a Lightning invoice created through this plugin.
/// </summary>
/// <remarks>
/// <para>
/// This table is not an optimisation, it is the only place an unpaid invoice exists. The Spark SDK
/// persists nothing at <c>ReceivePayment</c> time: after minting an invoice the SDK's <c>ListPayments</c>
/// returns nothing and <c>GetPayment</c> throws "Query returned no rows". Only settled payments appear in the
/// SDK's own storage. It is also what <c>SparkReconciliationTask</c> walks to recover dropped settlement
/// events, and the fix for the prior-art plugin's worst defect, which held invoice state in memory only so a
/// restart silently lost settlements.
/// </para>
/// <para>
/// The primary key is <see cref="PaymentHash"/> because that is the id BTCPay joins
/// <c>CreateInvoice</c>, <c>GetInvoice</c> and <c>WaitInvoice</c> on. It is deliberately <em>not</em>
/// the SDK's payment id: <c>GetPayment</c> keys on an opaque SSP transfer id and is not
/// hash-addressable (spike notes §6), so <see cref="SdkPaymentId"/> is a separate, nullable column
/// that we learn on settlement.
/// </para>
/// <para>
/// All hex values are stored lower-cased and must be normalised on the way in; BTCPay and the SDK
/// are not consistent about case and the primary-key comparison is case-sensitive.
/// </para>
/// </remarks>
public class InvoiceRecord
{
    /// <summary>Payment hash, lower-case hex. Primary key, and the id BTCPay sees.</summary>
    public string PaymentHash { get; set; } = null!;

    /// <summary>Store that owns this invoice. Indexed; scopes every query.</summary>
    public string StoreId { get; set; } = null!;

    /// <summary>The BOLT11 invoice as handed to the payer.</summary>
    public string Bolt11 { get; set; } = null!;

    /// <summary>
    /// Amount requested, in millisatoshi. Null for amountless invoices (LNURL / Lightning address).
    /// </summary>
    public long? AmountMsat { get; set; }

    /// <summary>
    /// Amount actually received, in millisatoshi; null until settlement. Recorded separately from
    /// <see cref="AmountMsat"/> on purpose — the received amount is what may be credited, and the
    /// prior-art plugin credited the invoiced amount instead.
    /// </summary>
    public long? AmountReceivedMsat { get; set; }

    /// <summary>
    /// Invoice description as placed in the BOLT11 <c>d</c> tag. Persisted because the SDK keeps no
    /// record of an unpaid invoice, so this is the only place it survives.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The SDK's own payment id, learned when the payment settles. Null while unpaid.
    /// </summary>
    /// <remarks>
    /// This is the only key <c>BreezSdk.GetPayment</c> accepts. Without it, resolving a settlement
    /// requires a bounded <c>ListPayments</c> scan matched on the payment hash; with it a single
    /// point lookup suffices. See <c>SparkLightningClient.GetInvoice</c>.
    /// </remarks>
    public string? SdkPaymentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Settlement timestamp; null while unpaid.</summary>
    public DateTimeOffset? SettledAt { get; set; }

    /// <summary>
    /// Persisted status. Natural expiry is deliberately <em>not</em> written here — see
    /// <see cref="EffectiveStatus"/> and <see cref="TryCancel"/>.
    /// </summary>
    public InvoiceRecordStatus Status { get; set; } = InvoiceRecordStatus.Unpaid;

    /// <summary>
    /// Payment preimage, lower-case hex; available only after settlement, and only when the SSP
    /// reported it (<c>SparkHtlcDetails.preimage</c> is nullable).
    /// </summary>
    public string? Preimage { get; set; }

    /// <summary>
    /// Status as reported to BTCPay, which treats a past-expiry unpaid invoice as expired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Natural expiry is computed rather than persisted, and that distinction is load-bearing.
    /// <see cref="InvoiceRecordStatus.Expired"/> in the database means "cancelled locally, refuse to
    /// settle" (see <see cref="TryCancel"/>). If natural expiry were persisted the same way, a
    /// payment arriving a second after expiry — which the SSP will happily accept, because the Spark
    /// SDK has no way to stop it — would be silently dropped instead of recorded.
    /// </para>
    /// <para>
    /// <b>How far that capability actually reaches.</b> A late payment settles through any path that gets
    /// to <see cref="IInvoiceRecordStore.SettleAsync"/>, whose only refusal is a cancelled invoice. The
    /// recurring reconciliation pass therefore keeps looking for an hour past expiry rather than stopping
    /// at it. Beyond that hour an invoice is no longer re-checked and a late payment stays unattributed
    /// in the wallet balance — a deliberate bound, since the alternative is rescanning every invoice ever
    /// created on every pass. Note also that BTCPay stops listening for an invoice once it expires
    /// (<c>LightningInstanceListener.RemoveExpiredInvoices</c>), so a settlement recorded after expiry
    /// keeps this plugin's own ledger truthful but will usually not revive the BTCPay invoice.
    /// </para>
    /// </remarks>
    public InvoiceRecordStatus EffectiveStatus(DateTimeOffset now) =>
        Status is InvoiceRecordStatus.Unpaid && now > ExpiresAt
            ? InvoiceRecordStatus.Expired
            : Status;

    /// <summary>
    /// Applies a settlement to this record, refusing to do so if the invoice was cancelled.
    /// </summary>
    /// <remarks>
    /// The refusal is the substance of the plugin's <c>CancelInvoice</c>: the Spark SDK has no
    /// invoice-cancellation primitive, so a cancelled invoice can still be paid on the SSP's side.
    /// Those funds do land in the store's Spark balance (and will be swept), but the BTCPay invoice
    /// must not be marked paid — BTCPay was told the invoice was cancelled and may have issued a
    /// replacement. Callers log a warning when this happens.
    /// </remarks>
    public InvoiceSettlementOutcome TrySettle(
        string sdkPaymentId,
        long amountReceivedMsat,
        string? preimage,
        DateTimeOffset settledAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(sdkPaymentId);
        ArgumentOutOfRangeException.ThrowIfNegative(amountReceivedMsat);

        if (Status is InvoiceRecordStatus.Expired)
            return InvoiceSettlementOutcome.RefusedCancelled;

        if (Status is InvoiceRecordStatus.Paid)
        {
            // Idempotent replay (event plus poll, or a restart mid-settlement). Backfill anything we
            // did not know the first time, but never move the amount or the settlement timestamp:
            // the first observation is the one BTCPay already credited.
            SdkPaymentId ??= sdkPaymentId;
            Preimage ??= preimage;
            return InvoiceSettlementOutcome.AlreadySettled;
        }

        Status = InvoiceRecordStatus.Paid;
        SdkPaymentId = sdkPaymentId;
        AmountReceivedMsat = amountReceivedMsat;
        Preimage = preimage ?? Preimage;
        SettledAt = settledAt;
        return InvoiceSettlementOutcome.Settled;
    }

    /// <summary>
    /// Marks an unpaid invoice cancelled. Returns true only when this call is what changed the status.
    /// </summary>
    /// <remarks>
    /// "Did anything change", not "is it cancelled now": an already-cancelled invoice returns false, and so
    /// does a paid one. That is the same predicate the store's conditional UPDATE evaluates
    /// (<c>WHERE Status = Unpaid</c>), which is what keeps the two implementations of
    /// <see cref="IInvoiceRecordStore"/> observably identical.
    /// </remarks>
    public bool TryCancel()
    {
        if (Status is not InvoiceRecordStatus.Unpaid)
            return false;
        Status = InvoiceRecordStatus.Expired;
        return true;
    }
}

public enum InvoiceRecordStatus
{
    Unpaid,
    Paid,

    /// <summary>
    /// Cancelled locally. Also what <see cref="InvoiceRecord.EffectiveStatus"/> reports for an unpaid
    /// invoice past its expiry.
    /// </summary>
    Expired
}

/// <summary>Result of applying a settlement to an <see cref="InvoiceRecord"/>.</summary>
public enum InvoiceSettlementOutcome
{
    /// <summary>The record moved from unpaid to paid. Notify BTCPay.</summary>
    Settled,

    /// <summary>The record was already paid. Do not notify BTCPay again.</summary>
    AlreadySettled,

    /// <summary>The invoice was cancelled; the settlement must not be reported. Log a warning.</summary>
    RefusedCancelled,

    /// <summary>No record exists for this payment hash — a receive this plugin did not create.</summary>
    NotFound
}

/// <summary>Outcome of a settlement attempt plus the resulting row, when there was one.</summary>
public sealed record InvoiceSettlementResult(InvoiceSettlementOutcome Outcome, InvoiceRecord? Record);
