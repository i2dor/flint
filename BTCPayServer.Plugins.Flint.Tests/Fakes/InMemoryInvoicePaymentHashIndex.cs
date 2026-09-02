using BTCPayServer.Plugins.Flint.Data;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IInvoicePaymentHashIndex"/> with the same semantics as the EF implementation, so the
/// indexer and the contract tests can run without Postgres.
/// </summary>
public sealed class InMemoryInvoicePaymentHashIndex : IInvoicePaymentHashIndex
{
    private readonly Dictionary<string, InvoicePaymentHash> _entries = new(StringComparer.Ordinal);

    /// <summary>Thrown by <see cref="RecordAsync"/> when set, to exercise the failure path.</summary>
    public Exception? FailRecordWith { get; set; }

    public IReadOnlyDictionary<string, InvoicePaymentHash> Entries => _entries;

    public Task RecordAsync(InvoicePaymentHash entry, CancellationToken cancellationToken = default)
    {
        if (FailRecordWith is not null)
            throw FailRecordWith;

        // Write-once, like core's AddressInvoices and like the EF store's ON CONFLICT DO NOTHING: the first
        // writer of a hash wins, and a duplicate is not an error.
        _entries.TryAdd(entry.PaymentHash.ToLowerInvariant(), new InvoicePaymentHash
        {
            PaymentHash = entry.PaymentHash.ToLowerInvariant(),
            InvoiceId = entry.InvoiceId,
            PaymentMethodId = entry.PaymentMethodId,
            FirstSeenAt = entry.FirstSeenAt == default ? DateTimeOffset.UtcNow : entry.FirstSeenAt
        });
        return Task.CompletedTask;
    }

    public Task<InvoicePaymentHash?> FindByPaymentHashAsync(
        string paymentHash,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.GetValueOrDefault(paymentHash.ToLowerInvariant()));

    public Task<int> PruneBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var stale = _entries.Where(pair => pair.Value.FirstSeenAt < cutoff)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var hash in stale)
            _entries.Remove(hash);
        return Task.FromResult(stale.Count);
    }

    /// <summary>Seeds an association directly, standing in for a row written before this instance existed.</summary>
    public void Seed(InvoicePaymentHash entry) => _entries[entry.PaymentHash.ToLowerInvariant()] = entry;
}
