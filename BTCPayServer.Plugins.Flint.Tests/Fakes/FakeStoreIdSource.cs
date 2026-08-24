using BTCPayServer.Plugins.Flint.Services;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ISparkStoreIdSource"/> for tests of the configuration sweep.
/// </summary>
public sealed class FakeStoreIdSource : ISparkStoreIdSource
{
    private readonly string[] _storeIds;

    public FakeStoreIdSource(params string[] storeIds)
    {
        _storeIds = storeIds;
    }

    public Task<string[]> GetStoreIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_storeIds);
}
