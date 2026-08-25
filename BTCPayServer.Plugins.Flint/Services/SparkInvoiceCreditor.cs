using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>What one attempt to route a settlement to its BTCPay invoice concluded.</summary>
public enum SparkInvoiceCreditResult
{
    /// <summary>This attempt put the payment on the BTCPay invoice, and the record is marked credited.</summary>
    Credited,

    /// <summary>
    /// BTCPay already held the payment — its own listener got there first, or an earlier attempt did — and the
    /// record is marked credited. Indistinguishable from <see cref="Credited"/> in effect, which is the point.
    /// </summary>
    AlreadyRecorded,

    /// <summary>
    /// The record was already marked credited before this attempt, so nothing was done.
    /// </summary>
    AlreadyCredited,

    /// <summary>
    /// The record was already given up on by an earlier pass, which reported it then. Nothing was done and
    /// nothing was logged — the report is deliberately once per record, not once per pass.
    /// </summary>
    AlreadyAbandoned,

    /// <summary>
    /// BTCPay has no invoice indexed against this payment hash yet. Left uncredited so the next pass retries.
    /// </summary>
    Deferred,

    /// <summary>
    /// BTCPay has no invoice for this payment hash and the retry horizon has passed, so no further attempt will
    /// be made. Reported to the operator with its amount, and stamped
    /// <see cref="InvoiceRecord.CreditAbandonedAt"/> — never <see cref="InvoiceRecord.CreditedAt"/>, because it
    /// never was credited. That stamp is what makes both the retry and the report stop.
    /// </summary>
    Abandoned,

    /// <summary>
    /// BTCPay's invoice for this payment hash belongs to a different store than the wallet the money arrived
    /// in. Refused outright and nothing was marked.
    /// </summary>
    RefusedCrossStore,

    /// <summary>
    /// BTCPay cannot hold a payment for this payment method on that invoice. Terminal, because retrying cannot
    /// change it — stamped <see cref="InvoiceRecord.CreditAbandonedAt"/> for the same reason
    /// <see cref="Abandoned"/> is: the money is in the wallet and no BTCPay invoice records it, and the row has
    /// to say so.
    /// </summary>
    Unrecordable,

    /// <summary>
    /// The attempt failed — BTCPay's database was unreachable, or something unexpected threw. Left uncredited
    /// so the next pass retries.
    /// </summary>
    Failed
}

/// <summary>
/// Decides whether, and to which BTCPay invoice, a recorded Spark settlement is credited.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> Settling an <see cref="InvoiceRecord"/> records that money arrived; it does not
/// put a payment on the merchant's BTCPay invoice. Ordinarily BTCPay's Lightning listener does that, woken by
/// <see cref="SparkSettlementBroadcaster"/> — but that listener watches only each invoice's <em>current</em>
/// payment prompt. When BTCPay replaces BOLT11 X with BOLT11 Y and the process then restarts, only Y is
/// watched again; X stays payable on the service provider, which has no cancellation primitive, and a payment
/// to X afterwards settles this plugin's record while the BTCPay invoice stays unpaid. The same gap opens when
/// a listening session is saturated past its retry allowance, and when the process dies between the settle and
/// the notification. In every one of those cases the money is in the merchant's wallet and their invoice says
/// it is not.
/// </para>
/// <para>
/// So the credit is a durable step of its own, attempted on the settlement path and retried by the
/// reconciliation pass until it lands (<see cref="InvoiceRecord.CreditedAt"/>). Both callers reach the same
/// decision here, for the same reason <see cref="SparkSettlementReconciler"/> exists: "the merchant is
/// credited exactly once" is a property of one shared implementation, not of two that agree today.
/// </para>
/// <para>
/// <b>Nothing here can lose a settlement.</b> Every failure path leaves both credit columns null, which is the
/// retry queue, and no failure is allowed to propagate into the settlement path — a BTCPay database that is
/// briefly unreachable must not stop this plugin recording that the money arrived, nor stop the notification
/// that usually makes this whole class unnecessary.
/// </para>
/// <para>
/// <b>And nothing here can quietly stop trying.</b> A settlement leaves the retry queue by exactly one of two
/// stamps, and which one it is says what happened: <see cref="InvoiceRecord.CreditedAt"/> means the money is on
/// a BTCPay invoice, <see cref="InvoiceRecord.CreditAbandonedAt"/> means it never will be and an operator has
/// been told once, with the amount and the payment hash. Nothing else terminates a retry — in particular there
/// is no path where a row simply ages out of the walk with both columns null, which is what an earlier revision
/// did: the horizon check here and the walk's listing bound were the same value, so the report this class was
/// written to emit could never fire (see <see cref="AbandonedReportingGrace"/>).
/// </para>
/// </remarks>
public class SparkInvoiceCreditor
{
    /// <summary>
    /// How long after settlement a credit is still retried when BTCPay has no invoice for the payment hash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two very different situations produce "no BTCPay invoice for this hash", and the horizon is what keeps
    /// them apart without having to tell them apart. The first is a race measured in milliseconds: BTCPay
    /// writes its <c>AddressInvoices</c> row just <em>after</em> <c>CreateInvoice</c> returns, so a payment
    /// that arrives in that window finds nothing, and the next pass finds it. The second is permanent: a
    /// BOLT11 minted through this plugin's own Greenfield endpoints, or by a merchant experimenting, was never
    /// attached to a BTCPay invoice and never will be. Without a bound the second case is re-attempted on
    /// every pass for the lifetime of the server.
    /// </para>
    /// <para>
    /// Seven days, which is far longer than the millisecond race needs and far longer than any BTCPay invoice
    /// stays open, because the cost of being generous is one indexed lookup per pass over a set that is
    /// normally empty, while the cost of being stingy is a merchant's payment silently never reaching their
    /// invoice. Deliberately much longer than
    /// <c>SparkSettlementReconciler.ExpiredReconciliationGrace</c>: that hour bounds a scan of the service
    /// provider's payment history for money that probably never arrived, whereas this bounds the routing of
    /// money that certainly did.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan CreditRetryHorizon = TimeSpan.FromDays(7);

    /// <summary>
    /// How long past <see cref="CreditRetryHorizon"/> a settlement stays visible to the credit walk, so that a
    /// pass can classify it as abandoned and say so once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because of a defect, and the defect is worth naming.</b> The walk's listing and this
    /// class's horizon check were originally the same boundary, which meant a record aged out of the listing at
    /// the exact moment it became eligible to be reported: the <see cref="SparkInvoiceCreditResult.Abandoned"/>
    /// branch was unreachable from the pass, no operator warning was ever emitted for a settlement that could
    /// never be credited, and the row sat with a null credit timestamp forever — indistinguishable in the
    /// database from one still in flight. The listing therefore has to outlast the horizon.
    /// </para>
    /// <para>
    /// Seven days again, which is enormously more than needed: the pass runs every few minutes, so a record
    /// crossing the horizon is classified, reported and stamped on the pass after it crosses. The slack is
    /// generous because the cost of it is nothing — a record is stamped terminal the first time it is examined
    /// past the horizon and permanently leaves the set — while the cost of it being too tight is exactly the
    /// silence described above. A server that was down for the whole fortnight is the only case where a record
    /// still ages out unreported, and that is bounded by design rather than accidental: the alternative is
    /// re-examining every settlement ever recorded on every pass.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan AbandonedReportingGrace = TimeSpan.FromDays(7);

    private readonly IInvoiceCreditGateway _gateway;
    private readonly IInvoiceRecordStore _invoiceStore;
    private readonly ILogger<SparkInvoiceCreditor> _logger;

    public SparkInvoiceCreditor(
        IInvoiceCreditGateway gateway,
        IInvoiceRecordStore invoiceStore,
        ILogger<SparkInvoiceCreditor> logger)
    {
        _gateway = gateway;
        _invoiceStore = invoiceStore;
        _logger = logger;
    }

    /// <summary>The oldest settlement a credit is still <em>attempted</em> for, given the horizon.</summary>
    public static DateTimeOffset CreditableFrom(DateTimeOffset now) => now - CreditRetryHorizon;

    /// <summary>
    /// The oldest settlement the credit walk still <em>lists</em>, which is deliberately older than
    /// <see cref="CreditableFrom"/>.
    /// </summary>
    /// <remarks>
    /// The two must not be the same value. A record that is only listed while it is still creditable can never
    /// be seen by the pass on the far side of the horizon, so it is never classified, never reported, and never
    /// stamped — see <see cref="AbandonedReportingGrace"/>, which is the difference between them.
    /// </remarks>
    public static DateTimeOffset ListableFrom(DateTimeOffset now) =>
        now - CreditRetryHorizon - AbandonedReportingGrace;

    /// <summary>
    /// Routes one settled invoice's payment to the BTCPay invoice its BOLT11 was minted for. Never throws.
    /// </summary>
    /// <remarks>
    /// Never throws because both callers are paths that must not be derailed by it: the settlement path has
    /// already committed the settlement and published the notification by the time it gets here, and the
    /// reconciliation pass is walking other stores' invoices behind it.
    /// </remarks>
    public async Task<SparkInvoiceCreditResult> CreditAsync(
        InvoiceRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Status is not InvoiceRecordStatus.Paid)
        {
            // Not a settlement, so there is nothing to credit. A caller that reached here with an unpaid row
            // has a bug, but refusing loudly and doing nothing is the safe answer: marking anything would
            // suppress the credit of the payment that may still arrive.
            throw new ArgumentException(
                "Only a settled invoice can be credited to a BTCPay invoice.", nameof(record));
        }

        if (record.CreditedAt is not null)
            return SparkInvoiceCreditResult.AlreadyCredited;

        // Terminal in the other direction, and silent on purpose: a previous pass concluded this settlement can
        // never reach a BTCPay invoice and said so, with the amount and the hash, at operator level. Repeating
        // that on every pass for the life of the server would bury it.
        if (record.CreditAbandonedAt is not null)
            return SparkInvoiceCreditResult.AlreadyAbandoned;

        try
        {
            return await AttemptAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Left uncredited on purpose: null is the retry queue, and the next pass tries again.
            _logger.LogWarning(ex,
                "Store {StoreId}: could not route the settled Lightning payment {PaymentHash} to its BTCPay "
                + "invoice ({Reason}). The settlement is recorded and the credit will be retried",
                record.StoreId, record.PaymentHash, SparkErrors.Describe(ex));
            return SparkInvoiceCreditResult.Failed;
        }
    }

    private async Task<SparkInvoiceCreditResult> AttemptAsync(
        InvoiceRecord record,
        CancellationToken cancellationToken)
    {
        var match = await _gateway
            .FindByPaymentHashAsync(record.PaymentHash, cancellationToken)
            .ConfigureAwait(false);
        if (match is null)
        {
            return await NotAttributableAsync(
                    record, "BTCPay has no invoice indexed against it", cancellationToken)
                .ConfigureAwait(false);
        }

        // The cross-store refusal. BTCPay's index is keyed on the payment hash alone, and this plugin's own
        // records are keyed the same way, so a hash that resolved to another store's invoice would credit that
        // store for money that arrived in this one's wallet. Nothing in the normal flow can produce it — the
        // hash came from a BOLT11 this store's wallet minted — which is exactly why it is checked rather than
        // assumed: the check costs a comparison, and the failure it prevents is money credited to the wrong
        // merchant. Nothing is marked, so if the mismatch is ever explained the credit is still owed.
        if (!string.Equals(match.StoreId, record.StoreId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Store {StoreId}: the Lightning payment {PaymentHash} resolves to BTCPay invoice "
                + "{InvoiceId}, which belongs to store {OtherStoreId}. Refusing to credit it — the money "
                + "arrived in this store's wallet and crediting another store's invoice would be a "
                + "misattribution. This should be impossible; investigate before settling it by hand",
                record.StoreId, record.PaymentHash, match.InvoiceId, match.StoreId);
            return SparkInvoiceCreditResult.RefusedCrossStore;
        }

        if (match.AlreadyHasPayment)
        {
            // The ordinary case: BTCPay's own listener was watching and credited it. Marking it here is what
            // stops every later pass asking again.
            await MarkCreditedAsync(record, cancellationToken).ConfigureAwait(false);
            return SparkInvoiceCreditResult.AlreadyRecorded;
        }

        var outcome = await _gateway
            .AddSettledPaymentAsync(
                new SparkInvoiceCreditRequest(
                    match.InvoiceId,
                    match.PaymentMethodId,
                    record.PaymentHash,
                    record.Bolt11,
                    // What arrived, never what was invoiced. The received amount is the only honest figure —
                    // crediting the invoiced one is a defect the prior-art plugin shipped.
                    record.AmountReceivedMsat ?? 0,
                    record.Preimage,
                    record.SettledAt ?? DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);

        switch (outcome)
        {
            case SparkInvoiceCreditOutcome.CreditedNow:
                _logger.LogInformation(
                    "Store {StoreId}: the Lightning payment {PaymentHash} was credited to BTCPay invoice "
                    + "{InvoiceId}, which was no longer being watched for it",
                    record.StoreId, record.PaymentHash, match.InvoiceId);
                await MarkCreditedAsync(record, cancellationToken).ConfigureAwait(false);
                return SparkInvoiceCreditResult.Credited;

            case SparkInvoiceCreditOutcome.AlreadyRecorded:
                // BTCPay's listener, or a concurrent pass, won the race on the payments primary key. The
                // merchant is credited exactly once and this is the caller that was not it.
                _logger.LogDebug(
                    "Store {StoreId}: BTCPay invoice {InvoiceId} already held the Lightning payment "
                    + "{PaymentHash}",
                    record.StoreId, match.InvoiceId, record.PaymentHash);
                await MarkCreditedAsync(record, cancellationToken).ConfigureAwait(false);
                return SparkInvoiceCreditResult.AlreadyRecorded;

            case SparkInvoiceCreditOutcome.PromptMissing:
                // A prompt is never removed from an invoice's blob, so a retry would fail identically. Stamped
                // terminal to stop an endless retry — as abandoned, not as credited, because the money is not on
                // the invoice and the row must not claim it is — and logged loudly, because only a human can
                // reconcile this one.
                _logger.LogWarning(
                    "Store {StoreId}: BTCPay invoice {InvoiceId} has no payment prompt that can hold the "
                    + "Lightning payment {PaymentHash} ({AmountSats} sat). The money is in the Spark wallet "
                    + "and recorded here, but the BTCPay invoice cannot be credited automatically and will "
                    + "not be retried",
                    record.StoreId, match.InvoiceId, record.PaymentHash,
                    (record.AmountReceivedMsat ?? 0) / 1000);
                await MarkAbandonedAsync(record, cancellationToken).ConfigureAwait(false);
                return SparkInvoiceCreditResult.Unrecordable;

            default:
                // InvoiceGone: the invoice disappeared between the lookup and the insert, which puts us back
                // in the same position as never having found it.
                return await NotAttributableAsync(
                        record, "its BTCPay invoice was gone by the time it was credited", cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports a settlement that has no BTCPay invoice to attach to, at the volume its age deserves, and gives
    /// up on it once it is past the horizon.
    /// </summary>
    /// <remarks>
    /// The report and the stamp are one step deliberately: the warning is emitted only when
    /// <see cref="IInvoiceRecordStore.MarkCreditAbandonedAsync"/> says <em>this</em> caller is the one that
    /// stamped the row, so a settlement that can never be credited is reported to the operator exactly once —
    /// with its amount and its payment hash, which is what a human needs to find the money by hand — rather than
    /// on every pass forever or, as an earlier revision managed, never at all.
    /// </remarks>
    private async Task<SparkInvoiceCreditResult> NotAttributableAsync(
        InvoiceRecord record,
        string reason,
        CancellationToken cancellationToken)
    {
        var settledAt = record.SettledAt ?? record.CreatedAt;
        if (DateTimeOffset.UtcNow - settledAt <= CreditRetryHorizon)
        {
            // Expected in the window between CreateInvoice returning and BTCPay indexing the hash, so debug:
            // an operator-level line here would fire on ordinary checkouts.
            _logger.LogDebug(
                "Store {StoreId}: the settled Lightning payment {PaymentHash} cannot be credited yet because "
                + "{Reason}; the next reconciliation pass will try again",
                record.StoreId, record.PaymentHash, reason);
            return SparkInvoiceCreditResult.Deferred;
        }

        if (await MarkAbandonedAsync(record, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Store {StoreId}: the settled Lightning payment {PaymentHash} ({AmountSats} sat) has had no "
                + "BTCPay invoice to credit for {Horizon}, so it will no longer be retried and is recorded as "
                + "abandoned. This is expected for a BOLT11 that was never issued for a BTCPay invoice; "
                + "otherwise the money is in the Spark wallet and no BTCPay invoice records it",
                record.StoreId, record.PaymentHash,
                (record.AmountReceivedMsat ?? 0) / 1000, CreditRetryHorizon);
        }

        return SparkInvoiceCreditResult.Abandoned;
    }

    /// <summary>
    /// Stamps the record as credited, tolerating the loser of a race.
    /// </summary>
    /// <remarks>
    /// A false return is not a failure: it means the other caller of this compare-and-set stamped it first,
    /// and "credited" is what both of them concluded.
    /// </remarks>
    private async Task MarkCreditedAsync(InvoiceRecord record, CancellationToken cancellationToken)
    {
        var creditedAt = DateTimeOffset.UtcNow;
        if (await _invoiceStore
                .MarkCreditedAsync(record.StoreId, record.PaymentHash, creditedAt, cancellationToken)
                .ConfigureAwait(false))
        {
            // Kept in step so a caller holding this instance — the settlement path does — does not re-attempt
            // the credit it just completed. The same value that was persisted, not a second reading of the
            // clock, so an in-hand record cannot disagree with the row about when the credit landed.
            record.CreditedAt ??= creditedAt;
        }
    }

    /// <summary>
    /// Stamps the record as never creditable, and reports whether this call is what stamped it.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="MarkCreditedAsync"/> the return value is used, because it is what makes the operator
    /// warning exactly-once: only the caller that won the compare-and-set logs. The store refuses to stamp a row
    /// that has since been credited, so the loser of that race is a pass that was about to report money as
    /// unaccounted-for when it had in fact just been accounted for.
    /// </remarks>
    private async Task<bool> MarkAbandonedAsync(InvoiceRecord record, CancellationToken cancellationToken)
    {
        var abandonedAt = DateTimeOffset.UtcNow;
        if (!await _invoiceStore
                .MarkCreditAbandonedAsync(record.StoreId, record.PaymentHash, abandonedAt, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        // Kept in step for the same reason the credit stamp is: a caller holding this instance must not attempt
        // the credit again, and must see the value that was actually persisted.
        record.CreditAbandonedAt ??= abandonedAt;
        return true;
    }
}
