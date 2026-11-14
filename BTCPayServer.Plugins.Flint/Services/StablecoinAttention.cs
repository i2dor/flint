using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// USDC/USDT money in a store's wallet that is on no invoice, for the store owner to place by hand — see
/// <see cref="StablecoinPaymentService.GetAttentionAsync"/>.
/// </summary>
/// <param name="Unattributed">Arrivals that matched no quote, or several.</param>
/// <param name="Uncredited">Payments that matched an invoice but have not been recorded on it, oldest first.</param>
/// <param name="UncreditedCount">How many of those there are, including any the bounded list leaves out.</param>
public sealed record StablecoinAttention(
    IReadOnlyList<StablecoinUnattributedArrival> Unattributed,
    IReadOnlyList<StablecoinUncreditedPayment> Uncredited,
    int UncreditedCount)
{
    public static StablecoinAttention None { get; } = new([], [], 0);

    public bool Any => Unattributed.Count > 0 || UncreditedCount > 0;
}

/// <summary>A completed USDC/USDT receive that could not be attributed to one quote.</summary>
/// <param name="Amount">What the SDK reported as deposited, in the token's units; null when it reported nothing.</param>
/// <param name="Candidates">How many open quotes it fitted equally well; zero when it fitted none.</param>
public sealed record StablecoinUnattributedArrival(
    string SdkPaymentId,
    string Asset,
    string ChainName,
    string? Amount,
    string? ExternalTxHash,
    int Candidates,
    DateTimeOffset ArrivedAt);

/// <summary>A settled USDC/USDT payment whose invoice credit has not landed.</summary>
public sealed record StablecoinUncreditedPayment(
    string InvoiceId,
    string Asset,
    string ChainName,
    string Amount,
    string SdkPaymentId,
    DateTimeOffset SettledAt);
