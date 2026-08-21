using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Everything a surface needs to show about funding a store's Spark wallet on-chain.
/// </summary>
/// <param name="Configured">False when the store has not set Spark up.</param>
/// <param name="Address">
/// The wallet's static Bitcoin deposit address, or null when it could not be read. Stable across calls, so it
/// is safe for a merchant to save.
/// </param>
/// <param name="AddressError">Why the address could not be read, when it could not.</param>
/// <param name="Deposits">
/// Everything the SDK has seen and not yet credited. Both immature deposits, which need nobody, and matured
/// ones whose claim failed, which need an operator.
/// </param>
public sealed record SparkDepositView(
    bool Configured,
    bool WalletRunning,
    string? Address,
    string? AddressError,
    IReadOnlyList<SparkDepositInfo> Deposits,
    SparkRecommendedFees? RecommendedFees,
    SparkDepositSettings Settings)
{
    public static SparkDepositView NotConfigured() =>
        new(false, false, null, null, [], null, new SparkDepositSettings());

    /// <summary>Deposits that will never arrive unless somebody acts.</summary>
    public IReadOnlyList<SparkDepositInfo> Stuck =>
        Deposits.Where(deposit => deposit.NeedsAttention).ToList();

    /// <summary>Deposits simply waiting for their third confirmation.</summary>
    public IReadOnlyList<SparkDepositInfo> Maturing =>
        Deposits.Where(deposit => !deposit.IsMature).ToList();

    /// <summary>
    /// True when the configured claim ceiling is already below what the mempool is asking.
    /// </summary>
    /// <remarks>
    /// A forward-looking warning rather than a report of damage: it is true <em>before</em> a deposit gets
    /// stranded, which is the only useful time to say it. Compared against the half-hour rate rather than the
    /// fastest, because that is the tier a claim realistically needs to clear.
    /// </remarks>
    public bool ClaimPolicyLooksTooLow =>
        RecommendedFees is { } fees &&
        Settings.EffectiveClaimFeeLeewaySatPerVbyte + fees.EconomyFeeSatPerVbyte < fees.HalfHourFeeSatPerVbyte;
}

/// <summary>What happened to a manual claim.</summary>
public enum SparkClaimStatus
{
    Claimed,

    /// <summary>The store has not set Spark up, or its wallet is not running.</summary>
    Unavailable,

    /// <summary>The plugin declined, and <b>nothing was broadcast</b>. Always has a reason.</summary>
    Refused,

    /// <summary>The claim was attempted and the SDK reported a failure. Nothing was spent.</summary>
    Failed
}

/// <param name="FeeSats">The ceiling the claim was actually issued at, when one was.</param>
public sealed record SparkClaimOutcome(SparkClaimStatus Status, string Message, long? FeeSats = null)
{
    public bool Succeeded => Status is SparkClaimStatus.Claimed;
}

/// <summary>
/// The single path by which a store's on-chain deposit state is read and a stuck deposit is claimed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reason this feature exists at all is that the SDK's default silently loses money.</b>
/// <c>maxDepositClaimFee</c> defaults to <c>Rate(1 sat/vB)</c> and is a cap rather than a bid, so on mainnet —
/// where even a cheap market runs at 2–3 sat/vB — a deposit is simply never claimed. It does not error to the
/// merchant, it does not appear in the balance, and the only trace is a row in <c>ListUnclaimedDeposits</c>
/// that nothing was reading. From the merchant's side, they sent Bitcoin to an address the plugin gave them and
/// it vanished.
/// </para>
/// <para>
/// So the policy has three parts and this class holds two of them: the configured ceiling
/// (<see cref="SparkDepositSettings"/>, applied at connect), the <b>dashboard</b> that makes a stranded deposit
/// visible with the fee it actually needed, and the <b>one-click claim</b> at that fee. Any one of the three
/// alone leaves the merchant stuck.
/// </para>
/// <para>
/// Shared by the status page and the Greenfield endpoint, on the same principle as
/// <see cref="SparkStoreStatusReader"/>: two surfaces each deciding what a safe claim ceiling is would be two
/// answers to a question with one right answer.
/// </para>
/// </remarks>
public sealed class SparkDepositService
{
    private readonly ISparkStoreSettingsStore _settingsStore;
    private readonly ISparkStoreRuntime _runtime;
    private readonly ILogger<SparkDepositService> _logger;

    /// <summary>
    /// Deposit addresses already fetched, keyed by store and by the wallet identity that produced them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The address is static — the SDK returns the existing one rather than minting — but reading it is a live
    /// service-provider round trip, and the status page would make one on every render.
    /// </para>
    /// <para>
    /// Keyed on the identity pubkey as well as the store id, which is what makes the cache correct rather than
    /// merely fast: replacing a store's seed gives it a different wallet and therefore a different deposit
    /// address, and a cache keyed on the store alone would go on showing the old wallet's address — sending a
    /// merchant's top-up to a wallet the plugin no longer has the seed for.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<(string StoreId, string Identity), string> _addresses = new();

    public SparkDepositService(
        ISparkStoreSettingsStore settingsStore,
        ISparkStoreRuntime runtime,
        ILogger<SparkDepositService> logger)
    {
        _settingsStore = settingsStore;
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>
    /// Reads the store's deposit address, its unclaimed deposits and the current fee market.
    /// </summary>
    /// <remarks>
    /// Every read degrades independently. A deposit address that cannot be fetched must not stop the unclaimed
    /// list from rendering, because that list is the thing a merchant with a missing deposit came to look at.
    /// </remarks>
    public async Task<SparkDepositView> ReadAsync(string storeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
            return SparkDepositView.NotConfigured();

        var deposits = settings.Deposits ?? new SparkDepositSettings();

        var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
        {
            return new SparkDepositView(
                Configured: true, WalletRunning: false, null,
                "This store's Spark wallet is not running, so its deposit address cannot be read.",
                [], null, deposits);
        }

        string? address = null;
        string? addressError = null;
        try
        {
            address = await GetAddressAsync(storeId, sdk, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: could not read its Spark deposit address ({Reason})",
                storeId, SparkErrors.Describe(ex));
            addressError = SparkErrors.Describe(ex);
        }

        IReadOnlyList<SparkDepositInfo> unclaimed = [];
        try
        {
            // A local storage read, so this works even when the service provider does not — which matters,
            // because "the provider is unreachable" and "my deposit never arrived" are exactly the two things a
            // merchant is trying to tell apart.
            unclaimed = await sdk.ListUnclaimedDepositsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: could not list its unclaimed Spark deposits ({Reason})",
                storeId, SparkErrors.Describe(ex));
        }

        SparkRecommendedFees? fees = null;
        try
        {
            fees = await sdk.GetRecommendedFeesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Least important of the three: it only decorates the claim policy with the market it is being
            // judged against.
            _logger.LogDebug(ex, "Store {StoreId}: could not read recommended fees", storeId);
        }

        return new SparkDepositView(true, true, address, addressError, unclaimed, fees, deposits);
    }

    /// <summary>
    /// Claims one deposit by hand, at a ceiling the store's policy allows.
    /// </summary>
    /// <param name="requestedFeeSats">
    /// The ceiling to claim at, or null to use the fee the SDK said the claim actually needs. Null is the
    /// one-click case and is the right answer almost always: the SDK reports <c>requiredFeeSats</c> on the very
    /// error that stranded the deposit.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Three guards, and none of them is the SDK's.</b> The SDK will spend whatever ceiling it is given, and
    /// the ceiling is paid out of a deposit whose size the SDK's own configuration knows nothing about. So:
    /// </para>
    /// <list type="number">
    /// <item><description>The fee may not exceed the store's configured
    /// <see cref="SparkDepositSettings.MaxManualClaimFeeSats"/>.</description></item>
    /// <item><description>Whatever that is set to, the fee may not exceed
    /// <see cref="SparkDepositSettings.HardMaxClaimFeePercent"/> of the deposit — a backstop no configuration
    /// can lift, mirroring the sweep engine's. There is no setting under which spending most of a deposit to
    /// receive the rest is what a merchant meant. <b>It binds here and nowhere else:</b> an automatic claim is
    /// the SDK's own background worker and is bounded by rate alone, because the SDK's config has no
    /// amount-relative cap to configure. See <c>SparkDepositSettings</c>' remarks for what that leaves
    /// exposed.</description></item>
    /// <item><description>The deposit has to exist and be matured. Claiming an immature one is a request the
    /// SDK will refuse, and the honest answer is that it is not stuck, it is
    /// waiting.</description></item>
    /// </list>
    /// </remarks>
    public async Task<SparkClaimOutcome> ClaimAsync(
        string storeId,
        string txId,
        uint vout,
        long? requestedFeeSats,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        if (string.IsNullOrWhiteSpace(txId))
            return new SparkClaimOutcome(SparkClaimStatus.Refused, "No deposit was named.");

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
            return new SparkClaimOutcome(SparkClaimStatus.Unavailable, "Flint is not set up for this store.");

        var policy = settings.Deposits ?? new SparkDepositSettings();

        var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
        {
            return new SparkClaimOutcome(
                SparkClaimStatus.Unavailable,
                "This store's Spark wallet is not running, so nothing can be claimed.");
        }

        IReadOnlyList<SparkDepositInfo> unclaimed;
        try
        {
            unclaimed = await sdk.ListUnclaimedDepositsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new SparkClaimOutcome(
                SparkClaimStatus.Failed,
                $"This store's unclaimed deposits could not be read: {SparkErrors.Describe(ex)}");
        }

        var deposit = unclaimed.FirstOrDefault(
            candidate => string.Equals(candidate.TxId, txId, StringComparison.OrdinalIgnoreCase)
                         && candidate.Vout == vout);

        if (deposit is null)
        {
            // Also the success-after-the-fact case: a deposit the SDK claimed on its own between the page render
            // and the button press is no longer in this list.
            return new SparkClaimOutcome(
                SparkClaimStatus.Refused,
                "Spark has no unclaimed deposit at that output. It may already have been claimed — check the "
                + "balance.");
        }

        if (!deposit.IsMature)
        {
            return new SparkClaimOutcome(
                SparkClaimStatus.Refused,
                "This deposit has not matured yet. Spark claims a deposit automatically once it has three "
                + "confirmations; there is nothing to fix.");
        }

        var feeSats = requestedFeeSats ?? deposit.ClaimError?.RequiredFeeSats;
        if (feeSats is not { } fee || fee <= 0)
        {
            return new SparkClaimOutcome(
                SparkClaimStatus.Refused,
                "Spark did not say what this claim would cost, so there is no fee to authorise. Enter one "
                + "explicitly, at most "
                + string.Format(CultureInfo.InvariantCulture, "{0:N0}", policy.EffectiveMaxManualClaimFeeSats)
                + " sat.");
        }

        if (Guard(fee, deposit, policy) is { } refusal)
            return new SparkClaimOutcome(SparkClaimStatus.Refused, refusal);

        _logger.LogInformation(
            "Store {StoreId}: claiming deposit {OutPoint} of {AmountSats} sat at a {FeeSats} sat ceiling",
            storeId, deposit.OutPoint, deposit.AmountSats, fee);

        var result = await sdk
            .ClaimDepositAsync(deposit.TxId, deposit.Vout, new SparkMaxFee.Fixed(fee), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
            return new SparkClaimOutcome(SparkClaimStatus.Failed, result.Error ?? "The claim failed.", fee);

        return new SparkClaimOutcome(
            SparkClaimStatus.Claimed,
            string.Format(
                CultureInfo.InvariantCulture,
                "Claimed {0:N0} sat from {1} for a fee of up to {2:N0} sat. It appears in the balance once it "
                + "settles.",
                deposit.AmountSats, deposit.OutPoint, fee),
            fee);
    }

    /// <summary>The reason a claim fee is refused, or null to proceed.</summary>
    internal static string? Guard(long feeSats, SparkDepositInfo deposit, SparkDepositSettings policy)
    {
        ArgumentNullException.ThrowIfNull(deposit);
        ArgumentNullException.ThrowIfNull(policy);

        if (feeSats <= 0)
            return "A claim fee has to be a positive number of satoshi.";

        var ceiling = policy.EffectiveMaxManualClaimFeeSats;
        if (feeSats > ceiling)
        {
            // No control is named, because there is none: the deposit policy has no write path on either
            // surface (see SparkDepositSettings' remarks), so telling a merchant to "raise the limit on the
            // deposit settings" sent them looking for a page that does not exist.
            return string.Format(
                CultureInfo.InvariantCulture,
                "Claiming this deposit needs up to {0:N0} sat, above the {1:N0} sat limit this store allows. "
                + "Wait for a cheaper fee market, or have a server administrator raise this store's claim "
                + "limit.",
                feeSats, ceiling);
        }

        // The backstop no configuration can lift. A rate-based ceiling knows nothing about the size of the
        // deposit it is spent out of, so this is the only place the two are ever compared.
        var hardCap = (long)Math.Floor(deposit.AmountSats * SparkDepositSettings.HardMaxClaimFeePercent / 100d);
        if (feeSats > hardCap)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Claiming this deposit would cost {0:N0} sat out of the {1:N0} sat it holds. The plugin will "
                + "not spend more than {2:0.#}% of a deposit to claim it, whatever the limits say.",
                feeSats, deposit.AmountSats, SparkDepositSettings.HardMaxClaimFeePercent);
        }

        return null;
    }

    private async Task<string> GetAddressAsync(
        string storeId,
        ISparkSdkClient sdk,
        CancellationToken cancellationToken)
    {
        // The identity is read from the cache rather than forced to sync: this runs on a request thread, and a
        // stale identity would only mean a cache miss, never a wrong address.
        var info = await sdk.GetInfoAsync(ensureSynced: false, cancellationToken).ConfigureAwait(false);

        // No identity, no cache — in either direction. An empty identity is not a wallet identity, it is the
        // absence of one, and using it as a cache key would make every wallet this store has ever had share
        // one slot: after a seed change, a request racing the new wallet's first sync would be handed the
        // previous wallet's deposit address. The SDK itself is always current for this store, so the fallback
        // is a fresh (idempotent) address read, not a guess.
        if (string.IsNullOrEmpty(info.IdentityPubkey))
            return await sdk.GetBitcoinDepositAddressAsync(cancellationToken).ConfigureAwait(false);

        var key = (storeId, info.IdentityPubkey);

        if (_addresses.TryGetValue(key, out var cached))
            return cached;

        var address = await sdk.GetBitcoinDepositAddressAsync(cancellationToken).ConfigureAwait(false);
        _addresses[key] = address;
        return address;
    }
}
