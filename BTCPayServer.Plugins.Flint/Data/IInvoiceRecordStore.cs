using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// Position in a reconciliation walk. Ordered by creation time, then payment hash to break ties.
/// </summary>
/// <remarks>
/// The payment hash is part of the cursor rather than decoration: two invoices created in the same tick would
/// otherwise sit either side of a page boundary forever, one of them never examined.
/// </remarks>
public sealed record InvoiceReconciliationCursor(DateTimeOffset CreatedAt, string PaymentHash);

/// <summary>
/// Durable storage for <see cref="InvoiceRecord"/>s.
/// </summary>
/// <remarks>
/// An interface rather than a direct <c>DbContext</c> dependency so that the money-handling logic in
/// <c>SparkLightningClient</c> and <c>SparkService</c> can be unit-tested without a Postgres server.
/// The production implementation is <see cref="EfInvoiceRecordStore"/>.
/// </remarks>
public interface IInvoiceRecordStore
{
    /// <summary>
    /// Inserts a freshly created invoice. Throws if a row with the same payment hash already exists —
    /// which would mean the SSP reused a payment hash, and must never be papered over.
    /// </summary>
    Task AddAsync(InvoiceRecord record, CancellationToken cancellationToken = default);

    /// <summary>Loads one invoice, scoped to a store so one store cannot read another's invoices.</summary>
    Task<InvoiceRecord?> GetAsync(string storeId, string paymentHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Newest-first page of a store's invoices.
    /// </summary>
    /// <param name="pendingOnly">
    /// When true, only invoices that are still payable (unpaid and not past expiry).
    /// </param>
    Task<IReadOnlyList<InvoiceRecord>> ListAsync(
        string storeId,
        bool pendingOnly,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of a store's still-settleable invoices, oldest first, for the reconciliation pass.
    /// </summary>
    /// <param name="settleableFrom">
    /// Lower bound on <see cref="InvoiceRecord.ExpiresAt"/>. Passing a time slightly in the past deliberately
    /// includes recently expired invoices: the service provider accepts a late payment and Spark cannot stop it,
    /// so an invoice that expired a minute ago can still receive real money that has to be recorded.
    /// </param>
    /// <param name="after">
    /// Keyset cursor — the last record of the previous page, or null to start. Not an offset: settling a record
    /// removes it from this query's result set, which would shift an offset and silently skip its neighbours.
    /// </param>
    /// <remarks>
    /// Ordered oldest-first, unlike <see cref="ListAsync"/>. The oldest pending invoices are the ones closest to
    /// falling out of the window entirely, so they are the ones that must not starve behind newer arrivals.
    /// </remarks>
    Task<IReadOnlyList<InvoiceRecord>> ListForReconciliationAsync(
        string storeId,
        DateTimeOffset settleableFrom,
        InvoiceReconciliationCursor? after,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the SDK's payment id against a still-unpaid invoice, without settling it.
    /// </summary>
    /// <remarks>
    /// Called when a <c>PaymentPending</c> event names an inbound payment for one of our invoices. Two
    /// things come of it: the next reconciliation becomes a point lookup instead of a history scan, and a
    /// completed receive whose <c>PaymentSucceeded</c> event never arrives — which does happen — still has
    /// a durable pointer to the payment the poll can resolve.
    /// <para>Returns false when there is no such unpaid invoice, or the id is already recorded.</para>
    /// </remarks>
    Task<bool> TryRecordSdkPaymentIdAsync(
        string storeId,
        string paymentHash,
        string sdkPaymentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a settlement atomically, returning both the outcome and the resulting row.
    /// </summary>
    /// <remarks>
    /// A compare-and-set, not a read-modify-write, and that is load-bearing. Duplicate settlement events have
    /// been observed arriving 57 ms apart on two different threads, and they race with both BTCPay's
    /// <c>GetInvoice</c> lookup and the reconciliation task. Exactly one caller may be told
    /// <see cref="InvoiceSettlementOutcome.Settled"/> for a given invoice — that is the caller that notifies
    /// BTCPay — and every other caller must be told
    /// <see cref="InvoiceSettlementOutcome.AlreadySettled"/>.
    /// </remarks>
    Task<InvoiceSettlementResult> SettleAsync(
        string storeId,
        string paymentHash,
        string sdkPaymentId,
        long amountReceivedMsat,
        string? preimage,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an unpaid invoice cancelled. Returns true only when this call is what changed the status.
    /// </summary>
    /// <remarks>
    /// Also a compare-and-set, and for a sharper reason than <see cref="SettleAsync"/>: BTCPay cancels a
    /// superseded LNURL invoice at exactly the moment it may be settling, so a read-modify-write here could
    /// overwrite a committed settlement and turn a paid invoice back into an expired one.
    /// </remarks>
    Task<bool> CancelAsync(string storeId, string paymentHash, CancellationToken cancellationToken = default);
}
