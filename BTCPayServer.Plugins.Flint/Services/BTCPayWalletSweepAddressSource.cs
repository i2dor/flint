using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Logging;
using NBXplorer.DerivationStrategy;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The real <see cref="ISweepAddressSource"/>, over the store's BTCPay wallet and NBXplorer.
/// </summary>
/// <remarks>
/// <para>
/// The reserving path is exactly what the Boltz plugin does for its own sweeps —
/// <c>BTCPayWallet.ReserveAddressAsync(storeId, derivation.AccountDerivation, label)</c> — which also writes a
/// wallet object so the address shows up labelled in the store's wallet. Rotation is a consequence of reserving
/// rather than a separate step: a reserved address is marked used, so the next sweep is handed the next one.
/// </para>
/// <para>
/// The peeking path deliberately goes to the explorer client directly. <c>BTCPayWallet</c> exposes no
/// non-reserving deposit-address read, and quoting a sweep for a merchant to look at must not consume an
/// address.
/// </para>
/// </remarks>
public sealed class BTCPayWalletSweepAddressSource : ISweepAddressSource
{
    /// <summary>Spark is Bitcoin-only, and so is every sweep destination in this wave.</summary>
    private const string CryptoCode = "BTC";

    /// <summary>
    /// The label written against a reserved address, so a merchant reading their wallet can tell where the
    /// money came from.
    /// </summary>
    internal const string AddressLabel = "Spark sweep";

    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly BTCPayWalletProvider _walletProvider;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly ILogger<BTCPayWalletSweepAddressSource> _logger;

    public BTCPayWalletSweepAddressSource(
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        BTCPayWalletProvider walletProvider,
        ExplorerClientProvider explorerClientProvider,
        ILogger<BTCPayWalletSweepAddressSource> logger)
    {
        _storeRepository = storeRepository;
        _handlers = handlers;
        _walletProvider = walletProvider;
        _explorerClientProvider = explorerClientProvider;
        _logger = logger;
    }

    public async Task<SweepAddressResult> GetAddressAsync(
        string storeId,
        bool reserve,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
        {
            return SweepAddressResult.Unavailable(
                "This store could not be read, so no sweep destination could be derived.");
        }

        // onlyEnabled is deliberately false. A merchant who has excluded on-chain payments from checkout still
        // has a wallet, and refusing to sweep into it because customers cannot pay on-chain would be a
        // non-sequitur that leaves funds stranded on Spark.
        var derivation = store.GetDerivationSchemeSettings(_handlers, CryptoCode);
        if (derivation?.AccountDerivation is not { } accountDerivation)
        {
            return SweepAddressResult.NoWallet(
                "This store has no Bitcoin wallet, so there is no address to sweep to. Either set up the "
                + "store's on-chain wallet, or configure a fixed sweep address instead.");
        }

        try
        {
            var address = reserve
                ? await ReserveAsync(storeId, accountDerivation).ConfigureAwait(false)
                : await PeekAsync(accountDerivation, cancellationToken).ConfigureAwait(false);

            return address is null
                ? SweepAddressResult.Unavailable(
                    "This store's Bitcoin wallet did not return an address. Check that NBXplorer is reachable "
                    + "and the wallet is being tracked.")
                : SweepAddressResult.Available(address);
        }
        catch (Exception ex)
        {
            // Not fatal to the sweep pass: the engine turns this into a refusal, and refusing is always safe.
            _logger.LogWarning(ex,
                "Store {StoreId}: could not obtain a sweep address from its Bitcoin wallet (reserve={Reserve})",
                storeId, reserve);
            return SweepAddressResult.Unavailable(
                $"This store's Bitcoin wallet could not provide an address: {SparkErrors.Describe(ex)}");
        }
    }

    private async Task<string?> ReserveAsync(string storeId, DerivationStrategyBase accountDerivation)
    {
        var wallet = _walletProvider.GetWallet(CryptoCode);
        if (wallet is null)
            return null;

        var pathInfo = await wallet
            .ReserveAddressAsync(storeId, accountDerivation, AddressLabel)
            .ConfigureAwait(false);
        return pathInfo?.Address?.ToString();
    }

    private async Task<string?> PeekAsync(
        DerivationStrategyBase accountDerivation,
        CancellationToken cancellationToken)
    {
        var explorerClient = _explorerClientProvider.GetExplorerClient(CryptoCode);
        if (explorerClient is null)
            return null;

        // Same account and derivation feature as the reserving path, so the script type — and therefore the
        // SDK's script-type-dependent dust floor — matches what the real sweep will use.
        var pathInfo = await explorerClient
            .GetUnusedAsync(
                accountDerivation,
                NBXplorer.DerivationStrategy.DerivationFeature.Deposit,
                skip: 0,
                reserve: false,
                cancellation: cancellationToken)
            .ConfigureAwait(false);
        return pathInfo?.Address?.ToString();
    }
}
