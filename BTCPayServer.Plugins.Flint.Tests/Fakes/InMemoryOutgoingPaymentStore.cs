using BTCPayServer.Plugins.Flint.Data;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IOutgoingPaymentStore"/> with the same observable semantics as the EF implementation.
/// </summary>
public sealed class InMemoryOutgoingPaymentStore : IOutgoingPaymentStore
{
    private readonly Dictionary<(string StoreId, string PaymentHash), OutgoingPaymentRecord> _records = [];

    /// <summary>Thrown by <see cref="RegisterAttemptAsync"/> when set.</summary>
    public Exception? FailRegisterWith { get; set; }

    /// <summary>Thrown by <see cref="TryMarkReportedAsync"/> when set.</summary>
    public Exception? FailMarkWith { get; set; }

    public IReadOnlyDictionary<(string StoreId, string PaymentHash), OutgoingPaymentRecord> Records => _records;

    public Task<OutgoingPaymentRecord> RegisterAttemptAsync(
        string storeId,
        string paymentHash,
        string idempotencyKey,
        string bolt11,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (FailRegisterWith is not null)
            throw FailRegisterWith;

        var key = (storeId, paymentHash);
        if (_records.TryGetValue(key, out var existing))
        {
            existing.AttemptCount++;
            // Returned by value so the caller cannot see a later mutation, matching the EF store's AsNoTracking
            // read. This matters: the client decides on attempt.ReportedAt and must not observe its own write.
            return Task.FromResult(Copy(existing));
        }

        var created = new OutgoingPaymentRecord
        {
            PaymentHash = paymentHash,
            StoreId = storeId,
            IdempotencyKey = idempotencyKey,
            Bolt11 = bolt11,
            FirstAttemptAt = now,
            AttemptCount = 1
        };
        _records[key] = created;
        return Task.FromResult(Copy(created));
    }

    public Task<bool> TryMarkReportedAsync(
        string storeId,
        string paymentHash,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (FailMarkWith is not null)
            throw FailMarkWith;

        if (!_records.TryGetValue((storeId, paymentHash), out var record) || record.ReportedAt is not null)
            return Task.FromResult(false);

        record.ReportedAt = now;
        return Task.FromResult(true);
    }

    private static OutgoingPaymentRecord Copy(OutgoingPaymentRecord source) => new()
    {
        PaymentHash = source.PaymentHash,
        StoreId = source.StoreId,
        IdempotencyKey = source.IdempotencyKey,
        Bolt11 = source.Bolt11,
        FirstAttemptAt = source.FirstAttemptAt,
        AttemptCount = source.AttemptCount,
        ReportedAt = source.ReportedAt
    };
}
