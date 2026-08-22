using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// A store's Stable Balance state: what it asked for, what the wallet is actually doing, and what it holds.
/// </summary>
/// <param name="DesiredActive">
/// What the merchant configured. The <em>intent</em>.
/// </param>
/// <param name="ActiveLabel">
/// The label the wallet reports as active, or null when stable balance is off there. The <em>fact</em>.
/// </param>
/// <param name="Balance">The token balance the wallet holds, or null when it holds none.</param>
/// <param name="ConversionMinimumSats">
/// The service's own floor on a BTC→token conversion, in satoshi, when it could be read. A configured
/// threshold below this is clamped <em>up</em> to it by the SDK rather than honoured.
/// </param>
public sealed record SparkStableBalanceView(
    bool Configured,
    bool WalletRunning,
    bool MainnetOnly,
    bool DesiredActive,
    string? ActiveLabel,
    SparkTokenBalance? Balance,
    long? ConversionMinimumSats,
    string? WalletError,
    StableBalanceSettings Settings)
{
    public static SparkStableBalanceView NotConfigured() =>
        new(false, false, false, false, null, null, null, null, new StableBalanceSettings());

    /// <summary>Whether the wallet is actually holding a stable balance right now.</summary>
    public bool ActuallyActive => ActiveLabel is not null;

    /// <summary>
    /// True when the wallet's own state could not be read at all.
    /// </summary>
    /// <remarks>
    /// Distinct from "reads as off". A wallet that could not be queried is not a wallet that agrees with the
    /// setting, and treating the two alike is how a page tells a merchant everything is fine.
    /// </remarks>
    public bool ActiveStateUnknown => WalletError is not null;

    /// <summary>
    /// True when the wallet holds a stablecoin balance while reporting nothing active to manage it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The state mainnet found, and the one that reads most like success.</b> After a deactivation that
    /// failed because the wallet had lost its stable-balance config, the store's setting said off and the wallet
    /// reported no active label — which looks like agreement — while 0.235824 USDB sat in the wallet with no
    /// route back. Nothing about "off" and "off" says the money is stranded.
    /// </para>
    /// <para>
    /// It is also briefly true during a legitimate deactivation, while the conversion back is still running. In
    /// that case re-applying is a harmless no-op, and saying so is better than the alternative: the two cases
    /// are indistinguishable from outside the SDK, and only one of them is safe to stay quiet about.
    /// </para>
    /// </remarks>
    public bool HoldingUnmanagedBalance =>
        !ActuallyActive && Balance is { } held && held.BaseUnits > BigInteger.Zero;

    /// <summary>
    /// True when the merchant's intent and the wallet's state disagree, or cannot be shown to agree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reported rather than silently reconciled, and that is the important part.</b> Reconciling means
    /// activating or deactivating — which means converting a merchant's whole balance between Bitcoin and a
    /// stablecoin — without them asking, on a page load or a timer. The disagreement is real and legitimate:
    /// the SDK caches the active label per wallet, so a store whose seed was replaced, or whose storage
    /// directory is new, starts deactivated no matter what the setting says.
    /// </para>
    /// <para>
    /// <b>Three conditions, not one.</b> The obvious one is intent differing from state. The other two are the
    /// ones that used to read as agreement: a wallet whose state could not be read at all, and a wallet holding
    /// a stablecoin it reports nothing active for. Both were false-negatives that told a merchant their money
    /// was where they thought it was.
    /// </para>
    /// <para>
    /// So the surfaces show every value and offer a button. Nothing converts without a press.
    /// </para>
    /// </remarks>
    public bool NeedsReapply =>
        Configured && WalletRunning &&
        (ActiveStateUnknown || DesiredActive != ActuallyActive || HoldingUnmanagedBalance);

    /// <summary>True when the wallet holds a stable balance the merchant has since asked to turn off.</summary>
    public bool HoldingAfterDisable =>
        !DesiredActive && Balance is { } balance && balance.BaseUnits > BigInteger.Zero;
}

/// <summary>What happened to a Stable Balance write.</summary>
public enum SparkStableBalanceStatus
{
    Applied,
    NotConfigured,
    Unavailable,
    Invalid
}

public sealed record SparkStableBalanceResult(
    SparkStableBalanceStatus Status,
    string Message,
    IReadOnlyList<SparkSweepSettingsError> Errors)
{
    private static readonly SparkSweepSettingsError[] NoErrors = [];

    public static SparkStableBalanceResult Applied(string message) =>
        new(SparkStableBalanceStatus.Applied, message, NoErrors);

    public static SparkStableBalanceResult Invalid(params SparkSweepSettingsError[] errors) =>
        new(SparkStableBalanceStatus.Invalid, errors.FirstOrDefault()?.Error ?? "Invalid.", errors);

    public static SparkStableBalanceResult Unavailable(string message) =>
        new(SparkStableBalanceStatus.Unavailable, message, NoErrors);

    public static SparkStableBalanceResult NotConfigured() =>
        new(SparkStableBalanceStatus.NotConfigured, "Flint is not set up for this store.", NoErrors);

    public bool Succeeded => Status is SparkStableBalanceStatus.Applied;
}

/// <summary>
/// The single path by which a store's Stable Balance configuration is read, validated, written and activated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Activation is a money movement, not a setting.</b> Turning this on queues a conversion of the store's
/// Bitcoin balance into USDB; turning it off queues the reverse. Both run on the SDK's own background worker
/// rather than inline, both take a spread, and <b>no SDK event reports either one</b> — the nine
/// <c>SdkEvent</c> variants contain nothing about conversions — so a merchant who has just pressed the button
/// will see nothing change for a while and must be told so.
/// </para>
/// <para>
/// <b>The disclosure is a gate, not decoration.</b> USDB is issued by a regulated issuer and its metadata
/// reports <c>isFreezable: true</c>: the issuer can freeze the merchant's balance. That is a genuinely new
/// counterparty on top of Spark's own 2-of-3 operator set, and neither surface will activate
/// without <see cref="StableBalanceSettings.DisclosureAcknowledged"/>.
/// </para>
/// <para>
/// <b>Mainnet only, and refused rather than accepted-then-ignored elsewhere.</b> The SDK <em>accepts</em> a
/// stable-balance configuration on regtest and then never converts, because USDB does not exist there. A
/// silent no-op is the worst of the three possible behaviours, so this refuses instead.
/// </para>
/// </remarks>
public sealed class SparkStableBalanceService
{
    private readonly ISparkStoreSettingsStore _settingsStore;
    private readonly ISparkStoreRuntime _runtime;
    private readonly bool _mainnet;
    private readonly ILogger<SparkStableBalanceService> _logger;

    /// <param name="mainnet">
    /// Whether this server runs on Bitcoin mainnet. Resolved once at registration, because the chain is fixed
    /// for the life of the process.
    /// </param>
    public SparkStableBalanceService(
        ISparkStoreSettingsStore settingsStore,
        ISparkStoreRuntime runtime,
        bool mainnet,
        ILogger<SparkStableBalanceService> logger)
    {
        _settingsStore = settingsStore;
        _runtime = runtime;
        _mainnet = mainnet;
        _logger = logger;
    }

    /// <summary>Whether Stable Balance can work on this server at all.</summary>
    public bool Available => _mainnet;

    public async Task<SparkStableBalanceView> ReadAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
            return SparkStableBalanceView.NotConfigured();

        var stable = settings.StableBalance ?? new StableBalanceSettings();

        var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
        {
            return new SparkStableBalanceView(
                true, false, !_mainnet, stable.Enabled, null, null, null,
                "This store's Spark wallet is not running.", stable);
        }

        string? activeLabel = null;
        string? walletError = null;
        SparkTokenBalance? balance = null;
        long? minimum = null;

        try
        {
            var userSettings = await sdk.GetUserSettingsAsync(cancellationToken).ConfigureAwait(false);
            activeLabel = userSettings.StableBalanceActiveLabel;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: could not read its Spark user settings ({Reason})",
                storeId, SparkErrors.Describe(ex));
            walletError = SparkErrors.Describe(ex);
        }

        try
        {
            // Cached read: a request thread, and the balance is indicative wherever it is reported.
            var info = await sdk.GetInfoAsync(ensureSynced: false, cancellationToken).ConfigureAwait(false);
            if (stable.Token() is { } token)
                balance = info.TokenBalance(token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: could not read its Spark token balances ({Reason})",
                storeId, SparkErrors.Describe(ex));
            walletError ??= SparkErrors.Describe(ex);
        }

        if (_mainnet && stable.Token() is { } limitToken)
        {
            try
            {
                var limits = await sdk
                    .FetchConversionLimitsAsync(SparkConversionDirection.FromBitcoin, limitToken, cancellationToken)
                    .ConfigureAwait(false);

                // FromBitcoin, so the minimum is in satoshi. The same field means token base units in the other
                // direction, which is why the direction travels with it.
                if (limits.MinimumFromAmount is { } floor)
                    minimum = (long)BigInteger.Min(floor, long.MaxValue);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Store {StoreId}: could not read conversion limits", storeId);
            }
        }

        return new SparkStableBalanceView(
            true, true, !_mainnet, stable.Enabled, activeLabel, balance, minimum, walletError, stable);
    }

    /// <summary>
    /// Stores a Stable Balance configuration and applies its activation state to the wallet.
    /// </summary>
    /// <remarks>
    /// The order is deliberate and matches the provisioner's: validate, then write, then act. A settings write
    /// that succeeded after a failed activation would leave a store claiming to hold dollars it does not; a
    /// failed activation after a successful write is merely a disagreement the read surfaces and a button
    /// fixes.
    /// </remarks>
    public async Task<SparkStableBalanceResult> SaveAsync(
        string storeId,
        StableBalanceInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentNullException.ThrowIfNull(input);

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
            return SparkStableBalanceResult.NotConfigured();

        var errors = input.Validate(_mainnet).ToArray();
        if (errors.Length > 0)
            return SparkStableBalanceResult.Invalid(errors);

        var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
        {
            return SparkStableBalanceResult.Unavailable(
                "This store's Spark wallet is not running, so Stable Balance cannot be changed.");
        }

        // Applied onto a copy of the whole settings object, not onto the one that was read. Cloning only the
        // stable-balance block was not enough: assigning it back still mutated the object the store handed over,
        // so a write that threw on the way to the database left the caller's copy holding a configuration that
        // was never persisted. Only StableBalance is touched: the protected mnemonic in the same blob is carried
        // across untouched.
        var updated = settings.Clone();
        var stable = (updated.StableBalance ?? new StableBalanceSettings()).Clone();
        var wasActive = stable.Enabled;
        var previousToken = stable.TokenIdentifier;
        input.ApplyTo(stable);

        // Switching tokens while the old one still holds a balance would strand it: only the configured token
        // is converted, displayed and swept, so the old balance would sit invisible on the wallet with nothing
        // in the UI even acknowledging it exists. Refused rather than warned — convert or sweep it out first.
        if (!string.IsNullOrEmpty(previousToken)
            && !string.Equals(previousToken, stable.TokenIdentifier, StringComparison.Ordinal))
        {
            try
            {
                var info = await sdk.GetInfoAsync(ensureSynced: true, cancellationToken).ConfigureAwait(false);
                var held = info.Tokens.FirstOrDefault(t =>
                    string.Equals(t.Identifier.Value, previousToken, StringComparison.Ordinal));
                if (held is { BaseUnits.Sign: > 0 })
                {
                    return SparkStableBalanceResult.Invalid(new SparkSweepSettingsError(
                        nameof(StableBalanceInput.TokenIdentifier),
                        $"This wallet still holds {held.Describe()}. Changing the token would leave that "
                        + "balance invisible to this plugin — convert it back to Bitcoin or sweep it out "
                        + "before switching."));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: could not read token balances to allow a stable-balance token switch "
                    + "({Reason})", storeId, SparkErrors.Describe(ex));
                return SparkStableBalanceResult.Unavailable(
                    "The wallet's token balances could not be read, so the token cannot be switched safely "
                    + "right now. Try again once the wallet is reachable.");
            }
        }

        // Verified against the SDK before anything is written, not after. An identifier the wallet does not
        // know is not a setting to store and correct later — activating on it would fail at the point the
        // merchant has been told it worked.
        if (stable.Enabled && stable.Token() is { } token)
        {
            try
            {
                await sdk
                    .FetchConversionLimitsAsync(SparkConversionDirection.FromBitcoin, token, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: Spark rejected the configured stable-balance token ({Reason})",
                    storeId, SparkErrors.Describe(ex));

                return SparkStableBalanceResult.Invalid(new SparkSweepSettingsError(
                    nameof(StableBalanceInput.TokenIdentifier),
                    $"Spark does not recognise this token identifier: {SparkErrors.Describe(ex)}"));
            }
        }

        updated.StableBalance = stable;
        var applied = await _settingsStore.SetAsync(storeId, updated).ConfigureAwait(false);

        // Re-resolved, never reused.
        //
        // Storing settings reconciles the store's running instance with them: the old SDK handle is torn down
        // and disposed, and a fresh one is connected. The handle resolved before the write is therefore dead by
        // the time the write returns, and using it throws ObjectDisposedException — which this method used to
        // catch and report as "Spark did not apply it", so every single save reported failure with a wrong
        // diagnosis while quietly never activating anything.
        sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
        {
            return SparkStableBalanceResult.Unavailable(
                "The setting was saved, but this store's Spark wallet did not come back up, so the change was "
                + "not applied to it. The Stable Balance page offers a way to try again.");
        }

        // Activation goes through UpdateUserSettings rather than through the wallet's configuration, because a
        // cached active label takes precedence over the config's default forever after — driving it from config
        // would work exactly once.
        try
        {
            await sdk
                .SetStableBalanceActiveAsync(stable.Enabled, stable.EffectiveLabel, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Store {StoreId}: stable balance was stored as {Desired} but the wallet refused the change "
                + "({Reason})",
                storeId, stable.Enabled ? "on" : "off", SparkErrors.Describe(ex));

            return SparkStableBalanceResult.Unavailable(
                $"The setting was saved, but Spark did not apply it: {SparkErrors.Describe(ex)}. The Stable "
                + "Balance page will show that the wallet and the setting disagree, and offers a way to try "
                + "again.");
        }

        if (!applied.WalletRunning)
        {
            return SparkStableBalanceResult.Unavailable(
                "The setting was saved, but this store's Spark wallet is not running: "
                + (applied.Reason ?? "check the server logs."));
        }

        var message = stable.Enabled
            ? (wasActive
                ? "Stable Balance settings saved."
                : $"Stable Balance is on. Spark will convert this store's Bitcoin balance to {stable.EffectiveLabel} "
                  + "in the background — it does not happen immediately and nothing reports when it finishes, so "
                  + "check back rather than waiting on this page.")
            : "Stable Balance is off. Spark will convert any stablecoin balance back to Bitcoin in the "
              + "background — again, not immediately, and with nothing to report when it finishes.";

        return SparkStableBalanceResult.Applied(message);
    }

    /// <summary>
    /// Re-applies the stored activation state to the wallet, for a store where the two have diverged.
    /// </summary>
    /// <remarks>
    /// The repair for a wallet whose cached user settings do not match the store's configuration — a replaced
    /// seed, a fresh storage directory, an activation the wallet refused. Explicit, because it converts.
    /// </remarks>
    public async Task<SparkStableBalanceResult> ReapplyAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
            return SparkStableBalanceResult.NotConfigured();

        var stable = settings.StableBalance ?? new StableBalanceSettings();

        // The same two gates SaveAsync applies, because this is a second way to reach the same activation and a
        // gate only one entry point enforces is a gate with a documented bypass. The network one matters as much
        // as the disclosure: a stored blob can say enabled on a server that is not mainnet — carried across from
        // a restored backup, say — and re-applying it there would activate a token that does not exist.
        if (stable.Enabled && !_mainnet)
        {
            return SparkStableBalanceResult.Invalid(new SparkSweepSettingsError(
                nameof(StableBalanceInput.Enabled), MainnetOnly));
        }

        if (stable.Enabled && !stable.DisclosureAcknowledged)
        {
            return SparkStableBalanceResult.Invalid(new SparkSweepSettingsError(
                nameof(StableBalanceInput.DisclosureAcknowledged), DisclosureRequired));
        }

        var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
            return SparkStableBalanceResult.Unavailable("This store's Spark wallet is not running.");

        try
        {
            await sdk
                .SetStableBalanceActiveAsync(stable.Enabled, stable.EffectiveLabel, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return SparkStableBalanceResult.Unavailable(
                $"Spark did not apply the change: {SparkErrors.Describe(ex)}");
        }

        return SparkStableBalanceResult.Applied(
            stable.Enabled
                ? "Stable Balance was re-applied to this store's wallet. The conversion runs in the background."
                : "Stable Balance was switched off on this store's wallet. The conversion back runs in the "
                  + "background.");
    }

    internal const string MainnetOnly =
        "Stable Balance only exists on Bitcoin mainnet. USDB has no regtest deployment, so switching this on "
        + "here would store a setting that never converts anything.";

    internal const string DisclosureRequired =
        "Confirm that you have read the counterparty warning. Holding a store's balance in USDB adds a "
        + "regulated issuer who can freeze it, on top of the Spark operators.";
}

/// <summary>
/// The bound half of the Stable Balance settings page, and the body of its Greenfield endpoint.
/// </summary>
/// <remarks>
/// Carries no store id, for the reason every other request model here does not: BTCPay resolves the authorised
/// store from route data and never from a JSON body, so a bindable store member is the cross-store hole this
/// plugin already found once.
/// </remarks>
public class StableBalanceInput
{
    /// <summary>Whether the store wants its balance held in a stablecoin.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Confirmation that the freezable-issuer warning has been read. Required to enable.
    /// </summary>
    /// <remarks>
    /// On the API as well as the form, deliberately. A disclosure that only one surface enforces is a
    /// disclosure with a documented bypass.
    /// </remarks>
    public bool DisclosureAcknowledged { get; set; }

    /// <summary>The token identifier. Defaults to mainnet USDB.</summary>
    public string? TokenIdentifier { get; set; } = StableBalanceSettings.DefaultTokenIdentifier;

    /// <summary>The label the wallet activates by. An arbitrary display string with no protocol meaning.</summary>
    public string? Label { get; set; } = StableBalanceSettings.DefaultLabel;

    /// <summary>Conversion slippage tolerance in basis points. The SDK's own default is 10.</summary>
    public uint MaxSlippageBps { get; set; } = StableBalanceSettings.DefaultMaxSlippageBps;

    /// <summary>Balance at which Spark auto-converts, in satoshi. Null leaves the service's minimum in force.</summary>
    public long? AutoConvertThresholdSats { get; set; }

    public static StableBalanceInput From(StableBalanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new StableBalanceInput
        {
            Enabled = settings.Enabled,
            DisclosureAcknowledged = settings.DisclosureAcknowledged,
            TokenIdentifier = settings.TokenIdentifier,
            Label = settings.EffectiveLabel,
            MaxSlippageBps = settings.MaxSlippageBps,
            AutoConvertThresholdSats = settings.AutoConvertThresholdSats
        };
    }

    /// <summary>
    /// Applies this form onto a store's settings, leaving anything not on the form alone.
    /// </summary>
    /// <remarks>
    /// Mutates a caller-supplied instance rather than constructing one, so a property added to
    /// <see cref="StableBalanceSettings"/> that this form does not expose survives a save instead of silently
    /// reverting to its default. <c>Decimals</c> is exactly such a property today.
    /// </remarks>
    public void ApplyTo(StableBalanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Enabled = Enabled;
        settings.DisclosureAcknowledged = DisclosureAcknowledged;
        settings.TokenIdentifier = string.IsNullOrWhiteSpace(TokenIdentifier)
            ? StableBalanceSettings.DefaultTokenIdentifier
            : TokenIdentifier.Trim();
        settings.Label = string.IsNullOrWhiteSpace(Label)
            ? StableBalanceSettings.DefaultLabel
            : Label.Trim();
        settings.MaxSlippageBps = MaxSlippageBps;
        settings.AutoConvertThresholdSats = AutoConvertThresholdSats is > 0 ? AutoConvertThresholdSats : null;
    }

    /// <summary>Every reason this configuration would be rejected, keyed by the field to attach it to.</summary>
    public IReadOnlyList<SparkSweepSettingsError> Validate(bool mainnet)
    {
        var errors = new List<SparkSweepSettingsError>();

        if (Enabled && !mainnet)
        {
            // Refused rather than stored and ignored. The SDK would accept the configuration on regtest and
            // simply never convert, which looks identical to a broken plugin.
            errors.Add(new SparkSweepSettingsError(nameof(Enabled), SparkStableBalanceService.MainnetOnly));
        }

        if (Enabled && !DisclosureAcknowledged)
            errors.Add(new SparkSweepSettingsError(nameof(DisclosureAcknowledged), SparkStableBalanceService.DisclosureRequired));

        if (Enabled && string.IsNullOrWhiteSpace(TokenIdentifier))
        {
            errors.Add(new SparkSweepSettingsError(
                nameof(TokenIdentifier),
                "A token identifier is required. Spark ships no default for this, so the plugin supplies the "
                + "mainnet USDB identifier and it must not be blank."));
        }

        if (Enabled && string.IsNullOrWhiteSpace(Label))
            errors.Add(new SparkSweepSettingsError(nameof(Label), "A label is required."));

        if (MaxSlippageBps > StableBalanceSettings.MaxSlippageBpsLimit)
        {
            errors.Add(new SparkSweepSettingsError(
                nameof(MaxSlippageBps),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Slippage cannot be above {0:N0} basis points.",
                    StableBalanceSettings.MaxSlippageBpsLimit)));
        }

        if (AutoConvertThresholdSats is { } threshold && threshold < 0)
            errors.Add(new SparkSweepSettingsError(nameof(AutoConvertThresholdSats), "The threshold cannot be negative."));

        return errors;
    }
}
