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
    /// When this settlement was recorded on the BTCPay invoice the BOLT11 was minted for. Null means it has
    /// not been, and the credit is still owed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a settled invoice is not necessarily a credited one.</b> Settling this row records that the money
    /// arrived; it does not put a payment on the merchant's BTCPay invoice. Normally BTCPay's own Lightning
    /// listener does that, having been woken by <c>SparkSettlementBroadcaster</c>. But that listener watches
    /// only the <em>current</em> prompt of each invoice: when BTCPay supersedes BOLT11 X with BOLT11 Y — which
    /// it does on every LNURL re-quote — and the process then restarts, only Y is watched again. X is still
    /// payable on the service provider, and a payment to X after that restart would settle this row while the
    /// BTCPay invoice stayed unpaid. The same hole opens when a listening session is saturated, or when the
    /// process dies between the settle and the notification.
    /// </para>
    /// <para>
    /// So the credit is tracked here as its own durable step, retried by the reconciliation pass until it
    /// lands. "Landed" covers two outcomes and deliberately does not distinguish them: our own insert won, or
    /// we confirmed BTCPay already holds a payment row for this hash. Both mean the merchant's invoice has the
    /// money on it exactly once, which is the only thing a retry needs to know.
    /// </para>
    /// <para>
    /// Never cleared, and only ever set on a <see cref="InvoiceRecordStatus.Paid"/> row — see
    /// <see cref="IInvoiceRecordStore.MarkCreditedAsync"/>, which is a compare-and-set for the same reason
    /// settling is.
    /// </para>
    /// </remarks>
    public DateTimeOffset? CreditedAt { get; set; }

    /// <summary>
    /// When this settlement was given up on as never creditable. Null means it has not been.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not just <see cref="CreditedAt"/>.</b> Some settlements can never reach a BTCPay invoice:
    /// a BOLT11 minted through this plugin's own Greenfield endpoints was never attached to one, and no amount
    /// of retrying will attach it. Such a row has to leave the retry set, or every pass re-attempts it and
    /// re-warns about it for the life of the server. But stamping <see cref="CreditedAt"/> to achieve that would
    /// record that the merchant <em>was</em> credited, which is exactly the opposite of what happened — and this
    /// table is the record an operator reconciles a wallet balance against. So the two terminal states are
    /// separate columns, and <c>Status = Paid AND "CreditedAt" IS NULL AND "CreditAbandonedAt" IS NOT NULL</c>
    /// is a precise SQL question: money in the wallet that no BTCPay invoice accounts for.
    /// </para>
    /// <para>
    /// Stamping this is also what makes the operator warning fire <em>once</em>. The warning is emitted by
    /// <see cref="Services.SparkInvoiceCreditor"/> at the moment the row is stamped, and the stamp removes the
    /// row from <see cref="IInvoiceRecordStore.ListUncreditedAsync"/> permanently — so the amount and the
    /// payment hash are reported to the operator exactly once rather than on every pass forever.
    /// </para>
    /// <para>
    /// A credited row is never labelled abandoned — the stamp is guarded on <see cref="CreditedAt"/> still being
    /// null. The reverse ordering has no path to it: an abandoned row leaves the retry set, and the creditor
    /// refuses an in-hand record that carries this stamp before it attempts anything. Were it ever to happen
    /// anyway, <see cref="CreditedAt"/> is the authoritative half of the pair — the money did reach the invoice.
    /// </para>
    /// </remarks>
    public DateTimeOffset? CreditAbandonedAt { get; set; }

    /// <summary>
    /// Status as reported to BTCPay, which treats a past-expiry unpaid invoice as expired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Natural expiry is computed rather than persisted, and that distinction is load-bearing.
    /// <see cref="InvoiceRecordStatus.Expired"/> in the database means "cancelled locally" (see
    /// <see cref="TryCancel"/>), not "no longer payable". If natural expiry were persisted the same way, a
    /// payment arriving a second after expiry — which the SSP will happily accept, because the Spark SDK
    /// has no way to stop it — would be silently dropped instead of recorded.
    /// </para>
    /// <para>
    /// A <em>cancelled</em> invoice is reported <b>unpaid</b>, not expired. Cancellation cannot withdraw
    /// the invoice from the service provider, so it remains payable, and BTCPay's Lightning listener drops
    /// an invoice whose status reads expired (<c>LightningInstanceListener.AddPaymentCore</c>) — the one
    /// listener that could still deliver a late payment's credit. Reporting it unpaid keeps that listener
    /// attached until a payment settles it or BTCPay's own invoice expiry lets it go.
    /// </para>
    /// <para>
    /// <b>How far that capability actually reaches.</b> A late payment settles through any path that gets
    /// to <see cref="IInvoiceRecordStore.SettleAsync"/>. The recurring reconciliation pass keeps looking
    /// for an hour past expiry rather than stopping at it; beyond that hour an invoice is no longer
    /// re-checked and a late payment stays unattributed in the wallet balance — a deliberate bound, since
    /// the alternative is rescanning every invoice ever created on every pass.
    /// </para>
    /// <para>
    /// A settlement recorded after BTCPay has stopped listening for the invoice (its own expiry —
    /// <c>LightningInstanceListener.RemoveExpiredInvoices</c>, or a restart that only re-watched the
    /// superseding BOLT11) no longer depends on that listener at all: the credit is routed to the invoice the
    /// BOLT11 was minted for directly, tracked by <see cref="CreditedAt"/> and retried until it lands.
    /// </para>
    /// </remarks>
    public InvoiceRecordStatus EffectiveStatus(DateTimeOffset now) =>
        Status switch
        {
            // Cancelled locally but still payable on Spark: report unpaid, so BTCPay's listener stays
            // attached and a late payment can still be credited (see the remarks above).
            InvoiceRecordStatus.Expired => InvoiceRecordStatus.Unpaid,
            InvoiceRecordStatus.Unpaid when now > ExpiresAt => InvoiceRecordStatus.Expired,
            _ => Status
        };

    /// <summary>
    /// Applies a settlement to this record, crediting a late payment of a cancelled invoice too.
    /// </summary>
    /// <remarks>
    /// The Spark SDK has no invoice-cancellation primitive, so a cancelled invoice can still be paid on
    /// the SSP's side. That payment is real money into the store's wallet, and refusing to credit it would
    /// leave it unattributed: the funds in the Spark balance, swept later, with no BTCPay invoice ever
    /// marked paid and no record of what the money was for. Cancellation therefore marks the invoice
    /// locally (<see cref="TryCancel"/>) but never bars the settlement — the invoice is credited exactly
    /// as if it had not been cancelled, and the payment's hash identifies which invoice that is.
    /// </remarks>
    public InvoiceSettlementOutcome TrySettle(
        string sdkPaymentId,
        long amountReceivedMsat,
        string? preimage,
        DateTimeOffset settledAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(sdkPaymentId);
        ArgumentOutOfRangeException.ThrowIfNegative(amountReceivedMsat);

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
    /// Records that this settlement has reached its BTCPay invoice. Returns true only when this call is what
    /// set the timestamp.
    /// </summary>
    /// <remarks>
    /// Guarded on <see cref="InvoiceRecordStatus.Paid"/> as well as on the column still being empty, and both
    /// guards matter. A row that is not paid has no settlement to credit, so marking one would permanently
    /// suppress the credit of the payment that later arrives; and re-stamping an already-credited row would
    /// move a timestamp that records when the merchant's invoice was actually paid. Same predicate as the
    /// store's conditional UPDATE (<c>WHERE Status = Paid AND CreditedAt IS NULL</c>), which is what keeps the
    /// two implementations of <see cref="IInvoiceRecordStore"/> observably identical.
    /// </remarks>
    public bool TryMarkCredited(DateTimeOffset creditedAt)
    {
        if (Status is not InvoiceRecordStatus.Paid || CreditedAt is not null)
            return false;
        CreditedAt = creditedAt;
        return true;
    }

    /// <summary>
    /// Records that this settlement will never reach a BTCPay invoice. Returns true only when this call is what
    /// set the timestamp.
    /// </summary>
    /// <remarks>
    /// Guarded on <see cref="InvoiceRecordStatus.Paid"/> and on <em>both</em> credit columns still being empty.
    /// The <see cref="CreditedAt"/> guard is the one that matters: a row the previous pass credited must not
    /// then be labelled abandoned, which would make the wallet look short by an amount that was in fact
    /// accounted for. The self-guard is what makes the operator warning fire exactly once — the caller emits it
    /// only when this returns true. Same predicate as the store's conditional UPDATE
    /// (<c>WHERE Status = Paid AND CreditedAt IS NULL AND CreditAbandonedAt IS NULL</c>), which is what keeps
    /// the two implementations of <see cref="IInvoiceRecordStore"/> observably identical.
    /// </remarks>
    public bool TryMarkCreditAbandoned(DateTimeOffset abandonedAt)
    {
        if (Status is not InvoiceRecordStatus.Paid || CreditedAt is not null || CreditAbandonedAt is not null)
            return false;
        CreditAbandonedAt = abandonedAt;
        return true;
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
    /// Cancelled locally: no longer offered to payers, but still payable on the service provider, so a
    /// late payment still settles and credits the invoice (see <see cref="InvoiceRecord.EffectiveStatus"/>).
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

    /// <summary>No record exists for this payment hash — a receive this plugin did not create.</summary>
    NotFound
}

/// <summary>Outcome of a settlement attempt plus the resulting row, when there was one.</summary>
public sealed record InvoiceSettlementResult(InvoiceSettlementOutcome Outcome, InvoiceRecord? Record);
