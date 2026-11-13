using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// Thread-safe in-memory <see cref="ISparkLedgerStore"/> for unit tests.
/// </summary>
public sealed class InMemorySparkLedgerStore : ISparkLedgerStore
{
    private readonly object _lock = new();
    private readonly List<SparkLedgerEntry> _entries = [];

    public IReadOnlyList<SparkLedgerEntry> All
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public Task AppendAsync(SparkLedgerEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsForSdkPaymentAsync(
        string storeId,
        string sdkPaymentId,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var exists = _entries.Any(e =>
                e.StoreId == storeId && e.SdkPaymentId == sdkPaymentId);
            return Task.FromResult(exists);
        }
    }

    public Task<bool> ExistsForPaymentHashAsync(
        string storeId,
        string paymentHash,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var exists = _entries.Any(e =>
                e.StoreId == storeId && e.PaymentHash == paymentHash);
            return Task.FromResult(exists);
        }
    }

    public Task<IReadOnlyList<SparkLedgerEntry>> ListAsync(
        string storeId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<SparkLedgerEntry> result = _entries
                .Where(e => e.StoreId == storeId)
                .OrderByDescending(e => e.OccurredAt)
                .Skip(skip)
                .Take(take)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<long> CountAsync(string storeId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var count = (long)_entries.Count(e => e.StoreId == storeId);
            return Task.FromResult(count);
        }
    }
}
