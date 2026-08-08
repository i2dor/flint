using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Why a store's BTCPay hot-wallet seed is or is not usable as this store's Spark seed.
/// </summary>
public enum HotWalletSeedStatus
{
    /// <summary>The seed was read and can be reused.</summary>
    Available,

    /// <summary>
    /// The store has no BTC wallet, or its wallet is watch-only/cold so BTCPay holds no keys for it.
    /// </summary>
    /// <remarks>
    /// These two cases are deliberately one status: core's <c>HotwalletSafe.TryUnlock</c> returns null for
    /// both (and for "no such store"), with no way to tell them apart. The UI steers to "generate new"
    /// either way, so nothing is lost.
    /// </remarks>
    NotAHotWallet,

    /// <summary>
    /// The store's wallet is a hot wallet, but NBXplorer holds no mnemonic for it — it was imported from an
    /// extended private key rather than generated from a recovery phrase.
    /// </summary>
    NoSeedStored,

    /// <summary>
    /// The seed could not be looked up at all: BTCPay's hot-wallet service is missing or it failed. Seed
    /// reuse is simply not offered; every other seed source still works.
    /// </summary>
    Unavailable
}

/// <summary>
/// Outcome of a hot-wallet seed lookup. <see cref="Mnemonic"/> is set only when
/// <see cref="Status"/> is <see cref="HotWalletSeedStatus.Available"/>.
/// </summary>
/// <param name="Reason">
/// Merchant-facing explanation of a non-available status, for the setup page. Null when available.
/// </param>
public sealed record HotWalletSeedResult(HotWalletSeedStatus Status, string? Mnemonic, string? Reason)
{
    public bool IsAvailable => Status is HotWalletSeedStatus.Available && Mnemonic is not null;

    public static HotWalletSeedResult Found(string mnemonic) =>
        new(HotWalletSeedStatus.Available, mnemonic, null);

    public static HotWalletSeedResult NotAvailable(HotWalletSeedStatus status, string reason) =>
        new(status, null, reason);
}

/// <summary>
/// Reads the BIP39 mnemonic of a store's BTCPay <c>BTC</c> hot wallet, if there is one.
/// </summary>
/// <remarks>
/// A seam over BTCPay core's <c>HotwalletSafe</c>. Wrapped rather than injected directly for two reasons:
/// the setup flow has to be testable without NBXplorer and a store, and the plugin should degrade to
/// "seed reuse unavailable" rather than fail to load if a future core release moves or removes that type.
/// </remarks>
public interface IHotWalletSeedReader
{
    /// <param name="user">
    /// The signed-in principal. Core authorises the read against this principal, so it must be the real
    /// caller and not a service identity.
    /// </param>
    Task<HotWalletSeedResult> ReadAsync(
        ClaimsPrincipal user,
        string storeId,
        CancellationToken cancellationToken = default);
}
