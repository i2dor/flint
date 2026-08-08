using System;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>The parts of a BOLT11 invoice this plugin needs.</summary>
public sealed record Bolt11Info(string PaymentHash, DateTimeOffset ExpiresAt, long? AmountMsat);

/// <summary>
/// Reads a BOLT11 invoice. A seam rather than a direct NBitcoin call so the surrounding logic can be
/// tested without hand-crafting signed invoices.
/// </summary>
public interface IBolt11Parser
{
    /// <summary>Parses an invoice, returning null when it is malformed or on the wrong network.</summary>
    Bolt11Info? Parse(string bolt11);
}

/// <summary>
/// <see cref="IBolt11Parser"/> backed by NBitcoin's BOLT11 decoder.
/// </summary>
/// <remarks>
/// Used instead of the SDK's own <c>Parse()</c> for two reasons: it is a local computation rather than
/// an async SDK round trip on the checkout path, and it does not risk being dispatched while the SDK's
/// inline event dispatcher holds a lock.
/// </remarks>
public class NBitcoinBolt11Parser : IBolt11Parser
{
    private readonly Network _network;
    private readonly ILogger<NBitcoinBolt11Parser> _logger;

    public NBitcoinBolt11Parser(Network network, ILogger<NBitcoinBolt11Parser> logger)
    {
        _network = network;
        _logger = logger;
    }

    public Bolt11Info? Parse(string bolt11)
    {
        if (string.IsNullOrWhiteSpace(bolt11))
            return null;

        BOLT11PaymentRequest parsed;
        try
        {
            parsed = BOLT11PaymentRequest.Parse(bolt11.Trim(), _network);
        }
        catch (Exception ex)
        {
            // Wrong network or malformed invoice. Callers treat null as "cannot use this invoice"; the
            // reason is logged rather than thrown because the caller has a meaningful fallback.
            _logger.LogWarning(ex, "Could not parse a BOLT11 invoice on {Network}", _network);
            return null;
        }

        if (parsed.PaymentHash is null)
        {
            _logger.LogWarning("Parsed a BOLT11 invoice with no payment hash on {Network}", _network);
            return null;
        }

        // MinimumAmount is LightMoney.Zero for an amountless invoice; normalise that to null so
        // "amountless" and "zero" are not confusable downstream.
        var amountMsat = parsed.MinimumAmount == LightMoney.Zero ? (long?)null : parsed.MinimumAmount.MilliSatoshi;
        return new Bolt11Info(parsed.PaymentHash.ToString().ToLowerInvariant(), parsed.ExpiryDate, amountMsat);
    }
}
