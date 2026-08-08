using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>Why an address from the store's on-chain wallet is or is not available.</summary>
public enum SweepAddressStatus
{
    /// <summary>An address was produced.</summary>
    Available,

    /// <summary>
    /// The store has no BTC derivation scheme, so there is nowhere in BTCPay to sweep to. The merchant must
    /// either add an on-chain wallet or switch to a static address.
    /// </summary>
    NoOnchainWallet,

    /// <summary>
    /// The store has a wallet but an address could not be obtained — NBXplorer unreachable, the store deleted
    /// mid-sweep, the chain not supported by this server.
    /// </summary>
    Unavailable
}

/// <param name="Address">The address, set exactly when <paramref name="Status"/> is
/// <see cref="SweepAddressStatus.Available"/>.</param>
/// <param name="Reason">Merchant-facing explanation when it is not.</param>
public sealed record SweepAddressResult(string? Address, SweepAddressStatus Status, string? Reason)
{
    public static SweepAddressResult Available(string address) =>
        new(address, SweepAddressStatus.Available, null);

    public static SweepAddressResult NoWallet(string reason) =>
        new(null, SweepAddressStatus.NoOnchainWallet, reason);

    public static SweepAddressResult Unavailable(string reason) =>
        new(null, SweepAddressStatus.Unavailable, reason);
}

/// <summary>
/// Produces on-chain addresses from a store's own BTCPay wallet, for sweeps to land in.
/// </summary>
/// <remarks>
/// A seam over BTCPay's wallet and NBXplorer, so the sweep engine and the destination rules above it are
/// testable without a running NBXplorer. The production implementation is
/// <see cref="BTCPayWalletSweepAddressSource"/>.
/// </remarks>
public interface ISweepAddressSource
{
    /// <summary>
    /// An address from the store's BTC derivation scheme.
    /// </summary>
    /// <param name="reserve">
    /// True to <em>reserve and label</em> the address, which is what a real sweep does: the address is marked
    /// used so the next sweep gets a different one, and it is tagged in the store's wallet so the incoming
    /// transaction is recognisable as a Spark sweep rather than an anonymous deposit. This is the proven Boltz
    /// pattern.
    /// <para>
    /// False peeks at the next unused address without consuming it, for obtaining a fee quote to show a
    /// merchant. That distinction exists because quoting requires <em>an</em> address of the right network and
    /// script type but not the final one, and reserving on every page view would burn a merchant's address gap
    /// for nothing. The address a subsequent reserve returns may differ; the exit fee does not depend on the
    /// address, and both come from the same account and derivation feature so the script type — and therefore
    /// the dust floor — is the same.
    /// </para>
    /// </param>
    Task<SweepAddressResult> GetAddressAsync(
        string storeId,
        bool reserve,
        CancellationToken cancellationToken = default);
}
