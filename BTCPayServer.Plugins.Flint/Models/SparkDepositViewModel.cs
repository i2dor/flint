using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BTCPayServer.Plugins.Flint.Models;

/// <summary>
/// The on-chain deposit page: where to send Bitcoin, and what has not arrived.
/// </summary>
/// <remarks>
/// Holds no bound members at all. The only thing this page posts is a claim, and that names a deposit by its
/// outpoint on a form of its own — every value it needs is re-read server-side before anything is broadcast, so
/// a stale page cannot claim a deposit that has since been claimed or authorise a fee the store's policy would
/// refuse.
/// </remarks>
public class SparkDepositViewModel
{
    [BindNever]
    public string StoreId { get; set; } = string.Empty;

    [BindNever]
    public SparkDepositView Deposits { get; set; } = SparkDepositView.NotConfigured();
}

/// <summary>
/// The Stable Balance page: the configuration, and what the wallet is actually doing with it.
/// </summary>
public class SparkStableBalanceViewModel
{
    [BindNever]
    public string StoreId { get; set; } = string.Empty;

    /// <summary>The only inbound part of this page.</summary>
    [Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]
    public StableBalanceInput Settings { get; set; } = new();

    /// <summary>
    /// What the wallet reports, which is not necessarily what <see cref="Settings"/> asks for.
    /// </summary>
    /// <remarks>
    /// The disagreement is shown rather than reconciled: converging the two means converting the store's whole
    /// balance, which is not something a page load may decide to do.
    /// </remarks>
    [BindNever]
    public SparkStableBalanceView View { get; set; } = SparkStableBalanceView.NotConfigured();
}
