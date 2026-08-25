using BTCPayServer.Plugins.Flint.Data;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IInvoiceRecordStore"/> with the same semantics as the EF implementation, so the
/// client's behaviour can be tested without Postgres.
/// </summary>
public sealed class InMemoryInvoiceRecordStore : IInvoiceRecordStore
{
    private readonly Dictionary<string, InvoiceRecord> _records = [];

    /// <summary>Thrown by <see cref="AddAsync"/> when set, to exercise the persistence-failure path.</summary>
    public Exception? FailAddWith { get; set; }

    /// <summary>
    /// Payment hashes whose <see cref="SettleAsync"/> throws, to exercise the per-invoice failure path.
    /// </summary>
    /// <remarks>
    /// A store-level hook rather than an SDK-level one, deliberately. <c>FindReceiveAsync</c> catches everything
    /// the SDK throws by design, so a failure injected there never reaches the reconciler's per-invoice
    /// <c>try/catch</c> — a test aimed at that catch has to fail somewhere the reconciler does not already
    /// swallow.
    /// </remarks>
    public HashSet<string> FailSettleFor { get; } = [];

    /// <summary>
    /// Thrown by <see cref="ListForReconciliationAsync"/> when set, to exercise the reconciler's isolation of
    /// the two walks.
    /// </summary>
    /// <remarks>
    /// The settlement walk and the credit walk read different queries of the same table, and the credit of money
    /// already received must not depend on the other query succeeding — a property that only a test which breaks
    /// one of them can pin.
    /// </remarks>
    public Exception? FailReconciliationListWith { get; set; }

    public IReadOnlyDictionary<string, InvoiceRecord> Records => _records;

    public Task AddAsync(InvoiceRecord record, CancellationToken cancellationToken = default)
    {
        if (FailAddWith is not null)
            throw FailAddWith;
        if (!_records.TryAdd(record.PaymentHash, record))
            throw new InvalidOperationException($"Duplicate payment hash {record.PaymentHash}");
        return Task.CompletedTask;
    }

    public Task<InvoiceRecord?> GetAsync(
        string storeId,
        string paymentHash,
        CancellationToken cancellationToken = default)
    {
        var record = _records.GetValueOrDefault(paymentHash);
        return Task.FromResult(record?.StoreId == storeId ? record : null);
    }

    public Task<IReadOnlyList<InvoiceRecord>> ListAsync(
        string storeId,
        bool pendingOnly,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        IEnumerable<InvoiceRecord> query = _records.Values.Where(r => r.StoreId == storeId);
        if (pendingOnly)
            query = query.Where(r => r.Status is InvoiceRecordStatus.Unpaid && r.ExpiresAt > now);
        return Task.FromResult<IReadOnlyList<InvoiceRecord>>(
            query.OrderByDescending(r => r.CreatedAt).Skip(offset).Take(limit).ToList());
    }

    public Task<IReadOnlyList<InvoiceRecord>> ListForReconciliationAsync(
        string storeId,
        DateTimeOffset settleableFrom,
        InvoiceReconciliationCursor? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (FailReconciliationListWith is not null)
            throw FailReconciliationListWith;

        IEnumerable<InvoiceRecord> query = _records.Values
            .Where(r => r.StoreId == storeId
                        && r.Status is not InvoiceRecordStatus.Paid
                        && r.ExpiresAt > settleableFrom);

        if (after is not null)
        {
            query = query.Where(r => r.CreatedAt > after.CreatedAt
                                     || (r.CreatedAt == after.CreatedAt
                                         && string.Compare(r.PaymentHash, after.PaymentHash,
                                             StringComparison.Ordinal) > 0));
        }

        return Task.FromResult<IReadOnlyList<InvoiceRecord>>(query
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.PaymentHash, StringComparer.Ordinal)
            .Take(limit)
            .ToList());
    }

    public Task<bool> TryRecordSdkPaymentIdAsync(
        string storeId,
        string paymentHash,
        string sdkPaymentId,
        CancellationToken cancellationToken = default)
    {
        var record = _records.GetValueOrDefault(paymentHash);
        if (record is null || record.StoreId != storeId ||
            record.Status is InvoiceRecordStatus.Paid || record.SdkPaymentId is not null)
        {
            return Task.FromResult(false);
        }

        record.SdkPaymentId = sdkPaymentId;
        return Task.FromResult(true);
    }

    public Task<InvoiceSettlementResult> SettleAsync(
        string storeId,
        string paymentHash,
        string sdkPaymentId,
        long amountReceivedMsat,
        string? preimage,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken = default)
    {
        if (FailSettleFor.Contains(paymentHash))
            throw new InvalidOperationException($"settle failure injected for {paymentHash}");

        var record = _records.GetValueOrDefault(paymentHash);
        if (record is null || record.StoreId != storeId)
            return Task.FromResult(new InvoiceSettlementResult(InvoiceSettlementOutcome.NotFound, null));

        var outcome = record.TrySettle(sdkPaymentId, amountReceivedMsat, preimage, settledAt);
        return Task.FromResult(new InvoiceSettlementResult(outcome, record));
    }

    public Task<bool> MarkCreditedAsync(
        string storeId,
        string paymentHash,
        DateTimeOffset creditedAt,
        CancellationToken cancellationToken = default)
    {
        var record = _records.GetValueOrDefault(paymentHash);
        if (record is null || record.StoreId != storeId)
            return Task.FromResult(false);
        return Task.FromResult(record.TryMarkCredited(creditedAt));
    }

    public Task<bool> MarkCreditAbandonedAsync(
        string storeId,
        string paymentHash,
        DateTimeOffset abandonedAt,
        CancellationToken cancellationToken = default)
    {
        var record = _records.GetValueOrDefault(paymentHash);
        if (record is null || record.StoreId != storeId)
            return Task.FromResult(false);
        return Task.FromResult(record.TryMarkCreditAbandoned(abandonedAt));
    }

    public Task<IReadOnlyList<string>> ListStoreIdsAwaitingCreditAsync(
        DateTimeOffset settledFrom,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(_records.Values
            .Where(r => AwaitingCredit(r, settledFrom))
            .Select(r => r.StoreId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(storeId => storeId, StringComparer.Ordinal)
            .Take(limit)
            .ToList());

    public Task<IReadOnlyList<InvoiceRecord>> ListUncreditedAsync(
        string storeId,
        DateTimeOffset settledFrom,
        InvoiceReconciliationCursor? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<InvoiceRecord> query = _records.Values
            .Where(r => r.StoreId == storeId && AwaitingCredit(r, settledFrom));

        if (after is not null)
        {
            query = query.Where(r => r.CreatedAt > after.CreatedAt
                                     || (r.CreatedAt == after.CreatedAt
                                         && string.Compare(r.PaymentHash, after.PaymentHash,
                                             StringComparison.Ordinal) > 0));
        }

        return Task.FromResult<IReadOnlyList<InvoiceRecord>>(query
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.PaymentHash, StringComparer.Ordinal)
            .Take(limit)
            .ToList());
    }

    public Task<bool> CancelAsync(string storeId, string paymentHash, CancellationToken cancellationToken = default)
    {
        var record = _records.GetValueOrDefault(paymentHash);
        if (record is null || record.StoreId != storeId)
            return Task.FromResult(false);
        return Task.FromResult(record.TryCancel());
    }

    /// <summary>
    /// The uncredited predicate, shared by both listings so they cannot drift apart the way the EF store's two
    /// <c>Where</c> clauses could not.
    /// </summary>
    private static bool AwaitingCredit(InvoiceRecord record, DateTimeOffset settledFrom) =>
        record.Status is InvoiceRecordStatus.Paid
        && record.CreditedAt is null
        && record.CreditAbandonedAt is null
        && record.SettledAt > settledFrom;

    public void Seed(InvoiceRecord record) => _records[record.PaymentHash] = record;
}
