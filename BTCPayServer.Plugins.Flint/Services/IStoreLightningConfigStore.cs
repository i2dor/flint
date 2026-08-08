using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// A store's <c>BTC-LN</c> payment-method configuration, as far as this plugin cares about it.
/// </summary>
/// <param name="IsInternalNode">
/// True when the store is pointed at BTCPay's own internal Lightning node. Mutually exclusive with
/// <paramref name="ConnectionString"/> being set.
/// </param>
/// <param name="ConnectionString">
/// The external Lightning connection string, or null when there is none.
/// </param>
/// <param name="Enabled">
/// False when Lightning is configured but excluded from checkout in the store's blob. Provisioning
/// un-excludes it; the status page reports it so a merchant is not left wondering why a configured wallet
/// takes no payments.
/// </param>
public sealed record StoreLightningConfig(bool IsInternalNode, string? ConnectionString, bool Enabled);

/// <summary>
/// Narrow read/write access to one store's Lightning payment-method configuration.
/// </summary>
/// <remarks>
/// Deliberately the smallest possible seam over <c>StoreRepository</c> and
/// <c>PaymentMethodHandlerDictionary</c>: it moves a connection string in and out and nothing else, so all of
/// the plugin's decisions about <em>when</em> to write one live in <see cref="SparkLightningWiring"/>, which is
/// unit-testable against a fake of this interface.
/// </remarks>
public interface IStoreLightningConfigStore
{
    /// <summary>
    /// The store's current Lightning configuration, or null when there is no such store.
    /// </summary>
    Task<StoreLightningConfig?> GetAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the store's Lightning configuration, or removes it when
    /// <paramref name="connectionString"/> is null. Returns false when there is no such store.
    /// </summary>
    /// <remarks>
    /// Writing also enables the store's LNURL payment method, and un-excludes both from checkout; removing
    /// takes LNURL away with Lightning, because an LNURL payment method with no Lightning behind it fails at
    /// checkout rather than being ignored.
    /// </remarks>
    Task<bool> SetAsync(
        string storeId,
        string? connectionString,
        CancellationToken cancellationToken = default);
}
