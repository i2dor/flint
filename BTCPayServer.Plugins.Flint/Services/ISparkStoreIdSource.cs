using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Every store id on the server, for the startup configuration sweep.
/// </summary>
/// <remarks>
/// A seam so <see cref="SparkLightningConfigSweeper"/> can be tested without a database: the sweep must see
/// every store — including the ones with no Spark settings, which is exactly the store a hijacked Lightning
/// configuration lives on — so the enumeration has to come from BTCPay's whole store table rather than from
/// this plugin's settings bucket.
/// </remarks>
public interface ISparkStoreIdSource
{
    Task<string[]> GetStoreIdsAsync(CancellationToken cancellationToken = default);
}
