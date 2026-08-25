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

    /// <summary>
    /// Records that a settled invoice's payment has reached the BTCPay invoice it was minted for. Returns true
    /// only when this call is what set the timestamp.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A compare-and-set on <c>Status = Paid AND CreditedAt IS NULL</c>, for two independent reasons. The
    /// <c>Paid</c> guard is a safety interlock: marking an unpaid row credited would tell every later pass
    /// that the credit is done, and the payment that eventually arrives would never be routed to the merchant's
    /// invoice. The null guard keeps the timestamp meaning "when the credit landed" rather than "when a pass
    /// last looked", which matters because two callers genuinely race here — the settlement path and the
    /// reconciliation pass both attempt the credit for the same row.
    /// </para>
    /// <para>
    /// Idempotent by construction: a second call returns false and changes nothing, which is what lets the
    /// caller treat "we credited it" and "BTCPay already had it" as the same outcome.
    /// </para>
    /// </remarks>
    Task<bool> MarkCreditedAsync(
        string storeId,
        string paymentHash,
        DateTimeOffset creditedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a settled invoice will never reach a BTCPay invoice. Returns true only when this call is
    /// what set the timestamp.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The terminal marker for the other half of <see cref="ListUncreditedAsync"/>'s exit condition. A
    /// settlement whose payment hash BTCPay has no invoice for can never be credited, so it has to leave the
    /// retry set — but marking it <em>credited</em> to achieve that would claim the merchant was paid on an
    /// invoice that in fact records nothing. This stamp says what actually happened, in a column an operator can
    /// query: see <see cref="InvoiceRecord.CreditAbandonedAt"/>.
    /// </para>
    /// <para>
    /// A compare-and-set guarded on <c>Status = Paid AND CreditedAt IS NULL AND CreditAbandonedAt IS NULL</c>,
    /// and every clause is load-bearing. <c>Paid</c> because an unsettled row has nothing to give up on;
    /// <c>CreditedAt IS NULL</c> so a credit that landed on a concurrent pass is never overwritten with a claim
    /// that it did not; and the self-guard so the operator warning the caller emits alongside this fires exactly
    /// once rather than on every pass.
    /// </para>
    /// </remarks>
    Task<bool> MarkCreditAbandonedAsync(
        string storeId,
        string paymentHash,
        DateTimeOffset abandonedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of a store's settled-but-unresolved invoices, oldest first: settlements this plugin recorded
    /// whose money has neither been put on a BTCPay invoice nor been given up on.
    /// </summary>
    /// <param name="settledFrom">
    /// Lower bound on <see cref="InvoiceRecord.SettledAt"/>, which bounds the size of this set on a server that
    /// has been running for years. It is deliberately <em>not</em> the credit retry horizon: a record has to
    /// stay listed for a while <em>after</em> that horizon passes, or it would age out of this listing before
    /// any pass could classify it as abandoned — reported to nobody, retried by nobody, and indistinguishable in
    /// the database from one still in flight. See <c>SparkInvoiceCreditor.ListableFrom</c>, which is what the
    /// reconciliation pass passes here and which adds that slack explicitly.
    /// </param>
    /// <param name="after">
    /// Keyset cursor, exactly as in <see cref="ListForReconciliationAsync"/> and for the same reason: crediting
    /// a record removes it from this result set, so an offset would skip its neighbours.
    /// </param>
    /// <remarks>
    /// Normally empty — the credit is attempted the moment a settlement is recorded, and only fails when
    /// BTCPay's own row is not there yet, when the process died in between, or when the settlement predates
    /// this column. That is why it is ordered and paged like the reconciliation walk rather than indexed
    /// specially: the set is small, and the (StoreId, Status) index already narrows it to a store's paid rows.
    /// </remarks>
    Task<IReadOnlyList<InvoiceRecord>> ListUncreditedAsync(
        string storeId,
        DateTimeOffset settledFrom,
        InvoiceReconciliationCursor? after,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The stores that have at least one settlement awaiting a BTCPay credit, whether or not their Spark wallet
    /// is currently running.
    /// </summary>
    /// <param name="settledFrom">As in <see cref="ListUncreditedAsync"/>, and the same value is passed.</param>
    /// <param name="limit">
    /// Cap on the number of stores returned, so this cannot become an unbounded query. Reaching it is
    /// pathological — it would mean hundreds of stores each holding uncredited money — and the caller says so
    /// in the log rather than pretending it walked them all.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Why the credit walk cannot take its stores from the live SDK instances.</b> Crediting a settlement
    /// touches only BTCPay's own tables and this plugin's own rows; it makes no SDK call at all. But the
    /// reconciliation pass used to derive its store list from the running wallets, so a store whose Spark
    /// connection was broken — a rotated key, a service-provider outage, a wallet reconfigured away — got no
    /// credit pass either, and its already-received money sat uncredited until it aged out of the retry set. The
    /// two concerns are now sourced separately: settling needs a wallet, crediting needs only a store id, and
    /// this is where that store id comes from.
    /// </para>
    /// <para>
    /// Ordered by store id so the cap is deterministic on a given server rather than picking an arbitrary
    /// subset each pass. The exact ordering is the database's collation and is not part of this contract.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<string>> ListStoreIdsAwaitingCreditAsync(
        DateTimeOffset settledFrom,
        int limit,
        CancellationToken cancellationToken = default);
}
