using System;
using System.Numerics;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// A token balance the wallet holds, as reported by <c>GetInfo</c>.
/// </summary>
/// <param name="BaseUnits">
/// <b>Base units, not a decimal quantity and not satoshi.</b> $35.60 of a 6-decimal token is
/// <c>35_600_000</c>. Kept as <see cref="BigInteger"/> because the SDK's is a <c>u128</c> and an 18-decimal
/// token overflows every fixed-width alternative for ordinary amounts.
/// </param>
/// <param name="IsFreezable">
/// Whether the issuer can freeze this balance. <b>True for USDB</b>, whose issuer is a regulated US
/// stablecoin issuer — a counterparty the merchant is accepting on top of Spark's own statechain operators.
/// Surfaced in the UI rather than buried.
/// </param>
public sealed record SparkTokenBalance(
    SparkTokenIdentifier Identifier,
    BigInteger BaseUnits,
    string Ticker,
    string Name,
    uint Decimals,
    bool IsFreezable)
{
    /// <summary>The balance as a decimal quantity with its ticker, e.g. <c>35.6 USDB</c>.</summary>
    public string Describe() => $"{SparkSendAmount.FormatBaseUnits(BaseUnits, Decimals)} {Ticker}";
}

/// <summary>
/// The wallet-level user settings the SDK persists, of which one member matters to this plugin.
/// </summary>
/// <param name="StableBalanceActiveLabel">
/// The label of the active stable-balance token, or null when stable balance is off. Note the asymmetry with
/// the write side: reading gives a plain <c>string?</c>, but writing is a three-state optional-of-enum where
/// <c>null</c> means "leave unchanged" rather than "deactivate" — which is why
/// <see cref="ISparkSdkClient.SetStableBalanceActiveAsync"/> takes an explicit <c>activate</c> flag.
/// </param>
public sealed record SparkUserSettings(bool PrivateModeEnabled, string? StableBalanceActiveLabel)
{
    public bool StableBalanceActive => StableBalanceActiveLabel is not null;
}

/// <summary>Which way a conversion runs.</summary>
public enum SparkConversionDirection
{
    /// <summary>Sats to token. The minimum is expressed in <b>satoshi</b>.</summary>
    FromBitcoin,

    /// <summary>Token to sats. The minimum is expressed in <b>token base units</b>.</summary>
    ToBitcoin
}

/// <summary>
/// The service's minimum for a conversion.
/// </summary>
/// <remarks>
/// <b>The unit of <see cref="MinimumFromAmount"/> follows the <em>from</em> side and therefore changes between
/// the two directions</b> — same field, different meaning: 800 satoshi for <c>FromBitcoin</c>, 500 000 base
/// units ($0.50) for <c>ToBitcoin</c>. <see cref="Direction"/> is carried alongside so a caller cannot read the
/// number without the unit, and the SDK rejects a <c>FromBitcoin</c> request with no token identifier outright.
/// </remarks>
public sealed record SparkConversionLimits(
    SparkConversionDirection Direction,
    BigInteger? MinimumFromAmount,
    BigInteger? MinimumToAmount);

/// <summary>
/// Raised when a stable-balance or cross-chain configuration is attempted on a wallet that cannot support it.
/// </summary>
/// <remarks>
/// <para>
/// Both features require <c>Config.backgroundTasksEnabled == true</c>, and setting either with it false is a
/// <b>hard init failure</b>: <c>stable_balance_config is not supported when background_tasks_enabled is
/// false</c> and <c>Cross-chain config must be unset when background tasks are disabled</c>. A store whose
/// connect throws has no wallet at all, so this is not a feature that degrades — it takes the merchant's
/// Lightning down with it.
/// </para>
/// <para>
/// The plugin builds from <c>DefaultConfig</c>, which sets the flag true, so this is unreachable today. It is
/// asserted rather than assumed because the whole cost of being wrong lands on a store that was working.
/// </para>
/// </remarks>
public sealed class SparkBackgroundTasksRequiredException : InvalidOperationException
{
    public SparkBackgroundTasksRequiredException(string feature)
        : base(
            $"{feature} requires the Spark SDK's background tasks, and this configuration has them disabled. "
            + "Connecting with that combination is a hard failure that would leave the store with no Spark "
            + "wallet at all, so the feature was not configured.")
    {
        Feature = feature;
    }

    public string Feature { get; }
}
