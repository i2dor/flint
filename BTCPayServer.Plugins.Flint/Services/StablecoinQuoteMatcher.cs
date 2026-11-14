using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>What <see cref="StablecoinQuoteMatcher.Match"/> concluded about one completed receive.</summary>
public enum StablecoinMatchKind
{
    /// <summary>Exactly one quote is this receive's.</summary>
    Matched,

    /// <summary>
    /// The receive's quote-time fingerprint names none of this store's open quotes: it is not one of this
    /// plugin's, its quote has left the window, or it carries no fingerprint to go on.
    /// </summary>
    NoMatch,

    /// <summary>More than one open quote carries the receive's fingerprint, and nothing else it carries is evidence.</summary>
    Ambiguous
}

public sealed record StablecoinMatch(StablecoinMatchKind Kind, StablecoinQuote? Quote = null, int Candidates = 0);

/// <summary>
/// Decides which of a store's open quotes a completed USDC/USDT receive came from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why there is a decision to make at all.</b> The SDK returns no quote id when it quotes a receive, and the
/// payment that arrives carries no deposit address, so there is no identifier common to both ends. What the
/// completed payment does carry, frozen from the quote by the SDK's own provider row, is the route plus two
/// amounts — <c>estimatedOut</c> (the quote's <c>expectedReceivedAmount</c>) and <c>serviceFeeAmount</c>. Those
/// two are the fingerprint, and <see cref="StablecoinPaymentService.QuoteAsync"/> only ever records a quote whose
/// fingerprint no other open quote on its route shares, so a fingerprint names one quote.
/// </para>
/// <para>
/// <b>The rules, in order.</b>
/// </para>
/// <list type="number">
/// <item><description>Only quotes on the same route are candidates — chain, asset and contract all equal — and only
/// quotes an arrival at that moment could have paid: made before it (give or take <see cref="ClockSkew"/>), and not
/// out of <see cref="StablecoinPayments.MatchWindow"/> before it. The window is anchored on the arrival rather than
/// on the clock, so a verdict does not drift as time passes: an arrival that fitted two quotes does not start
/// fitting the survivor once the other ages out.</description></item>
/// <item><description>Candidates must carry the payment's fingerprint. One is the answer, whatever the payer sent —
/// the fingerprint is quote-time data, so a payer who sent the wrong amount still paid <em>that</em> quote's
/// address. A fingerprint matching nothing is <see cref="StablecoinMatchKind.NoMatch"/> rather than a reason to
/// guess by amount: it means a quote this plugin did not make, such as the same recovery phrase receiving in a
/// mobile wallet.</description></item>
/// <item><description>Several candidates sharing one fingerprint is <see cref="StablecoinMatchKind.Ambiguous"/>, and
/// is left for a human. It is <b>not</b> broken by the amount paid. <c>assetAmountIn</c> is what the payer's wallet
/// or exchange sent, which may round the ask — a Tron or Solana QR code carries no amount, so the payer types it —
/// and when the provider's order omits it the SDK reports the quote-time deposit in its place
/// (<c>build_orchestra_receive_conversion_info</c>). Asks kept a millionth apart once made "the exact deposit" the
/// tie-break, and a payer who typed 10.08 for an ask of 10.080001 credited somebody else's invoice. Only quotes
/// recorded before fingerprints were kept unique can get here.</description></item>
/// <item><description>A payment with no fingerprint at all is <see cref="StablecoinMatchKind.NoMatch"/>. The SDK
/// this plugin is built against reports both halves on every Orchestra receive it quoted — the fee is written onto
/// its provider row at quote time, and <c>estimatedOut</c> is not optional — so one without them is not a receive
/// one of this plugin's quotes produced, and the amount alone is not evidence, for the reason above.</description></item>
/// </list>
/// <para>
/// Pure and static, so every one of these is tested without an SDK, a database or BTCPay.
/// </para>
/// </remarks>
public static class StablecoinQuoteMatcher
{
    /// <summary>How far an arrival's timestamp may precede its quote's, for clock skew between the two.</summary>
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

    /// <param name="openQuotes">The store's unsettled quotes: at least every one an arrival at <paramref name="arrivedAt"/> could be.</param>
    /// <param name="conversion">The provider's conversion details on the arrival.</param>
    /// <param name="arrivedAt">When the arrival reached the wallet; null when unknown, which admits every open quote.</param>
    public static StablecoinMatch Match(
        IReadOnlyList<StablecoinQuote> openQuotes,
        SparkConversionState conversion,
        DateTimeOffset? arrivedAt = null)
    {
        ArgumentNullException.ThrowIfNull(openQuotes);
        ArgumentNullException.ThrowIfNull(conversion);

        if (conversion.EstimatedOut is not { } estimatedOut || conversion.ServiceFeeAmount is not { } serviceFee)
            return new StablecoinMatch(StablecoinMatchKind.NoMatch);

        // Saturated, because the timestamp is the SDK's and a clamped one sits at either end of the calendar.
        var madeBy = arrivedAt is { } at && at < DateTimeOffset.MaxValue - ClockSkew ? at + ClockSkew : DateTimeOffset.MaxValue;
        var expiringAfter = arrivedAt is { } since && since > DateTimeOffset.MinValue + StablecoinPayments.MatchWindow
            ? since - StablecoinPayments.MatchWindow
            : DateTimeOffset.MinValue;

        var fingerprinted = openQuotes
            .Where(quote => OnRoute(quote, conversion.Chain, conversion.Asset, conversion.AssetContract)
                            && quote.CreatedAt <= madeBy
                            && quote.ExpiresAt > expiringAfter
                            && quote.ExpectedReceived == estimatedOut
                            && quote.ServiceFee == serviceFee)
            .ToList();

        return fingerprinted.Count switch
        {
            0 => new StablecoinMatch(StablecoinMatchKind.NoMatch),
            1 => new StablecoinMatch(StablecoinMatchKind.Matched, fingerprinted[0], 1),
            _ => new StablecoinMatch(StablecoinMatchKind.Ambiguous, Candidates: fingerprinted.Count)
        };
    }

    /// <summary>
    /// Whether an open quote on <paramref name="route"/> already carries this fingerprint — which a quote a payer is
    /// shown must never share, since the fingerprint is all its payment will be matched by.
    /// </summary>
    public static bool FingerprintTaken(
        IEnumerable<StablecoinQuote> openQuotes,
        SparkCrossChainReceiveRoute route,
        BigInteger expectedReceived,
        BigInteger serviceFee)
    {
        ArgumentNullException.ThrowIfNull(openQuotes);
        ArgumentNullException.ThrowIfNull(route);
        return openQuotes.Any(quote => OnRoute(quote, route.Chain, route.Asset, route.ContractAddress)
                                       && quote.ExpectedReceived == expectedReceived
                                       && quote.ServiceFee == serviceFee);
    }

    private static bool OnRoute(StablecoinQuote quote, string? chain, string? asset, string? contract) =>
        StablecoinPayments.Same(quote.Chain, chain)
        && StablecoinPayments.Same(quote.Asset, asset)
        && StablecoinPayments.Same(quote.ContractAddress, contract);
}
