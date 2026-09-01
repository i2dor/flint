using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Every store on the server, already loaded, for the configuration sweep.
/// </summary>
/// <remarks>
/// A seam so <see cref="SparkLightningConfigSweeper"/> can be tested without a database: the sweep must see
/// every store — including the ones with no Spark settings, which is exactly the store a hijacked Lightning
/// configuration lives on — so the enumeration has to come from BTCPay's whole store table rather than from
/// this plugin's settings bucket. The rows come back whole rather than as ids because the sweep parses each
/// store's Lightning configuration out of columns the row already carries; an id list would make it refetch
/// every row one store at a time.
/// </remarks>
public interface ISparkStoreSource
{
    Task<StoreData[]> GetStoresAsync(CancellationToken cancellationToken = default);
}
