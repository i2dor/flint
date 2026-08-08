using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// A usable route, or the reason there is not one. Exactly one of the two is set.
/// </summary>
public sealed record CrossChainRouteResolution(
    SparkCrossChainRoute? Route,
    SweepRefusal? Refusal,
    IReadOnlyList<SparkCrossChainRoute> Considered)
{
    public static CrossChainRouteResolution Resolved(
        SparkCrossChainRoute route,
        IReadOnlyList<SparkCrossChainRoute> considered) =>
        new(route, null, considered);

    public static CrossChainRouteResolution Refused(
        SweepRefusal refusal,
        IReadOnlyList<SparkCrossChainRoute>? considered = null) =>
        new(null, refusal, considered ?? []);
}

/// <summary>
/// Picks the route a cross-chain sweep will take, out of everything the SDK offers.
/// </summary>
/// <remarks>
/// <para>
/// <b>The route list cannot be trusted as-is</b>, and that is what this class is for. Three of its rules exist
/// because the naive reading of the list is wrong in a way that costs money or time:
/// </para>
/// <list type="number">
/// <item><description><b>Boltz routes appear and do not work.</b> Every Boltz prepare attempted during the
/// spike — three chains, three amounts, six attempts — failed with <c>Boltz API: BTC/TBTC pair not found. Is
/// referral header configured?</c>, from a machine that could reach Boltz perfectly well. So Boltz is filtered
/// out here rather than discovered at prepare, and a destination whose only route is Boltz is reported as
/// having no route rather than as a provider failure the merchant can do nothing about. If the referral
/// question is resolved with Breez, deleting the filter is the whole change.</description></item>
/// <item><description><b>Boltz cannot send from a token balance at all</b> (<c>Boltz does not support token
/// sends in v1</c>), so a Stable-Balance store could never use one even if prepare worked. The source check
/// below covers that independently of the provider filter.</description></item>
/// <item><description><b>The asset name must match exactly.</b> Boltz mostly carries <c>USDT0</c>, the
/// LayerZero omnichain token — a genuinely different asset that a merchant expecting Tether will not accept.
/// A prefix or contains match would silently deliver it.</description></item>
/// </list>
/// <para>
/// An empty list from the SDK is not handled here at all: the client raises
/// <see cref="SparkCrossChainNotConfiguredException"/> for it, because it means the plugin's own configuration
/// is wrong rather than that the merchant chose an unreachable chain.
/// </para>
/// </remarks>
public sealed class CrossChainRouteResolver
{
    private readonly ILogger<CrossChainRouteResolver> _logger;

    public CrossChainRouteResolver(ILogger<CrossChainRouteResolver> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Resolves the route for a store's configured EVM destination.
    /// </summary>
    /// <param name="fromToken">
    /// The token a sweep would be funded from, or null when it would be funded from the sats balance. Decides
    /// whether a route needs to support token sources — which, today, means whether Orchestra is merely
    /// preferred or strictly required.
    /// </param>
    public async Task<CrossChainRouteResolution> ResolveAsync(
        ISparkSdkClient sdk,
        SweepDestination destination,
        SweepSettings settings,
        SparkTokenIdentifier? fromToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sdk);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(settings);

        var chain = settings.EffectiveCrossChainChain;
        var asset = settings.EffectiveCrossChainAsset;

        IReadOnlyList<SparkCrossChainRoute> routes;
        try
        {
            routes = await sdk
                .GetCrossChainRoutesAsync(destination.Address, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SparkCrossChainNotConfiguredException ex)
        {
            // Deliberately loud, and deliberately not folded into "no route". This is the trap the spike flagged
            // first: with the SDK's cross-chain config unset the route query answers with an empty array and no
            // error, so the honest report is that the plugin is misconfigured, not that the merchant picked a
            // bad chain.
            _logger.LogError(ex,
                "Cross-chain routing returned nothing at all for {Address}. The SDK's cross-chain configuration "
                + "is set on every mainnet connect, so this is a plugin or network fault rather than an "
                + "unreachable destination", destination.Address);

            return CrossChainRouteResolution.Refused(new SweepRefusal(
                SweepRefusalCode.CrossChainUnavailable,
                "Cross-chain sending is not available on this server right now. Spark returned no routes at "
                + "all, which means the feature is not configured rather than that this chain is unreachable — "
                + "it is also mainnet-only. Nothing was sent."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read cross-chain routes for {Address} ({Reason})",
                destination.Address, SparkErrors.Describe(ex));

            return CrossChainRouteResolution.Refused(new SweepRefusal(
                SweepRefusalCode.CrossChainUnavailable,
                $"Spark could not look up a cross-chain route: {SparkErrors.Describe(ex)}"));
        }

        var matching = routes
            .Where(route => string.Equals(route.Chain, chain, StringComparison.OrdinalIgnoreCase))
            // Exact, case-insensitive. USDT0 is not USDT.
            .Where(route => string.Equals(route.Asset, asset, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matching.Count == 0)
        {
            return CrossChainRouteResolution.Refused(
                new SweepRefusal(
                    SweepRefusalCode.NoCrossChainRoute,
                    $"Spark has no route carrying {asset} on {chain}. "
                    + DescribeAlternatives(routes, asset)),
                routes);
        }

        var usable = matching
            .Where(route => route.Provider is SparkCrossChainProvider.Orchestra)
            .Where(route => fromToken is null ? route.SupportsBitcoin : route.SupportsToken)
            .ToList();

        if (usable.Count == 0)
        {
            var providers = string.Join(", ", matching.Select(route => route.Provider.ToString()).Distinct());

            // Named as a provider problem rather than a routing one, because it is: the route exists, and the
            // merchant cannot fix it by choosing differently.
            _logger.LogWarning(
                "A {Asset}/{Chain} route exists but none is usable: providers were {Providers}, and the sweep "
                + "would be funded from {Source}",
                asset, chain, providers, fromToken is null ? "the sats balance" : "a token balance");

            return CrossChainRouteResolution.Refused(
                new SweepRefusal(
                    SweepRefusalCode.NoCrossChainRoute,
                    fromToken is null
                        ? $"The only {asset} routes to {chain} go through {providers}, and none of those can "
                          + "carry this sweep today. Nothing was sent."
                        : $"The only {asset} routes to {chain} go through {providers}, and none of those can "
                          + "send from a stablecoin balance. Either switch the destination chain, or turn "
                          + "Stable Balance off so sweeps are funded from the Bitcoin balance."),
                routes);
        }

        // Deterministic rather than "the first one the SDK happened to list", so two passes over an unchanged
        // route table make the same choice and a sweep is reproducible from its record.
        var chosen = usable
            .OrderBy(route => route.Chain, StringComparer.Ordinal)
            .ThenBy(route => route.ContractAddress ?? string.Empty, StringComparer.Ordinal)
            .First();

        return CrossChainRouteResolution.Resolved(chosen, routes);
    }

    /// <summary>
    /// The chains that <em>do</em> carry the asked-for asset, for a refusal a merchant can act on.
    /// </summary>
    /// <remarks>
    /// Restricted to Orchestra and capped, because listing the Boltz chains would be suggesting destinations
    /// that fail at prepare.
    /// </remarks>
    private static string DescribeAlternatives(IReadOnlyList<SparkCrossChainRoute> routes, string asset)
    {
        var chains = routes
            .Where(route => route.Provider is SparkCrossChainProvider.Orchestra)
            .Where(route => string.Equals(route.Asset, asset, StringComparison.OrdinalIgnoreCase))
            .Select(route => route.Chain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(chain => chain, StringComparer.Ordinal)
            .Take(8)
            .ToList();

        return chains.Count == 0
            ? "Nothing was sent."
            : $"It does carry {asset} on: {string.Join(", ", chains)}. Nothing was sent.";
    }
}
