using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>One store to reconcile, and the live SDK handle to reconcile it against.</summary>
public sealed record SparkReconciliationTarget(string StoreId, ISparkSdkClient Sdk);

/// <summary>
/// The one place a Spark receive is turned into a settled BTCPay invoice.
/// </summary>
/// <remarks>
/// <para>
/// Three callers converge here, and they must agree: the SDK event consumer, BTCPay's
/// <c>GetInvoice</c> lookup, and the plugin's own reconciliation task. Sharing the code is what makes
/// "exactly one caller notifies BTCPay" true rather than aspirational — the guarantee lives in
/// <see cref="IInvoiceRecordStore.SettleAsync"/>'s compare-and-set, and every path has to go through it.
/// </para>
/// <para>
/// <b>Why a reconciliation task exists at all.</b> Settlement cannot rely on the SDK's events: a completed
/// receive has been observed emitting only <c>PaymentPending</c> and never <c>PaymentSucceeded</c>, with the
/// completion visible solely from a later storage read. Nor can it rely on BTCPay re-polling. BTCPay calls
/// <c>GetInvoice</c> once per invoice when the invoice is created or activated, and once per invoice when a
/// listening session starts (<c>LightningInstanceListener.PollAllListenedInvoices</c>); its one-minute
/// <c>_ListenPoller</c> timer only calls <c>CheckConnections()</c>, which expires stale entries and restarts
/// a <em>dead</em> session — it polls no invoices. Since <c>WaitInvoice</c> here awaits a channel and never
/// faults, the session never dies and never re-polls. Without this task, a dropped completion event means an
/// invoice that expires unpaid while the sats sit in the merchant's wallet.
/// </para>
/// </remarks>
public class SparkSettlementReconciler
{
    /// <summary>
    /// How far before an invoice's creation time the payment scan starts. Absorbs clock skew between this
    /// server and the service provider without widening the scan much.
    /// </summary>
    private static readonly TimeSpan ReconciliationSlack = TimeSpan.FromMinutes(10);

    /// <summary>Payments fetched per page when scanning for a settled receive.</summary>
    private const int PaymentPageSize = 50;

    /// <summary>
    /// Most pages the scan will walk before giving up on one invoice, bounding it at 500 payments.
    /// </summary>
    /// <remarks>
    /// A single page is not enough: the scan is newest-first, so on a busy store the target receive is
    /// pushed off page one by later payments and would never be found. It still has to be bounded, because
    /// this runs per pending invoice on a timer.
    /// </remarks>
    private const int MaxPaymentPages = 10;

    /// <summary>Invoices fetched per page while walking a store's set.</summary>
    private const int InvoicePageSize = 100;

    /// <summary>Invoices examined per store per reconciliation pass, across all pages.</summary>
    private const int MaxInvoicesPerPass = 1000;

    /// <summary>
    /// How long after expiry an invoice is still worth re-checking.
    /// </summary>
    /// <remarks>
    /// The service provider accepts a payment after expiry and Spark cannot stop it, so a just-expired invoice
    /// can still take real money. Recording it keeps this plugin's own ledger truthful even when BTCPay has
    /// already stopped listening for that invoice — the alternative is sats in the wallet that nothing accounts
    /// for. Bounded because the window is otherwise unbounded work on every pass, forever.
    /// </remarks>
    private static readonly TimeSpan ExpiredReconciliationGrace = TimeSpan.FromHours(1);

    private readonly IInvoiceRecordStore _invoiceStore;
    private readonly SparkSettlementBroadcaster _broadcaster;
    private readonly ILogger<SparkSettlementReconciler> _logger;

    /// <summary>
    /// Where the last capped reconciliation pass stopped, per store, so the next pass resumes rather than
    /// re-examining the same oldest invoices — see the note at the cursor's initialisation.
    /// </summary>
    private readonly ConcurrentDictionary<string, InvoiceReconciliationCursor> _resumeCursors = new();

    public SparkSettlementReconciler(
        IInvoiceRecordStore invoiceStore,
        SparkSettlementBroadcaster broadcaster,
        ILogger<SparkSettlementReconciler> logger)
    {
        _invoiceStore = invoiceStore;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    /// <summary>
    /// Applies a completed inbound payment to its invoice, notifying listeners if this call is the one that
    /// settled it.
    /// </summary>
    public async Task<InvoiceSettlementResult> ApplyAsync(
        string storeId,
        SparkPayment payment,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentNullException.ThrowIfNull(payment);

        if (payment.PaymentHash is not { } paymentHash)
            throw new ArgumentException("A settled receive must have a payment hash.", nameof(payment));
        if (payment.Direction is not SparkPaymentDirection.Receive)
            throw new ArgumentException("Only an inbound payment can settle an invoice.", nameof(payment));

        var result = await _invoiceStore.SettleAsync(
                storeId,
                paymentHash,
                payment.SdkPaymentId,
                payment.AmountMsat,
                payment.Preimage,
                payment.Timestamp,
                cancellationToken)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case InvoiceSettlementOutcome.Settled when result.Record is not null:
                // Audit finding PaymentFlow F3. Nothing here compares what arrived to what was invoiced, and the
                // record settles once and never revises upward — so a sub-amount arrival linked to this hash
                // marks the invoice Paid for less than it asked, and the completing payment that follows is
                // swallowed as AlreadySettled. On the classic Lightning rail this cannot happen, because the
                // preimage is only released for a full payment; the Spark rail's amount semantics are undefined,
                // which is why this is a loud warning rather than a refusal. Refusing would be worse: if the SDK
                // ever reported an amount a hair low, legitimate invoices would stop settling and the merchant
                // would lose sales to a defence against an unproven attack.
                if (result.Record.AmountMsat is { } invoiced && payment.AmountMsat < invoiced)
                {
                    _logger.LogWarning(
                        "Store {StoreId}: Lightning invoice {PaymentHash} settled with {ReceivedSats} sat but "
                        + "asked for {InvoicedSats} sat. It is now marked paid and a later, larger payment for "
                        + "the same hash will be recorded as already settled. Check the Spark balance against "
                        + "this invoice before fulfilling it",
                        storeId, paymentHash, payment.AmountSats, invoiced / 1000);
                }
                else
                {
                    _logger.LogInformation(
                        "Store {StoreId}: Lightning invoice {PaymentHash} settled with {AmountSats} sat",
                        storeId, paymentHash, payment.AmountSats);
                }

                // Published only on the transition, so a duplicate event or a poll racing the event does not
                // wake every listening session twice.
                _broadcaster.Publish(ToSettlement(result.Record));
                break;

            case InvoiceSettlementOutcome.AlreadySettled:
                _logger.LogDebug(
                    "Store {StoreId}: Lightning invoice {PaymentHash} was already settled",
                    storeId, paymentHash);
                break;

            default:
                _logger.LogWarning(
                    "Store {StoreId}: received {AmountSats} sat for payment hash {PaymentHash}, which this "
                    + "plugin has no record of creating. The funds are in the Spark balance",
                    storeId, payment.AmountSats, paymentHash);
                break;
        }

        return result;
    }

    /// <summary>
    /// Finds the completed inbound payment for an invoice, or null if there is not one yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A point lookup when the SDK's payment id is known — which the pending-event path records — and a bounded,
    /// paged, newest-first scan otherwise, because <c>GetPayment</c> is not addressable by payment hash. A point
    /// lookup that comes back empty <b>falls through to the scan</b> rather than concluding anything: the
    /// recorded id came from a <c>PaymentPending</c> event, and if the SDK has since replaced or re-keyed that
    /// row, treating the miss as "not paid" would make the invoice permanently unresolvable.
    /// </para>
    /// <para>
    /// Never throws. An unreachable service provider means "not settled yet", which the next pass retries;
    /// an exception here would abort a whole reconciliation pass or fail a BTCPay lookup. Every SDK call is
    /// bounded by a deadline for the same reason.
    /// </para>
    /// </remarks>
    public async Task<SparkPayment?> FindReceiveAsync(
        ISparkSdkClient sdk,
        InvoiceRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sdk);
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            if (record.SdkPaymentId is { } sdkPaymentId)
            {
                var byId = await SparkDeadline.OrNullAsync(
                        sdk.GetPaymentAsync(sdkPaymentId, cancellationToken),
                        Constants.SdkCallDeadline,
                        () => _logger.LogWarning(
                            "Store {StoreId}: looking up Spark payment {SdkPaymentId} exceeded {Seconds}s",
                            record.StoreId, sdkPaymentId, Constants.SdkCallDeadline.TotalSeconds),
                        cancellationToken)
                    .ConfigureAwait(false);

                // Direction is re-checked because a point lookup cannot filter on it, and a self-payment
                // produces a Receive leg and a Send leg sharing one payment hash whose amounts differ by the
                // routing fee.
                if (byId is { Direction: SparkPaymentDirection.Receive })
                    return byId;

                _logger.LogDebug(
                    "Store {StoreId}: the recorded Spark payment id for invoice {PaymentHash} resolved to "
                    + "nothing usable; falling back to a history scan",
                    record.StoreId, record.PaymentHash);
            }

            for (var page = 0; page < MaxPaymentPages; page++)
            {
                var payments = await SparkDeadline.OrNullAsync(
                        sdk.ListPaymentsAsync(
                            new SparkListPaymentsQuery(
                                SparkPaymentDirection.Receive,
                                CompletedOnly: true,
                                // Anchored to the invoice's own creation time so this stays a small scan however
                                // long the wallet's history gets.
                                From: record.CreatedAt - ReconciliationSlack,
                                Offset: page * PaymentPageSize,
                                Limit: PaymentPageSize),
                            cancellationToken),
                        Constants.SdkCallDeadline,
                        () => _logger.LogWarning(
                            "Store {StoreId}: scanning Spark payments for invoice {PaymentHash} exceeded "
                            + "{Seconds}s",
                            record.StoreId, record.PaymentHash, Constants.SdkCallDeadline.TotalSeconds),
                        cancellationToken)
                    .ConfigureAwait(false);

                // Null means the deadline passed, not that there is nothing there. Give up on this invoice for
                // this pass rather than paging on against a service that is not answering.
                if (payments is null)
                    return null;

                var match = payments.FirstOrDefault(p =>
                    p.PaymentHash == record.PaymentHash && p.Direction is SparkPaymentDirection.Receive);
                if (match is not null)
                    return match;

                // A short page is the end of the window.
                if (payments.Count < PaymentPageSize)
                    return null;
            }

            _logger.LogWarning(
                "Store {StoreId}: gave up looking for a payment matching invoice {PaymentHash} after {Count} "
                + "payments. If this repeats, the wallet's history has outgrown the scan window",
                record.StoreId, record.PaymentHash, MaxPaymentPages * PaymentPageSize);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: could not check the Spark service for invoice {PaymentHash} ({Reason})",
                record.StoreId, record.PaymentHash, SparkErrors.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// Resolves one invoice against the SDK and settles it if it has been paid. Returns the current record.
    /// </summary>
    public async Task<InvoiceRecord> ResolveAsync(
        ISparkSdkClient sdk,
        InvoiceRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        // Paid is terminal. A cancelled invoice is still scanned: on Spark it remains payable, so a late
        // payment must be found and credited here (see InvoiceRecord.EffectiveStatus).
        if (record.Status is InvoiceRecordStatus.Paid)
            return record;

        var payment = await FindReceiveAsync(sdk, record, cancellationToken).ConfigureAwait(false);
        if (payment is not { Status: SparkPaymentStatus.Completed, PaymentHash: not null })
            return record;

        var result = await ApplyAsync(record.StoreId, payment, cancellationToken).ConfigureAwait(false);
        return result.Record ?? record;
    }

    /// <summary>
    /// Reconciles every supplied store, isolating each one's failures from the others.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives here rather than in <c>SparkService</c> so the isolation guarantee is testable: the service's
    /// dependencies (a store repository, an event aggregator, a network provider) need a BTCPay host to
    /// construct, and "one store's failure must not skip the others" is exactly the sort of property that
    /// quietly stops holding.
    /// </para>
    /// <para>
    /// The walk itself belongs to <paramref name="pass"/>, which supplies the pass budget, the per-store
    /// deadline and the rotation. It is a parameter rather than something built here because the rotation
    /// position has to outlive a pass, and because the reconciliation task and the startup catch-up are the same
    /// walk over the same stores and should share one position rather than each starting from the top.
    /// </para>
    /// </remarks>
    public async Task<int> ReconcileStoresAsync(
        IEnumerable<SparkReconciliationTarget> targets,
        SparkStorePassScheduler pass,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(pass);

        // Indexed by store id because the scheduler walks ids, not targets — it owns the rotation and has to be
        // able to name where it got to. A repeated store id would collapse here rather than being reconciled
        // twice; that cannot arise from the only caller, which projects SparkService._instances, and reconciling
        // one store twice in a pass would be wasted work rather than a correctness problem in any case.
        var byStore = new Dictionary<string, ISparkSdkClient>(StringComparer.Ordinal);
        foreach (var target in targets)
            byStore[target.StoreId] = target.Sdk;

        // Interlocked rather than a plain increment: a store whose visit outlived its deadline is still running
        // when this method returns, and it must not race the read below or corrupt a later pass's count. Its
        // settlements are simply not counted here, which is honest — nothing waited to find out.
        var settled = 0;

        await pass.RunAsync(
                byStore.Keys,
                async (storeId, token) =>
                {
                    var count = await ReconcileStoreAsync(storeId, byStore[storeId], token).ConfigureAwait(false);
                    Interlocked.Add(ref settled, count);
                },
                (storeId, ex) =>
                {
                    if (ex is ObjectDisposedException)
                    {
                        // The store was reconfigured or removed mid-pass; the next pass picks up its replacement.
                        _logger.LogDebug(
                            "Store {StoreId}: Spark wallet went away during reconciliation", storeId);
                        return;
                    }

                    _logger.LogError(ex, "Store {StoreId}: Spark reconciliation failed", storeId);
                },
                cancellationToken)
            .ConfigureAwait(false);

        return Volatile.Read(ref settled);
    }

    /// <summary>
    /// Walks a store's still-settleable invoices, oldest first, and settles any that have been paid. Returns the
    /// number settled by this pass.
    /// </summary>
    /// <remarks>
    /// Pages by keyset to the end of the set rather than taking one page, so no invoice starves behind a
    /// sustained backlog. Bounded overall by <see cref="MaxInvoicesPerPass"/>, and the log says so honestly when
    /// the bound is what stopped the walk.
    /// </remarks>
    public async Task<int> ReconcileStoreAsync(
        string storeId,
        ISparkSdkClient sdk,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentNullException.ThrowIfNull(sdk);

        // Recently expired invoices are included on purpose. The service provider accepts a late payment and
        // Spark has no way to withdraw an invoice from it, so an invoice that expired moments ago can still
        // receive real money. Excluding it would leave that money unattributed in this plugin's own records.
        var settleableFrom = DateTimeOffset.UtcNow - ExpiredReconciliationGrace;

        var settled = 0;
        var examined = 0;
        // Resumed from where the previous pass stopped, not from the top. The per-pass cap exists to bound a
        // pass's work, but restarting at the oldest invoice every pass would make it a starvation line: with
        // more settleable invoices than the cap, the same oldest set is re-examined forever and everything
        // behind it is never reached. The cursor carries across passes and resets only when a pass drains the
        // set, so every invoice is reached within a bounded number of passes.
        InvoiceReconciliationCursor? cursor = _resumeCursors.TryGetValue(storeId, out var resume) ? resume : null;

        while (examined < MaxInvoicesPerPass)
        {
            var pageSize = Math.Min(InvoicePageSize, MaxInvoicesPerPass - examined);

            IReadOnlyList<InvoiceRecord> page;
            try
            {
                page = await _invoiceStore
                    .ListForReconciliationAsync(storeId, settleableFrom, cursor, pageSize, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Store {StoreId}: could not load Spark invoices to reconcile", storeId);
                return settled;
            }

            if (page.Count == 0)
                break;

            foreach (var record in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;
                // Advanced before the attempt, so a record that keeps failing cannot stall the walk on itself.
                cursor = new InvoiceReconciliationCursor(record.CreatedAt, record.PaymentHash);

                try
                {
                    var resolved = await ResolveAsync(sdk, record, cancellationToken).ConfigureAwait(false);
                    if (resolved.Status is InvoiceRecordStatus.Paid)
                        settled++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One bad invoice must not abandon the rest of the store's set.
                    _logger.LogWarning(ex,
                        "Store {StoreId}: could not reconcile invoice {PaymentHash}", storeId, record.PaymentHash);
                }
            }

            // A short page is the end of the set.
            if (page.Count < pageSize)
                break;
        }

        if (examined >= MaxInvoicesPerPass && cursor is { } stopped)
        {
            // The next pass resumes here rather than restarting at the oldest invoice, which is what makes the
            // sentence below true rather than a starvation line.
            _resumeCursors[storeId] = stopped;
            _logger.LogInformation(
                "Store {StoreId}: reconciliation examined its per-pass limit of {Count} Spark invoices; the "
                + "next pass resumes where this one stopped",
                storeId, MaxInvoicesPerPass);
        }
        else
        {
            // The set drained; the next pass starts from the top again.
            _resumeCursors.TryRemove(storeId, out _);
        }

        if (settled > 0)
        {
            _logger.LogInformation(
                "Store {StoreId}: reconciliation settled {Settled} Lightning invoice(s) whose settlement event "
                + "had been missed",
                storeId, settled);
        }

        return settled;
    }

    private static SparkSettlement ToSettlement(InvoiceRecord record) => new(
        record.StoreId,
        record.PaymentHash,
        record.Bolt11,
        record.AmountMsat,
        record.AmountReceivedMsat ?? 0,
        record.SettledAt ?? DateTimeOffset.UtcNow,
        record.ExpiresAt,
        record.Preimage);
}
