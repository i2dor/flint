using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// How a store's Lightning payment method relates to this plugin.
/// </summary>
public enum SparkLightningWiringState
{
    /// <summary>There is no such store.</summary>
    StoreNotFound,

    /// <summary>The store has no Lightning payment method at all.</summary>
    NotConfigured,

    /// <summary>The store points at BTCPay's internal Lightning node.</summary>
    InternalNode,

    /// <summary>The store points at some other Lightning node. This plugin must not touch it.</summary>
    OtherNode,

    /// <summary>The store points at this store's Spark wallet with its current payment key.</summary>
    Spark,

    /// <summary>
    /// The store points at a Spark wallet, but not with the payment key the settings now hold — a leftover
    /// from an earlier configuration. Still ours, and dead: checkout would fail on it.
    /// </summary>
    StaleSpark,

    /// <summary>
    /// The store points at <em>another</em> store's Spark wallet — the one configuration that is broken by
    /// definition, because there is no legitimate reason for one store's Lightning payment method to drive
    /// another store's wallet. It is refused at save time (<c>SparkLightningClient.Validate</c>) and cleared
    /// by <see cref="SparkLightningConfigSweeper"/> at startup; until then the status page reports it in red
    /// as the configuration to repair.
    /// </summary>
    OtherStoreSpark
}

/// <summary>
/// What the plugin knows about a store's Lightning wiring.
/// </summary>
/// <param name="EnabledForCheckout">
/// False when a Lightning payment method exists but is excluded from checkout, so the status page can say so.
/// Meaningless when <paramref name="State"/> is <see cref="SparkLightningWiringState.NotConfigured"/> or
/// <see cref="SparkLightningWiringState.StoreNotFound"/>.
/// </param>
public sealed record SparkLightningWiringReport(SparkLightningWiringState State, bool EnabledForCheckout);

/// <summary>
/// Decides when this plugin may write or clear a store's Lightning payment-method configuration.
/// </summary>
/// <remarks>
/// <para>
/// The plugin owns the store's <c>BTC-LN</c> configuration so a merchant never copies a connection string by
/// hand — a two-page setup was the design target, and a copy/paste step was not part of it. The corollary is that it must be scrupulous about ownership: a merchant who
/// configured an LND node and then experimented with Spark must get their LND configuration back untouched.
/// So writing is unconditional (the merchant just asked for it) but clearing happens only when the stored
/// connection string is demonstrably one of ours, for this store.
/// </para>
/// <para>
/// Ownership is decided by <em>parsing</em> the stored string rather than comparing it byte for byte, so a
/// merchant who retyped it with different spacing or casing still gets it cleaned up. The payment key is
/// deliberately not required to match: a Spark connection string for this store with a stale key is ours and
/// is already dead, and leaving it behind is what makes checkout fail with "not configured for this store"
/// instead of telling the merchant their wallet was removed.
/// </para>
/// </remarks>
public sealed class SparkLightningWiring
{
    private readonly IStoreLightningConfigStore _configStore;
    private readonly ILogger<SparkLightningWiring> _logger;

    public SparkLightningWiring(IStoreLightningConfigStore configStore, ILogger<SparkLightningWiring> logger)
    {
        _configStore = configStore;
        _logger = logger;
    }

    /// <summary>
    /// Classifies a store's Lightning configuration. <paramref name="paymentKey"/> is the key the store's
    /// Spark settings currently hold, or null when it has none.
    /// </summary>
    public async Task<SparkLightningWiringReport> InspectAsync(
        string storeId,
        string? paymentKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var config = await _configStore.GetAsync(storeId, cancellationToken).ConfigureAwait(false);
        return new SparkLightningWiringReport(
            Classify(storeId, paymentKey, config),
            config?.Enabled is true);
    }

    /// <summary>
    /// Points the store's Lightning payment method at its Spark wallet. Returns false when there is no such
    /// store.
    /// </summary>
    public async Task<bool> EnableAsync(
        string storeId,
        string paymentKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentException.ThrowIfNullOrEmpty(paymentKey);

        var connectionString = SparkConnectionString.Format(storeId, paymentKey);
        var written = await _configStore
            .SetAsync(storeId, connectionString, cancellationToken)
            .ConfigureAwait(false);

        if (written)
        {
            _logger.LogInformation(
                "Store {StoreId}: its Lightning payment method now uses this store's Spark wallet", storeId);
        }
        else
        {
            _logger.LogWarning(
                "Store {StoreId}: could not point the Lightning payment method at Spark because the store "
                + "no longer exists", storeId);
        }

        return written;
    }

    /// <summary>
    /// Removes the store's Lightning payment method, but only if it points at a Spark wallet belonging to
    /// this store. Returns true when something was cleared.
    /// </summary>
    public async Task<bool> ClearIfOursAsync(string storeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var config = await _configStore.GetAsync(storeId, cancellationToken).ConfigureAwait(false);

        // Classified with a null payment key on purpose: whether the key is current or stale, the answer to
        // "is this ours to clear" is the same, and this runs while the settings are being removed.
        var state = Classify(storeId, paymentKey: null, config);
        if (state is not (SparkLightningWiringState.Spark or SparkLightningWiringState.StaleSpark))
        {
            if (state is SparkLightningWiringState.OtherNode or SparkLightningWiringState.InternalNode
                or SparkLightningWiringState.OtherStoreSpark)
            {
                _logger.LogInformation(
                    "Store {StoreId}: leaving its Lightning payment method alone, it does not point at this "
                    + "store's Spark wallet", storeId);
            }

            return false;
        }

        var cleared = await _configStore.SetAsync(storeId, null, cancellationToken).ConfigureAwait(false);
        if (cleared)
        {
            _logger.LogInformation(
                "Store {StoreId}: removed the Lightning payment method that pointed at its Spark wallet",
                storeId);
        }

        return cleared;
    }

    /// <summary>
    /// Removes a store's Lightning payment method because it points at <em>another</em> store's Spark
    /// wallet. Returns the embedded id of the victim wallet, or null when there is nothing cross-store to
    /// clear.
    /// </summary>
    /// <remarks>
    /// The sweep's remediation, deliberately narrower than <see cref="ClearIfOursAsync"/>: only a valid
    /// Spark connection string embedding a <em>different</em> store id is cleared, so this can never touch
    /// an <c>OtherNode</c> configuration (a merchant's own Lightning node), the internal node, or a
    /// malformed string whose owner is unknown.
    /// </remarks>
    public async Task<string?> ClearCrossStoreAsync(string storeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var config = await _configStore.GetAsync(storeId, cancellationToken).ConfigureAwait(false);
        if (config?.ConnectionString is not { } connectionString)
            return null;

        var parsed = SparkConnectionString.Parse(connectionString, out var configuredStoreId, out _, out _);
        if (parsed is not SparkConnectionStringParseResult.Ok ||
            configuredStoreId is null ||
            string.Equals(configuredStoreId, storeId, StringComparison.Ordinal))
        {
            return null;
        }

        var cleared = await _configStore.SetAsync(storeId, null, cancellationToken).ConfigureAwait(false);
        if (cleared)
        {
            _logger.LogWarning(
                "Store {StoreId}: removed its Lightning payment method, which pointed at store "
                + "{VictimStoreId}'s Spark wallet", storeId, configuredStoreId);
        }

        return configuredStoreId;
    }

    /// <summary>
    /// The pure ownership decision, exposed for tests and shared by every path above.
    /// </summary>
    internal static SparkLightningWiringState Classify(
        string storeId,
        string? paymentKey,
        StoreLightningConfig? config)
    {
        if (config is null)
            return SparkLightningWiringState.StoreNotFound;
        if (config.IsInternalNode)
            return SparkLightningWiringState.InternalNode;
        if (string.IsNullOrEmpty(config.ConnectionString))
            return SparkLightningWiringState.NotConfigured;

        var parsed = SparkConnectionString.Parse(
            config.ConnectionString, out var configuredStoreId, out var configuredKey, out _);

        // Invalid counts as "not ours" as well: a malformed flint string is not evidence of what the
        // merchant meant, and refusing to touch it is the conservative reading.
        if (parsed is not SparkConnectionStringParseResult.Ok)
            return SparkLightningWiringState.OtherNode;

        if (!string.Equals(configuredStoreId, storeId, StringComparison.Ordinal))
            return SparkLightningWiringState.OtherStoreSpark;

        if (paymentKey is null)
            return SparkLightningWiringState.Spark;

        return SparkConnectionString.PaymentKeyMatches(paymentKey, configuredKey)
            ? SparkLightningWiringState.Spark
            : SparkLightningWiringState.StaleSpark;
    }
}
