using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The default rate rules that price a USDC or USDT prompt — and nothing else on the server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default rules are server-wide, so they must match only the pairs these prompts ask for.</b> A prompt in the
/// coin asks BTCPay for <c>USDC_X</c>, X being the invoice's currency (and any currency the store's criteria or
/// tracked rates name). BTCPay's rule engine picks a rule for a pair by pattern, in a fixed priority that ignores
/// the order rules are written in (<c>RateRules.FindBestCandidate</c>): the exact pair, then its <em>inverse</em>,
/// then <c>L_X</c> / <c>X_R</c>, then the inverse of those, and only then the store's own <c>X_X</c> catch-all.
/// </para>
/// <para>
/// The first version registered <c>USDT_USD = 1</c>, <c>USDT_X = USDT_BTC * BTC_X</c> and
/// <c>USDT_BTC = 1 / BTC_USD</c>. The last is the inverse of <c>BTC_USDT</c>, which outranks the store's catch-all, so
/// every store on the server that priced anything in USDT or USDC — whether or not it had heard of this plugin —
/// had bitcoin priced at <c>BTC_USD</c> instead of its exchange's <c>BTC_USDT</c> market; and a store whose exchange
/// lists no <c>BTC_USD</c> (Binance) could not create an invoice at all. <c>USDT_X</c> would have done the same one
/// rank lower.
/// </para>
/// <para>
/// So these are exact pairs only, one per fiat currency in <see cref="FiatCurrencies"/>: <c>USDT_USD = 1</c>, and
/// <c>USDT_EUR = BTC_EUR / BTC_USD</c> — dollar parity, exactly as the SDK sizes the quote, crossed through the
/// store's own bitcoin rates. No rule names <c>USDT_BTC</c>, <c>USDT_X</c> or <c>X_USDT</c>, so <c>BTC_USDT</c>
/// resolves exactly as it would without the plugin. A bitcoin- or sats-denominated invoice's prompt is priced by the
/// store's exchange at its <c>BTC_USDT</c> market, through the catch-all and BTCPay's implicit <c>SATS</c> rule.
/// </para>
/// <para>
/// <b>What is left.</b> The exact pairs still apply to any USDC or USDT prompt, so another plugin's USDT prompt on a
/// euro invoice is priced at parity rather than at the exchange's <c>USDT_EUR</c>. They are registered after
/// everyone else's (<see cref="Order"/>) so another plugin's own rule for the same pair wins, and the difference is
/// the coin's distance from its peg. A store on custom rate scripting gets none of these and adds its own.
/// </para>
/// </remarks>
public static class StablecoinRateRules
{
    /// <summary>After BTCPay's own defaults (0) and its recommended exchanges (10), so an explicit rule elsewhere wins.</summary>
    public const int Order = 100;

    /// <summary>
    /// The fiat currencies the coins are priced in at dollar parity: the ones stores price in. Any other currency's
    /// pair falls to the store's own catch-all, which prices it at the exchange's market for the coin when the exchange
    /// lists one.
    /// </summary>
    /// <remarks>
    /// A list rather than every fiat BTCPay knows, because the rules are not free. BTCPay re-parses the whole
    /// default rule set — with Roslyn — every time it builds a store's rules (<c>WithPreferredExchange</c>), which it
    /// does for each invoice, wallet page and public rates request, for every store on the server. Two coins across all
    /// 165 of its fiat currencies measured 17 ms a build in the test suite; this list, 7 ms; without them, under half a
    /// millisecond.
    /// </remarks>
    public static IReadOnlyList<string> FiatCurrencies { get; } =
    [
        "USD", "EUR", "GBP", "CAD", "AUD", "NZD", "CHF", "JPY", "CNY", "HKD", "SGD", "INR", "BRL", "MXN", "ARS", "CLP",
        "COP", "PEN", "ZAR", "NGN", "KES", "GHS", "TRY", "PLN", "CZK", "HUF", "RON", "SEK", "NOK", "DKK", "ILS", "AED",
        "SAR", "KRW", "THB", "IDR", "MYR", "PHP", "VND", "TWD", "UAH", "RUB"
    ];

    /// <summary>The default rules for one coin's prompts.</summary>
    public static DefaultRules For(StablecoinAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        // One script, parsed once: each line on its own would be a Roslyn parse apiece at startup.
        return new DefaultRules(string.Join("\n", Lines(asset.Symbol, FiatCurrencies))) { Order = Order };
    }

    /// <summary>The rule lines for <paramref name="symbol"/> over <paramref name="fiat"/>.</summary>
    public static IEnumerable<string> Lines(string symbol, IEnumerable<string> fiat)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);
        ArgumentNullException.ThrowIfNull(fiat);

        yield return $"{symbol}_USD = 1;";
        foreach (var code in fiat
                     .Select(c => c.Trim().ToUpperInvariant())
                     .Where(c => c.Length > 0 && c != "USD" && c.All(char.IsAsciiLetterOrDigit))
                     .Distinct(StringComparer.Ordinal))
        {
            yield return $"{symbol}_{code} = BTC_{code} / BTC_USD;";
        }
    }
}
