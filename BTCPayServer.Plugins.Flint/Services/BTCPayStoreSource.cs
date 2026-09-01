using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// <see cref="ISparkStoreSource"/> over BTCPay's whole store table.
/// </summary>
/// <remarks>
/// The concrete <see cref="StoreRepository"/> is resolved through a deferred <c>Func</c> rather than injected:
/// this is reachable from the configuration sweep, which <c>SparkService</c> triggers at startup, and
/// <c>SparkService</c> is itself a dependency of the sweep — the same deferral the connection-string handler
/// and the value oracle use to keep the container's graph acyclic.
/// </remarks>
public sealed class BTCPayStoreSource : ISparkStoreSource
{
    private readonly Func<StoreRepository> _repository;

    public BTCPayStoreSource(Func<StoreRepository> repository)
    {
        _repository = repository;
    }

    public Task<StoreData[]> GetStoresAsync(CancellationToken cancellationToken = default) =>
        _repository().GetStores();
}
