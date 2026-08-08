using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Everything a surface can say about a store's Spark wallet without holding a secret.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no seed on this type, and there must never be one.</b> The mnemonic is stored protected and is
/// unwrapped only inside <c>SparkService</c>; nothing that reports status reads it back. The identity pubkey is
/// public by design, and <see cref="StorageDirectory"/> is a path, not a key.
/// </para>
/// <para>
/// <see cref="BalanceSats"/> is indicative. It lagged settlement by ~20 s in the funded regtest run and drifts by
/// a few sats around the SDK's background leaf optimisation, and it is read from
/// the SDK's cache rather than forcing a sync, because this runs on a request thread. Nothing may derive an
/// accounting figure from it.
/// </para>
/// </remarks>
/// <param name="Configured">False when this store has never set Spark up. Everything else is then meaningless.</param>
/// <param name="WalletRunning">
/// False when the settings exist but no SDK instance is live — a seed this server can no longer decrypt, a second
/// store on the same wallet, an unsupported chain.
/// </param>
/// <param name="WalletError">Set when the wallet is running but could not be read.</param>
public sealed record SparkStoreStatus(
    bool Configured,
    SeedSource SeedSource,
    bool WalletRunning,
    string? IdentityPubkey,
    long? BalanceSats,
    string? WalletError,
    SparkNetworkStatus? NetworkStatus,
    SparkLightningWiringState LightningWiring,
    bool LightningEnabledForCheckout,
    string StorageDirectory)
{
    /// <summary>
    /// <see cref="StorageDirectory"/> as <paramref name="user"/> is allowed to see it: the path for a server
    /// admin, and null for everyone else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One implementation for both surfaces, deliberately. The status page and the Greenfield status endpoint
    /// each project this record, and a second copy of the rule is how one of them keeps disclosing the path
    /// after the other stops.
    /// </para>
    /// <para>
    /// Both surfaces are reachable with <c>CanViewStoreSettings</c>, which every store role above the lowest
    /// holds. The absolute path is a fact about the host's filesystem layout rather than about the store —
    /// where the data directory lives, and by inference how the server is deployed — and a store manager is
    /// not a server operator. Minor as disclosures go, and free to remove.
    /// </para>
    /// </remarks>
    public string? StorageDirectoryFor(ClaimsPrincipal? user) =>
        user?.IsInRole(Roles.ServerAdmin) is true ? StorageDirectory : null;

    public static SparkStoreStatus NotConfigured(string storageDirectory) => new(
        Configured: false,
        SeedSource: SeedSource.Generated,
        WalletRunning: false,
        IdentityPubkey: null,
        BalanceSats: null,
        WalletError: null,
        NetworkStatus: null,
        LightningWiring: SparkLightningWiringState.NotConfigured,
        LightningEnabledForCheckout: false,
        StorageDirectory: storageDirectory);
}

/// <summary>
/// Assembles a store's Spark status from the settings, the live SDK instance, the Spark network and the store's
/// Lightning wiring.
/// </summary>
/// <remarks>
/// Extracted from <c>SparkController.Status</c> so the status page and the Greenfield status endpoint report the
/// same facts, gathered in the same order, with the same failure handling. Two surfaces each doing this by hand
/// is how one of them ends up forcing a sync on a request thread, or reporting a running wallet for a store whose
/// settings were removed a moment ago.
/// </remarks>
public sealed class SparkStoreStatusReader
{
    private readonly ISparkStoreSettingsStore _settingsStore;
    private readonly ISparkStoreRuntime _runtime;
    private readonly ISparkNetworkStatusProbe _networkStatusProbe;
    private readonly SparkLightningWiring _lightningWiring;
    private readonly ILogger<SparkStoreStatusReader> _logger;

    public SparkStoreStatusReader(
        ISparkStoreSettingsStore settingsStore,
        ISparkStoreRuntime runtime,
        ISparkNetworkStatusProbe networkStatusProbe,
        SparkLightningWiring lightningWiring,
        ILogger<SparkStoreStatusReader> logger)
    {
        _settingsStore = settingsStore;
        _runtime = runtime;
        _networkStatusProbe = networkStatusProbe;
        _lightningWiring = lightningWiring;
        _logger = logger;
    }

    /// <summary>
    /// Reads the store's status. Returns <see cref="SparkStoreStatus.Configured"/> false — rather than throwing or
    /// returning null — for a store that has not set Spark up, because "not configured" is a status a caller asks
    /// about rather than an error.
    /// </summary>
    public async Task<SparkStoreStatus> ReadAsync(string storeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var storageDirectory = _runtime.GetStorageDirectory(storeId);

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
            return SparkStoreStatus.NotConfigured(storageDirectory);

        string? identityPubkey = null;
        long? balanceSats = null;
        string? walletError = null;

        var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is not null)
        {
            try
            {
                // Cached read (ensureSynced: false): this is a request thread, a forced sync costs seconds, and
                // the balance is indicative regardless.
                var info = await sdk.GetInfoAsync(ensureSynced: false, cancellationToken).ConfigureAwait(false);
                identityPubkey = info.IdentityPubkey;
                balanceSats = info.BalanceSats;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Store {StoreId}: could not read its Spark wallet for a status report",
                    storeId);
                walletError = SparkErrors.Describe(ex);
            }
        }

        var networkStatus = await _networkStatusProbe.TryGetAsync(cancellationToken).ConfigureAwait(false);

        var wiring = await _lightningWiring
            .InspectAsync(storeId, settings.PaymentKey, cancellationToken)
            .ConfigureAwait(false);

        return new SparkStoreStatus(
            Configured: true,
            SeedSource: settings.SeedSource,
            WalletRunning: sdk is not null,
            IdentityPubkey: identityPubkey,
            BalanceSats: balanceSats,
            WalletError: walletError,
            NetworkStatus: networkStatus,
            LightningWiring: wiring.State,
            LightningEnabledForCheckout: wiring.EnabledForCheckout,
            StorageDirectory: storageDirectory);
    }
}
