using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// <see cref="IInvoiceRecordStore"/> over the plugin's own Postgres schema.
/// </summary>
/// <remarks>
/// <para>
/// One short-lived <see cref="SparkPluginDbContext"/> per operation, created from
/// <see cref="SparkPluginDbContextFactory"/>. That is the BTCPay plugin convention and it matters here:
/// these calls are made from the SDK event consumer loop, from the reconciliation task and from BTCPay's
/// Lightning listener, none of which is inside an HTTP request scope.
/// </para>
/// <para>
/// <b>Nothing here may open an explicit transaction.</b> <c>BaseDbContextFactory</c> configures
/// <c>EnableRetryOnFailure(10)</c>, and EF Core's retrying execution strategy throws
/// <c>InvalidOperationException("The configured execution strategy 'NpgsqlRetryingExecutionStrategy' does
/// not support user-initiated transactions")</c> on the first operation inside a
/// <c>BeginTransactionAsync</c> scope — reproduced on EF Core 10 against Postgres 17. An earlier revision
/// of this class did exactly that, which meant no invoice could ever settle. Where atomicity is required
/// it comes from a single conditional statement, not from a transaction; where more than one statement is
/// genuinely needed, each is individually safe to interleave and the ordering is reasoned about explicitly.
/// </para>
/// </remarks>
public class EfInvoiceRecordStore : IInvoiceRecordStore
{
    /// <summary>
    /// Attempts of the settle compare-and-set before giving up. More than one is only ever needed if the
    /// row appears between the update and the read-back, which requires a concurrent
    /// <see cref="AddAsync"/> for the same payment hash.
    /// </summary>
    private const int SettleAttempts = 2;

    private readonly SparkPluginDbContextFactory _contextFactory;

    public EfInvoiceRecordStore(SparkPluginDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task AddAsync(InvoiceRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var context = _contextFactory.CreateContext();
        context.InvoiceRecords.Add(record);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<InvoiceRecord?> GetAsync(
        string storeId,
        string paymentHash,
        CancellationToken cancellationToken = default)
    {
        await using var context = _contextFactory.CreateContext();
        return await context.InvoiceRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.PaymentHash == paymentHash && r.StoreId == storeId, cancellationToken);
    }

    public async Task<IReadOnlyList<InvoiceRecord>> ListAsync(
        string storeId,
        bool pendingOnly,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var context = _contextFactory.CreateContext();
        var query = context.InvoiceRecords.AsNoTracking().Where(r => r.StoreId == storeId);
        if (pendingOnly)
        {
            // Natural expiry is not persisted (see InvoiceRecord.EffectiveStatus), so "still payable" is a
            // two-part predicate rather than a status comparison.
            var now = DateTimeOffset.UtcNow;
            query = query.Where(r => r.Status == InvoiceRecordStatus.Unpaid && r.ExpiresAt > now);
        }

        return await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InvoiceRecord>> ListForReconciliationAsync(
        string storeId,
        DateTimeOffset settleableFrom,
        InvoiceReconciliationCursor? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var context = _contextFactory.CreateContext();
        var query = context.InvoiceRecords
            .AsNoTracking()
            .Where(r => r.StoreId == storeId
                        // A cancelled invoice is still payable on Spark, so it is as settleable as an
                        // unpaid one — only a paid invoice is terminal.
                        && r.Status != InvoiceRecordStatus.Paid
                        && r.ExpiresAt > settleableFrom);

        if (after is not null)
        {
            // Keyset, not offset: a settled record leaves this result set, so an offset would skip whatever had
            // shifted into its place.
            var cursorCreatedAt = after.CreatedAt;
            var cursorHash = after.PaymentHash;
            query = query.Where(r => r.CreatedAt > cursorCreatedAt
                                     || (r.CreatedAt == cursorCreatedAt
                                         && string.Compare(r.PaymentHash, cursorHash) > 0));
        }

        return await query
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.PaymentHash)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> TryRecordSdkPaymentIdAsync(
        string storeId,
        string paymentHash,
        string sdkPaymentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sdkPaymentId);
        await using var context = _contextFactory.CreateContext();

        // Guarded on Status and on the column still being empty, so this can never overwrite the id of an
        // already-settled payment — which for a self-payment would be the wrong leg's id. A cancelled
        // invoice is still settleable, so its id is recordable too.
        var updated = await context.InvoiceRecords
            .Where(r => r.PaymentHash == paymentHash
                        && r.StoreId == storeId
                        && r.Status != InvoiceRecordStatus.Paid
                        && r.SdkPaymentId == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.SdkPaymentId, sdkPaymentId),
                cancellationToken);
        return updated == 1;
    }

    public async Task<InvoiceSettlementResult> SettleAsync(
        string storeId,
        string paymentHash,
        string sdkPaymentId,
        long amountReceivedMsat,
        string? preimage,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sdkPaymentId);
        ArgumentOutOfRangeException.ThrowIfNegative(amountReceivedMsat);

        await using var context = _contextFactory.CreateContext();

        for (var attempt = 0; attempt < SettleAttempts; attempt++)
        {
            // A single conditional UPDATE, so the database decides the winner. Two concurrent callers — a
            // duplicated event, the reconciliation task, or BTCPay's poll — cannot both be told "Settled"
            // and so cannot both notify BTCPay.
            var updated = await context.InvoiceRecords
                .Where(r => r.PaymentHash == paymentHash
                            && r.StoreId == storeId
                            // Paid is the only terminal status: an Unpaid or cancelled invoice may still
                            // receive the payment being applied.
                            && r.Status != InvoiceRecordStatus.Paid)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(r => r.Status, InvoiceRecordStatus.Paid)
                        .SetProperty(r => r.SdkPaymentId, sdkPaymentId)
                        .SetProperty(r => r.AmountReceivedMsat, amountReceivedMsat)
                        .SetProperty(r => r.SettledAt, settledAt)
                        // A plain assignment, not a coalesce: the guard above means this row was unsettled,
                        // and an unsettled invoice never has a preimage.
                        .SetProperty(r => r.Preimage, preimage),
                    cancellationToken);

            var record = await context.InvoiceRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.PaymentHash == paymentHash && r.StoreId == storeId, cancellationToken);

            if (updated == 1)
            {
                // The read-back is a separate statement, and deliberately so: the alternative is a
                // transaction, which this context cannot have. Nothing ever deletes an InvoiceRecord or
                // moves one out of Paid, so the row we read is the row we just wrote.
                return record is null
                    ? new InvoiceSettlementResult(InvoiceSettlementOutcome.NotFound, null)
                    : new InvoiceSettlementResult(InvoiceSettlementOutcome.Settled, record);
            }

            if (record is null)
                return new InvoiceSettlementResult(InvoiceSettlementOutcome.NotFound, null);

            switch (record.Status)
            {
                case InvoiceRecordStatus.Paid:
                    // Someone else won the race, or this is a duplicate event. Backfill only what is still
                    // missing: the amount and the settlement time belong to whoever settled it first,
                    // because that is what BTCPay has already been told.
                    if (preimage is not null && record.Preimage is null)
                    {
                        var backfilled = await context.InvoiceRecords
                            .Where(r => r.PaymentHash == paymentHash
                                        && r.StoreId == storeId
                                        && r.Preimage == null)
                            .ExecuteUpdateAsync(
                                setters => setters.SetProperty(r => r.Preimage, preimage),
                                cancellationToken);
                        if (backfilled == 1)
                            record.Preimage = preimage;
                    }

                    return new InvoiceSettlementResult(InvoiceSettlementOutcome.AlreadySettled, record);

                default:
                    // Unsettled, yet the update matched nothing: the row was inserted or reworked between
                    // the two statements. Retry the compare-and-set against the row that now exists.
                    continue;
            }
        }

        return new InvoiceSettlementResult(InvoiceSettlementOutcome.NotFound, null);
    }

    public async Task<bool> CancelAsync(
        string storeId,
        string paymentHash,
        CancellationToken cancellationToken = default)
    {
        await using var context = _contextFactory.CreateContext();

        // A conditional UPDATE for the same reason SettleAsync uses one, and it is the more important of the
        // two: BTCPay cancels a superseded LNURL invoice at exactly the moment it may be settling. A
        // read-modify-write would issue an unconditional UPDATE and could turn a Paid invoice back into an
        // Expired one, losing a payment the merchant had already been credited for.
        var updated = await context.InvoiceRecords
            .Where(r => r.PaymentHash == paymentHash
                        && r.StoreId == storeId
                        && r.Status == InvoiceRecordStatus.Unpaid)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.Status, InvoiceRecordStatus.Expired),
                cancellationToken);
        return updated == 1;
    }
}
