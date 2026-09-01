using BTCPayServer.Data;
using BTCPayServer.Plugins.Flint.Services;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IStoreLightningConfigStore"/>.
/// </summary>
/// <remarks>
/// Models the two behaviours the real one has that matter to the logic above it: a store that does not exist
/// (absent from <see cref="Stores"/>) reads as null and refuses writes, and a write of null removes the
/// configuration rather than storing an empty one. Records every write so a test can assert that nothing was
/// touched, which is the whole point of the ownership rules, and optionally into a shared
/// <see cref="WriteLog"/> so ordering against other fakes is assertable.
/// </remarks>
public sealed class FakeStoreLightningConfigStore : IStoreLightningConfigStore
{
    private readonly WriteLog? _writeLog;

    public FakeStoreLightningConfigStore(WriteLog? writeLog = null)
    {
        _writeLog = writeLog;
    }

    /// <summary>Store id to configuration. A store absent from here does not exist.</summary>
    public Dictionary<string, StoreLightningConfig> Stores { get; } = [];

    /// <summary>Every <see cref="SetAsync"/> call, in order, including ones that found no store.</summary>
    public List<(string StoreId, string? ConnectionString)> Writes { get; } = [];

    public static FakeStoreLightningConfigStore WithStore(
        string storeId,
        string? connectionString = null,
        bool isInternalNode = false,
        bool enabled = true,
        WriteLog? writeLog = null)
        => new FakeStoreLightningConfigStore(writeLog).Add(storeId, connectionString, isInternalNode, enabled);

    public FakeStoreLightningConfigStore Add(
        string storeId,
        string? connectionString = null,
        bool isInternalNode = false,
        bool enabled = true)
    {
        Stores[storeId] = new StoreLightningConfig(isInternalNode, connectionString, enabled);
        return this;
    }

    /// <summary>How many reads went through the store-id lookup — a sweep classifying from loaded rows
    /// must not use it per store.</summary>
    public int IdReads { get; private set; }

    public Task<StoreLightningConfig?> GetAsync(string storeId, CancellationToken cancellationToken = default)
    {
        IdReads++;
        return Task.FromResult(Stores.TryGetValue(storeId, out var config) ? config : null);
    }

    public StoreLightningConfig Read(StoreData store) =>
        // A row that exists but holds no entry here stands for a row with no BTC-LN configuration —
        // the same answer the real Read gives when the payment-method config is absent.
        Stores.TryGetValue(store.Id, out var config)
            ? config
            : new StoreLightningConfig(false, null, false);

    /// <summary>Thrown by the next <see cref="SetAsync"/> — a store repository whose write failed.</summary>
    public Exception? FailNextSetWith { get; set; }

    public Task<bool> SetAsync(
        string storeId,
        string? connectionString,
        CancellationToken cancellationToken = default)
    {
        if (FailNextSetWith is { } failure)
        {
            FailNextSetWith = null;
            throw failure;
        }

        Writes.Add((storeId, connectionString));
        _writeLog?.Record($"lightning:{storeId}:{(connectionString is null ? "cleared" : "set")}");

        if (!Stores.ContainsKey(storeId))
            return Task.FromResult(false);

        Stores[storeId] = connectionString is null
            ? new StoreLightningConfig(false, null, false)
            : new StoreLightningConfig(false, connectionString, true);
        return Task.FromResult(true);
    }
}
