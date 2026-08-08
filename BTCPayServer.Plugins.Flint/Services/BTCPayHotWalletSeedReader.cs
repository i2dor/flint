using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Wallets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Reads a store's hot-wallet seed through BTCPay core's <c>HotwalletSafe</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>HotwalletSafe</c> is resolved per call from the service provider rather than injected, and every
/// failure — including its absence — degrades to <see cref="HotWalletSeedStatus.Unavailable"/>. It lives in
/// the <c>BTCPayServer</c> assembly as part of core's built-in <c>Wallets</c> plugin, registered transient by
/// <c>WalletsPlugin</c>. That is not a contract this plugin can rely on across core releases, and the failure
/// mode matters: injecting it would make the whole controller unresolvable if the registration ever moved,
/// taking down the status and removal pages too. Resolving it here costs one dictionary lookup and turns that
/// into one greyed-out setup option.
/// </para>
/// <para>
/// The mnemonic returned by core is the seed of the store's on-chain wallet. It is handed straight to the
/// provisioner and never logged, never put in a view model, and never written anywhere but the protected
/// settings blob.
/// </para>
/// </remarks>
public sealed class BTCPayHotWalletSeedReader : IHotWalletSeedReader
{
    /// <summary>
    /// The only chain this plugin reuses a seed from. Spark is Bitcoin-only, and so is the plugin's
    /// Lightning payment method.
    /// </summary>
    private const string CryptoCode = "BTC";

    private readonly IServiceProvider _services;
    private readonly ILogger<BTCPayHotWalletSeedReader> _logger;

    public BTCPayHotWalletSeedReader(IServiceProvider services, ILogger<BTCPayHotWalletSeedReader> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HotWalletSeedResult> ReadAsync(
        ClaimsPrincipal user,
        string storeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        HotwalletSafe? safe;
        try
        {
            safe = _services.GetService<HotwalletSafe>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not resolve BTCPay's hot-wallet service, so reusing the store's on-chain seed is not "
                + "being offered for store {StoreId}", storeId);
            return HotWalletSeedResult.NotAvailable(
                HotWalletSeedStatus.Unavailable,
                "This BTCPay Server does not expose its hot-wallet seeds to plugins.");
        }

        if (safe is null)
        {
            return HotWalletSeedResult.NotAvailable(
                HotWalletSeedStatus.Unavailable,
                "This BTCPay Server does not expose its hot-wallet seeds to plugins.");
        }

        HotwalletSafe.HotwalletRecord? record;
        try
        {
            record = await safe.TryUnlock(user, new WalletId(storeId, CryptoCode)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // NBXplorer being unreachable is the likely cause, and it is transient. Reported as unavailable
            // rather than as "not a hot wallet" so the merchant does not conclude their wallet is cold.
            _logger.LogWarning(ex,
                "Store {StoreId}: could not read the on-chain wallet seed. Reusing it is not being offered",
                storeId);
            return HotWalletSeedResult.NotAvailable(
                HotWalletSeedStatus.Unavailable,
                "The store's on-chain wallet could not be read just now. Try again, or generate a new seed.");
        }

        if (record is null)
        {
            return HotWalletSeedResult.NotAvailable(
                HotWalletSeedStatus.NotAHotWallet,
                "This store has no Bitcoin hot wallet. Watch-only and hardware wallets keep no keys on the "
                + "server, so there is no seed here to reuse.");
        }

        if (!record.CanSee)
        {
            // Belt and braces: the actions that reach this code already require CanModifyStoreSettings, and
            // this is the same check core makes before revealing a seed.
            return HotWalletSeedResult.NotAvailable(
                HotWalletSeedStatus.Unavailable,
                "You do not have permission to use this store's on-chain wallet seed.");
        }

        if (string.IsNullOrWhiteSpace(record.Mnemonic))
        {
            return HotWalletSeedResult.NotAvailable(
                HotWalletSeedStatus.NoSeedStored,
                "This store's hot wallet was imported from an extended private key, so BTCPay holds no "
                + "recovery phrase for it.");
        }

        return HotWalletSeedResult.Found(record.Mnemonic);
    }
}
