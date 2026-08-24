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

    public Task<bool> CancelAsync(string storeId, string paymentHash, CancellationToken cancellationToken = default)
    {
        var record = _records.GetValueOrDefault(paymentHash);
        if (record is null || record.StoreId != storeId)
            return Task.FromResult(false);
        return Task.FromResult(record.TryCancel());
    }

    public void Seed(InvoiceRecord record) => _records[record.PaymentHash] = record;
}
