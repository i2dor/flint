using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The real <see cref="IStoreLightningConfigStore"/>, over BTCPay's store repository.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors what core's <c>UIStoresController.SetupLightningNode</c> does on save, including enabling LNURL
/// with the same three flags, so a store provisioned by this plugin is indistinguishable from one a merchant
/// configured by hand — Lightning Address and LNURL-pay then work through core with no further wiring.
/// </para>
/// <para><b>Why <see cref="PaymentMethodHandlerDictionary"/> arrives as a factory.</b></para>
/// <para>
/// Constructing that dictionary enumerates <c>IEnumerable&lt;IPaymentMethodHandler&gt;</c>, which builds
/// core's <c>LightningLikePaymentHandler</c>, which takes <c>LightningClientFactoryService</c>, which takes
/// <c>IEnumerable&lt;ILightningConnectionStringHandler&gt;</c> — a list this plugin contributes to. Injecting
/// the dictionary here therefore put a cycle in the container's object graph, and because two of its edges run
/// through factory delegates the container cannot see it at graph-build time: instead of reporting a circular
/// dependency it recursed until <c>StackGuard</c> moved resolution to another thread and deadlocked BTCPay's
/// startup outright. Resolving it on first use keeps this class off that graph entirely — every call site is an
/// async method that only ever runs long after the container is built.
/// </para>
/// </remarks>
public sealed class BTCPayStoreLightningConfigStore : IStoreLightningConfigStore
{
    /// <summary>Spark is Bitcoin-only, and so is this plugin's Lightning payment method.</summary>
    private const string CryptoCode = "BTC";

    private readonly StoreRepository _storeRepository;
    private readonly Func<PaymentMethodHandlerDictionary> _handlers;

    /// <param name="handlers">
    /// Resolved on first use rather than injected, and that is load-bearing: see the remarks on this class.
    /// </param>
    public BTCPayStoreLightningConfigStore(
        StoreRepository storeRepository,
        Func<PaymentMethodHandlerDictionary> handlers)
    {
        _storeRepository = storeRepository;
        _handlers = handlers;
    }

    /// <summary>
    /// The two payment methods this plugin owns. Internal so a test can pin their identifiers.
    /// </summary>
    /// <remarks>
    /// Worth pinning: everything else in the wiring is exercised against a fake of
    /// <see cref="IStoreLightningConfigStore"/>, so these two ids are the one part of the path that no unit test
    /// reaches. If either drifted from what BTCPay's own Lightning handler uses, the plugin would write a
    /// configuration nothing reads and report success.
    /// </remarks>
    internal static PaymentMethodId LightningId => PaymentTypes.LN.GetPaymentMethodId(CryptoCode);

    /// <inheritdoc cref="LightningId" />
    internal static PaymentMethodId LnurlId => PaymentTypes.LNURL.GetPaymentMethodId(CryptoCode);

    /// <inheritdoc />
    public async Task<StoreLightningConfig?> GetAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
            return null;

        var config = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(LightningId, _handlers());
        if (config is null)
            return new StoreLightningConfig(false, null, false);

        var enabled = !store.GetStoreBlob().IsExcluded(LightningId);
        return new StoreLightningConfig(config.IsInternalNode, config.ConnectionString, enabled);
    }

    /// <inheritdoc />
    public async Task<bool> SetAsync(
        string storeId,
        string? connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
            return false;

        if (connectionString is null)
        {
            // Null config removes the payment method outright rather than leaving an empty one behind.
            store.SetPaymentMethodConfig(LightningId, null);
            store.SetPaymentMethodConfig(LnurlId, null);
        }
        else
        {
            store.SetPaymentMethodConfig(
                _handlers()[LightningId],
                new LightningPaymentMethodConfig { ConnectionString = connectionString });

            // Same values core writes on a manual save. Bech32 for wallet compatibility, LUD-12 comments off
            // (the SDK cannot carry them), LUD-21 verify on.
            store.SetPaymentMethodConfig(
                _handlers()[LnurlId],
                new LNURLPaymentMethodConfig
                {
                    UseBech32Scheme = true,
                    LUD12Enabled = false,
                    LUD21Enabled = true
                });

            // A merchant who previously turned Lightning off would otherwise finish setup with a working
            // wallet that silently takes no payments.
            var blob = store.GetStoreBlob();
            blob.SetExcluded(LightningId, false);
            blob.SetExcluded(LnurlId, false);
            store.SetStoreBlob(blob);
        }

        await _storeRepository.UpdateStore(store).ConfigureAwait(false);
        return true;
    }
}
