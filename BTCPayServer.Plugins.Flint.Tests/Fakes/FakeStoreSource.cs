using BTCPayServer.Data;
using BTCPayServer.Plugins.Flint.Services;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ISparkStoreSource"/> for tests of the configuration sweep.
/// </summary>
/// <remarks>
/// The rows carry only their id: sweep tests read each store's Lightning configuration through
/// <see cref="FakeStoreLightningConfigStore"/>, which keys off the id, and none of them parse the row's
/// JSONB columns — that mapping is the real config store's job and is exercised by the store-test suite.
/// </remarks>
public sealed class FakeStoreSource : ISparkStoreSource
{
    private readonly StoreData[] _stores;

    public FakeStoreSource(params string[] storeIds)
    {
        _stores = storeIds.Select(id => new StoreData { Id = id }).ToArray();
    }

    public Task<StoreData[]> GetStoresAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_stores);
}
