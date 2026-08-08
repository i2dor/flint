using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Stable Balance: the disclosure gate, the network gate, and the disagreement that is reported rather than fixed.
/// </summary>
/// <remarks>
/// Activation is a money movement, not a setting — it converts the store's whole balance, in the background,
/// with no event to say when it finished. So the interesting behaviour is all about what the plugin refuses to
/// do on its own.
/// </remarks>
public class SparkStableBalanceServiceTests
{
    private const string StoreId = "store-1";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region Gates

    /// <summary>
    /// Enabling without acknowledging the freezable-issuer disclosure is refused.
    /// </summary>
    /// <remarks>
    /// USDB's issuer can freeze the balance, and if they do, this plugin cannot move it, sweep it or convert it
    /// back. That is a new counterparty on top of Spark's own operators, and it is exactly the
    /// kind of thing a merchant should have to tick rather than discover.
    /// </remarks>
    [Fact]
    public async Task Enabling_without_acknowledging_the_freeze_risk_is_refused()
    {
        var h = Harness();

        var result = await h.Service.SaveAsync(
            StoreId,
            new StableBalanceInput { Enabled = true, DisclosureAcknowledged = false },
            Ct);

        Assert.Equal(SparkStableBalanceStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Field == nameof(StableBalanceInput.DisclosureAcknowledged));
        Assert.Contains("freeze", Assert.Single(result.Errors, e => e.Field == nameof(StableBalanceInput.DisclosureAcknowledged)).Error, StringComparison.OrdinalIgnoreCase);

        // Nothing was stored and nothing was activated.
        Assert.False(h.Settings.Settings[StoreId]!.StableBalance.Enabled);
        Assert.Empty(h.Sdk.StableBalanceCalls);
    }

    /// <summary>
    /// The same refusal reaches the re-apply path, which does not go through the form.
    /// </summary>
    /// <remarks>
    /// A disclosure only one entry point enforces is a disclosure with a documented bypass — and re-apply is the
    /// entry point a merchant reaches by pressing a button on a page that is already warning them.
    /// </remarks>
    [Fact]
    public async Task Re_applying_an_unacknowledged_activation_is_refused_too()
    {
        var h = Harness();
        // A stored configuration that says on but was never acknowledged — a hand edit, or an older blob.
        h.Settings.Settings[StoreId]!.StableBalance = new StableBalanceSettings
        {
            Enabled = true,
            DisclosureAcknowledged = false
        };

        var result = await h.Service.ReapplyAsync(StoreId, Ct);

        Assert.Equal(SparkStableBalanceStatus.Invalid, result.Status);
        Assert.Empty(h.Sdk.StableBalanceCalls);
    }

    /// <summary>
    /// Re-applying an enabled configuration off mainnet is refused too.
    /// </summary>
    /// <remarks>
    /// The same asymmetry the disclosure gate had: re-apply is a second route to the same activation, and a
    /// gate only one route enforces is a gate with a bypass. A stored blob can legitimately say enabled on a
    /// server that is not mainnet — carried across from a restored backup, or from a store provisioned
    /// elsewhere — and re-applying it there would activate a token that does not exist.
    /// </remarks>
    [Fact]
    public async Task Re_applying_off_mainnet_is_refused()
    {
        var h = Harness(mainnet: false);
        h.Settings.Settings[StoreId]!.StableBalance = new StableBalanceSettings
        {
            Enabled = true,
            DisclosureAcknowledged = true
        };

        var result = await h.Service.ReapplyAsync(StoreId, Ct);

        Assert.Equal(SparkStableBalanceStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Field == nameof(StableBalanceInput.Enabled));
        Assert.Empty(h.Sdk.StableBalanceCalls);
    }

    /// <summary>
    /// Off mainnet, enabling is refused rather than stored and quietly ignored.
    /// </summary>
    /// <remarks>
    /// <b>The worst of the three possible behaviours is the one Spark gives you.</b> It <em>accepts</em> a
    /// stable-balance configuration on regtest and then never converts anything, because USDB does not exist
    /// there — indistinguishable from a broken plugin. A refusal is the only honest answer.
    /// </remarks>
    [Fact]
    public async Task Enabling_off_mainnet_is_refused_rather_than_silently_doing_nothing()
    {
        var h = Harness(mainnet: false);

        var result = await h.Service.SaveAsync(
            StoreId,
            new StableBalanceInput { Enabled = true, DisclosureAcknowledged = true },
            Ct);

        Assert.Equal(SparkStableBalanceStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Field == nameof(StableBalanceInput.Enabled));
        Assert.Empty(h.Sdk.StableBalanceCalls);
        Assert.False(h.Service.Available);
    }

    /// <summary>
    /// A token identifier Spark does not recognise is refused before it is stored.
    /// </summary>
    /// <remarks>
    /// The identifier is a piece of mainnet state hard-coded into a distributed binary, and it is deliberately
    /// overridable — so it is also mistypeable. Checking it against Spark first means a store cannot be
    /// configured for a token that does not exist and then fail at activation, after the merchant has been told
    /// it worked.
    /// </remarks>
    [Fact]
    public async Task A_token_identifier_Spark_does_not_know_is_refused_before_it_is_stored()
    {
        var h = Harness();

        var result = await h.Service.SaveAsync(
            StoreId,
            new StableBalanceInput
            {
                Enabled = true,
                DisclosureAcknowledged = true,
                TokenIdentifier = "btkn1definitelynotarealtoken"
            },
            Ct);

        Assert.Equal(SparkStableBalanceStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Field == nameof(StableBalanceInput.TokenIdentifier));

        Assert.Equal(
            StableBalanceSettings.DefaultTokenIdentifier,
            h.Settings.Settings[StoreId]!.StableBalance.TokenIdentifier);
        Assert.Empty(h.Sdk.StableBalanceCalls);
    }

    #endregion

    #region Activation

    /// <summary>
    /// A valid activation is stored and applied, and the merchant is told it will not be instant.
    /// </summary>
    /// <remarks>
    /// The conversion runs on Spark's own background worker and <b>no event reports it</b>, so a message that
    /// implied the balance had moved would be wrong for as long as the merchant kept looking.
    /// </remarks>
    [Fact]
    public async Task Enabling_stores_the_setting_activates_the_wallet_and_says_it_is_not_immediate()
    {
        var h = Harness();

        var result = await h.Service.SaveAsync(
            StoreId,
            new StableBalanceInput { Enabled = true, DisclosureAcknowledged = true },
            Ct);

        Assert.True(result.Succeeded);
        Assert.True(h.Settings.Settings[StoreId]!.StableBalance.Enabled);

        var call = Assert.Single(h.Live.StableBalanceCalls);
        Assert.True(call.Activate);
        Assert.Equal(StableBalanceSettings.DefaultLabel, call.Label);

        Assert.Contains("background", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not happen immediately", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deactivation is an explicit "off", not an omitted label.
    /// </summary>
    /// <remarks>
    /// Spark's activation field is a three-state optional-of-enum in which <c>null</c> means <em>leave
    /// unchanged</em>. Deactivating by passing null would silently do nothing and leave the merchant's balance
    /// in a stablecoin they had just asked to leave.
    /// </remarks>
    [Fact]
    public async Task Disabling_deactivates_explicitly_rather_than_leaving_it_unchanged()
    {
        var h = Harness();
        h.Sdk.StableBalanceActiveLabel = StableBalanceSettings.DefaultLabel;

        var result = await h.Service.SaveAsync(StoreId, new StableBalanceInput { Enabled = false }, Ct);

        Assert.True(result.Succeeded);
        var call = Assert.Single(h.Live.StableBalanceCalls);
        Assert.False(call.Activate);
        Assert.Null(h.Live.StableBalanceActiveLabel);
    }

    /// <summary>
    /// A save leaves everything else in the settings blob alone.
    /// </summary>
    /// <remarks>
    /// The blob holds the protected mnemonic next to this configuration, and a read-modify-write that
    /// reconstructed the object would destroy the wallet. The same hazard the sweep settings page has.
    /// </remarks>
    [Fact]
    public async Task A_save_does_not_touch_the_seed_or_the_sweep_configuration()
    {
        var h = Harness();
        h.Settings.Settings[StoreId]!.Sweep.MinimumSweepSats = 123_456;
        var seedBefore = h.Settings.Settings[StoreId]!.ProtectedMnemonic;

        await h.Service.SaveAsync(
            StoreId, new StableBalanceInput { Enabled = true, DisclosureAcknowledged = true }, Ct);

        var stored = h.Settings.Settings[StoreId]!;
        Assert.Equal(seedBefore, stored.ProtectedMnemonic);
        Assert.Equal(123_456, stored.Sweep.MinimumSweepSats);
    }

    /// <summary>
    /// A setting Spark refused is still stored, and the failure is reported rather than swallowed.
    /// </summary>
    /// <remarks>
    /// Reported as unavailable rather than as a success, so a merchant is not told their balance is converting
    /// when it is not — and stored, so the disagreement shows up on the page with a way to try again. Rolling
    /// the setting back instead would lose the merchant's intent for no benefit.
    /// </remarks>
    [Fact]
    public async Task An_activation_the_wallet_refuses_is_reported_and_leaves_a_disagreement_to_fix()
    {
        var h = Harness();
        h.Sdk.FailStableBalanceWith = new SdkException.SparkException("@v1=Operator RPC error");

        var result = await h.Service.SaveAsync(
            StoreId, new StableBalanceInput { Enabled = true, DisclosureAcknowledged = true }, Ct);

        Assert.Equal(SparkStableBalanceStatus.Unavailable, result.Status);
        Assert.False(result.Succeeded);
        Assert.True(h.Settings.Settings[StoreId]!.StableBalance.Enabled);

        // And the page now has something to show and a button to press.
        h.Sdk.FailStableBalanceWith = null;
        var view = await h.Service.ReadAsync(StoreId, Ct);
        Assert.True(view.NeedsReapply);
    }

    #endregion

    /// <summary>
    /// A save survives the wallet being reconnected by the write itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The regression this pins made every single save fail in production while the suite stayed green.</b>
    /// Storing settings reconciles the store's running instance with them — the old SDK handle is torn down and
    /// disposed, a fresh one connected — so a handle resolved <em>before</em> the write is dead by the time it
    /// returns. The service captured one, used it afterwards, caught the resulting
    /// <c>ObjectDisposedException</c>, and reported "Spark did not apply it": a wrong diagnosis on a 100%
    /// failure rate, with nothing ever activated.
    /// </para>
    /// <para>
    /// It was invisible because the fake settings store did not model the reconnect. It does now, which is what
    /// gives this test teeth.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_save_applies_to_the_wallet_that_exists_after_the_write_not_the_one_before_it()
    {
        var h = Harness();
        var handleBeforeWrite = h.Runtime.Clients[StoreId];

        var result = await h.Service.SaveAsync(
            StoreId,
            new StableBalanceInput { Enabled = true, DisclosureAcknowledged = true },
            Ct);

        Assert.True(result.Succeeded);

        // The write really did replace the handle, so the test is exercising the hazard rather than asserting
        // past it.
        Assert.NotSame(handleBeforeWrite, h.Runtime.Clients[StoreId]);
        Assert.True(((FakeSparkSdkClient)handleBeforeWrite).Disposed);

        // And the activation landed on the live handle.
        var live = (FakeSparkSdkClient)h.Runtime.Clients[StoreId];
        Assert.Equal(StableBalanceSettings.DefaultLabel, live.StableBalanceActiveLabel);
        Assert.True(Assert.Single(live.StableBalanceCalls).Activate);

        // Nothing was attempted on the dead one.
        Assert.Empty(((FakeSparkSdkClient)handleBeforeWrite).StableBalanceCalls);
    }

    /// <summary>
    /// A wallet that does not come back up after the write is reported as such, not as a refusal.
    /// </summary>
    /// <remarks>
    /// The other side of re-resolving: the handle can legitimately be absent afterwards, and the merchant needs
    /// to be told the setting was stored but not applied — which is the state the page's re-apply button exists
    /// for.
    /// </remarks>
    [Fact]
    public async Task A_wallet_that_does_not_restart_after_the_write_is_reported_and_the_setting_is_kept()
    {
        var h = Harness();
        h.Settings.AlwaysDeclineWith = "the seed could not be decrypted";

        var result = await h.Service.SaveAsync(
            StoreId,
            new StableBalanceInput { Enabled = true, DisclosureAcknowledged = true },
            Ct);

        Assert.Equal(SparkStableBalanceStatus.Unavailable, result.Status);
        Assert.True(h.Settings.Settings[StoreId]!.StableBalance.Enabled);
    }

    /// <summary>
    /// Stable Balance can be switched off again, and on again after that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Mainnet found that it could not.</b> The connect path only declared the stable-balance config when
    /// the <em>setting</em> was enabled, and saving <c>enabled: false</c> persists before it reconnects — so the
    /// wallet came back up with no config at all, and the deactivation that followed threw
    /// <c>Stable balance is not configured</c>. Switching off was unreachable and a merchant's balance was
    /// stranded in USDB with no route back through the plugin.
    /// </para>
    /// <para>
    /// The round trip is the assertion, not just the disable: re-enabling afterwards is what proves the config
    /// is still declared rather than the flag having been shuffled somewhere else.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Stable_balance_can_be_switched_off_again_and_back_on()
    {
        var h = Harness();

        var enabled = await h.Service.SaveAsync(
            StoreId, new StableBalanceInput { Enabled = true, DisclosureAcknowledged = true }, Ct);
        Assert.True(enabled.Succeeded);
        Assert.Equal(StableBalanceSettings.DefaultLabel, h.Live.StableBalanceActiveLabel);

        var disabled = await h.Service.SaveAsync(
            StoreId, new StableBalanceInput { Enabled = false }, Ct);

        Assert.True(disabled.Succeeded, disabled.Message);
        Assert.Null(h.Live.StableBalanceActiveLabel);
        Assert.False(h.Settings.Settings[StoreId]!.StableBalance.Enabled);

        var again = await h.Service.SaveAsync(
            StoreId, new StableBalanceInput { Enabled = true, DisclosureAcknowledged = true }, Ct);

        Assert.True(again.Succeeded, again.Message);
        Assert.Equal(StableBalanceSettings.DefaultLabel, h.Live.StableBalanceActiveLabel);
    }

    /// <summary>
    /// The wallet keeps its stable-balance configuration even when the feature is switched off.
    /// </summary>
    /// <remarks>
    /// The rule underneath the round trip above, stated directly: the config declares which tokens are
    /// <em>available</em>, and the active label decides which is <em>on</em>. Tying the first to the second is
    /// what made deactivation impossible, and the ordering makes it unfixable elsewhere — the state a
    /// deactivation needs must be in the config the reconnect used.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_stable_balance_config_is_declared_whether_or_not_the_feature_is_on(bool enabled)
    {
        var settings = new SparkSettings
        {
            StableBalance = new StableBalanceSettings { Enabled = enabled, DisclosureAcknowledged = true }
        };

        var config = SparkService.BuildStableBalance(settings);

        Assert.NotNull(config);
        Assert.Equal(StableBalanceSettings.DefaultTokenIdentifier, config!.Token.Value);
        Assert.Equal(StableBalanceSettings.DefaultLabel, config.Label);
    }

    /// <summary>
    /// A store upgraded mid-flight repairs itself on the next save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state a merchant is left in by the bug: a wallet connected before the fix, so it is running with no
    /// stable-balance config even though the settings now declare one, and holding USDB it cannot move.
    /// </para>
    /// <para>
    /// Because saving reconnects, and the reconnect now always declares the config, the very write that used to
    /// strand the balance is the one that frees it. That is worth pinning: it means an operator's recovery
    /// procedure is "press save", not "re-provision the store".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_wallet_that_lost_its_config_is_repaired_by_the_next_save()
    {
        var h = Harness();
        h.Settings.Settings[StoreId]!.StableBalance = new StableBalanceSettings
        {
            Enabled = true,
            DisclosureAcknowledged = true
        };

        // A wallet connected before the fix: no config, and holding tokens with no route out.
        h.Sdk.StableBalanceConfigured = false;
        h.Sdk.StableBalanceActiveLabel = null;
        h.Sdk.TokenBalances.Add(new SparkTokenBalance(
            FakeSparkSdkClient.Usdb, 235_824, "USDB", "Bitcoin USD", 6, IsFreezable: true));

        var result = await h.Service.SaveAsync(StoreId, new StableBalanceInput { Enabled = false }, Ct);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(h.Live.StableBalanceConfigured);
        Assert.False(Assert.Single(h.Live.StableBalanceCalls).Activate);
    }

    /// <summary>
    /// Re-applying against a wallet that has lost its config reports the failure rather than claiming success.
    /// </summary>
    /// <remarks>
    /// Re-apply does not write settings, so it does not reconnect — which makes it the one path that still meets
    /// a wallet in the broken state. It must say so: reporting success here would tell a merchant their balance
    /// had been converted back when nothing had happened at all.
    /// </remarks>
    [Fact]
    public async Task Re_applying_against_a_wallet_with_no_config_is_reported_rather_than_claimed()
    {
        var h = Harness();
        h.Sdk.StableBalanceConfigured = false;
        h.Sdk.TokenBalances.Add(new SparkTokenBalance(
            FakeSparkSdkClient.Usdb, 235_824, "USDB", "Bitcoin USD", 6, IsFreezable: true));

        var result = await h.Service.ReapplyAsync(StoreId, Ct);

        Assert.Equal(SparkStableBalanceStatus.Unavailable, result.Status);
        Assert.False(result.Succeeded);
        Assert.Contains("not configured", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    #region The disagreement, which is reported and not reconciled

    /// <summary>
    /// A wallet whose cached state differs from the setting is reported, not silently converted.
    /// </summary>
    /// <remarks>
    /// <b>The disagreement is legitimate.</b> Spark caches the active label per wallet, so a store whose seed
    /// was replaced — or whose storage directory is new — starts deactivated whatever the setting says.
    /// Reconciling on a read would mean converting a merchant's whole balance on a page load.
    /// </remarks>
    [Fact]
    public async Task A_wallet_that_disagrees_with_the_setting_is_reported_rather_than_converted()
    {
        var h = Harness();
        h.Settings.Settings[StoreId]!.StableBalance = new StableBalanceSettings
        {
            Enabled = true,
            DisclosureAcknowledged = true
        };
        // The wallet has no cached label: a replaced seed.
        h.Sdk.StableBalanceActiveLabel = null;

        var view = await h.Service.ReadAsync(StoreId, Ct);

        Assert.True(view.DesiredActive);
        Assert.False(view.ActuallyActive);
        Assert.True(view.NeedsReapply);

        // Reading changed nothing.
        Assert.Empty(h.Sdk.StableBalanceCalls);
    }

    /// <summary>
    /// A wallet holding a stablecoin it reports nothing active for is a disagreement, not agreement.
    /// </summary>
    /// <remarks>
    /// <b>The reading that made a mainnet failure look like success.</b> After the failed deactivation the page
    /// showed <c>desiredActive: false</c>, <c>activeLabel: null</c>, <c>needsReapply: false</c> — setting and
    /// wallet apparently agreeing — while 0.235824 USDB sat in a wallet with no configuration to move it. "Off"
    /// and "off" says nothing about the money being stranded.
    /// </remarks>
    [Fact]
    public async Task Holding_a_token_the_wallet_reports_nothing_active_for_is_reported_as_a_disagreement()
    {
        var h = Harness();
        h.Sdk.StableBalanceActiveLabel = null;
        h.Sdk.StableBalanceConfigured = false;
        h.Sdk.TokenBalances.Add(new SparkTokenBalance(
            FakeSparkSdkClient.Usdb, 235_824, "USDB", "Bitcoin USD", 6, IsFreezable: true));

        var view = await h.Service.ReadAsync(StoreId, Ct);

        Assert.False(view.DesiredActive);
        Assert.False(view.ActuallyActive);
        Assert.True(view.HoldingUnmanagedBalance);
        Assert.True(view.NeedsReapply);
    }

    /// <summary>
    /// A wallet holding nothing and reporting nothing does agree, so the check above is not a blanket alarm.
    /// </summary>
    [Fact]
    public async Task An_empty_wallet_that_is_off_agrees_with_a_setting_that_is_off()
    {
        var h = Harness();

        var view = await h.Service.ReadAsync(StoreId, Ct);

        Assert.False(view.NeedsReapply);
        Assert.False(view.HoldingUnmanagedBalance);
    }

    /// <summary>
    /// A wallet whose state could not be read is not a wallet that agrees.
    /// </summary>
    [Fact]
    public async Task A_wallet_whose_state_cannot_be_read_never_reads_as_agreement()
    {
        var h = Harness();
        h.Sdk.FailUserSettingsWith = new SdkException.SparkException("@v1=Operator RPC error");

        var view = await h.Service.ReadAsync(StoreId, Ct);

        Assert.True(view.ActiveStateUnknown);
        Assert.True(view.NeedsReapply);
    }

    /// <summary>
    /// Re-applying converges them, and only when asked.
    /// </summary>
    [Fact]
    public async Task Re_applying_converges_the_wallet_with_the_setting()
    {
        var h = Harness();
        h.Settings.Settings[StoreId]!.StableBalance = new StableBalanceSettings
        {
            Enabled = true,
            DisclosureAcknowledged = true
        };

        var result = await h.Service.ReapplyAsync(StoreId, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(StableBalanceSettings.DefaultLabel, h.Sdk.StableBalanceActiveLabel);
        Assert.False((await h.Service.ReadAsync(StoreId, Ct)).NeedsReapply);
    }

    /// <summary>
    /// A store still holding a stablecoin after switching off is said to be doing so.
    /// </summary>
    /// <remarks>
    /// The conversion back runs in the background with nothing to report when it completes, so between the
    /// switch and the conversion the store genuinely holds dollars it has asked not to. Silence there reads as a
    /// bug.
    /// </remarks>
    [Fact]
    public async Task A_balance_still_held_after_switching_off_is_surfaced()
    {
        var h = Harness();
        h.Sdk.TokenBalances.Add(new SparkTokenBalance(
            FakeSparkSdkClient.Usdb, 35_600_000, "USDB", "Bitcoin USD", 6, IsFreezable: true));

        var view = await h.Service.ReadAsync(StoreId, Ct);

        Assert.False(view.DesiredActive);
        Assert.True(view.HoldingAfterDisable);
        Assert.Equal("35.6 USDB", view.Balance!.Describe());
        Assert.True(view.Balance.IsFreezable);
    }

    /// <summary>
    /// The conversion floor is reported so a threshold that can never fire is visible.
    /// </summary>
    /// <remarks>
    /// Spark clamps a configured threshold <em>upward</em> to its own minimum rather than honouring it, so a
    /// merchant who sets a small number has not done what the field appears to say.
    /// </remarks>
    [Fact]
    public async Task The_services_own_conversion_floor_is_reported()
    {
        var h = Harness();
        h.Sdk.ConversionMinimumFromBitcoinSats = 800;

        Assert.Equal(800, (await h.Service.ReadAsync(StoreId, Ct)).ConversionMinimumSats);
    }

    /// <summary>
    /// A wallet that cannot be read reports the error rather than a confidently wrong state.
    /// </summary>
    [Fact]
    public async Task A_wallet_that_cannot_be_read_reports_why()
    {
        var h = Harness();
        h.Sdk.FailUserSettingsWith = new SdkException.SparkException("@v1=Operator RPC error");

        var view = await h.Service.ReadAsync(StoreId, Ct);

        Assert.NotNull(view.WalletError);
        Assert.Null(view.ActiveLabel);
    }

    #endregion

    #region Configuration

    /// <summary>
    /// The wallet is configured with the store's token, and never with an activation state.
    /// </summary>
    /// <remarks>
    /// <c>defaultActiveLabel</c> seeds the first run only and is then overridden forever by whatever the wallet
    /// has cached, so driving activation from the config would work exactly once and then silently stop.
    /// Activation goes through the user-settings call instead, which is why the config here carries a token list
    /// and nothing else.
    /// </remarks>
    [Fact]
    public void The_wallet_config_carries_the_token_but_never_the_activation_state()
    {
        var applied = SparkSdkClientFactory.ApplyPostMvpConfig(
            BreezSdkSparkMethods.DefaultConfig(Network.Mainnet),
            new SparkConnectOptions(
                StoreId, "mnemonic", null, "key", Network.Mainnet,
                new SparkDepositSettings().ToMaxFee(),
                new SparkStableBalanceConfiguration(
                    FakeSparkSdkClient.Usdb, "USDB", StableBalanceSettings.DefaultMaxSlippageBps, null)),
            NullLogger.Instance);

        var config = Assert.IsType<StableBalanceConfig>(applied.stableBalanceConfig);
        var token = Assert.Single(config.tokens);
        Assert.Equal(StableBalanceSettings.DefaultTokenIdentifier, token.tokenIdentifier);
        Assert.Equal("USDB", token.label);
        Assert.Equal(StableBalanceSettings.DefaultMaxSlippageBps, config.maxSlippageBps);

        Assert.Null(config.defaultActiveLabel);
    }

    /// <summary>
    /// Stable Balance is not configured off mainnet, where it would be accepted and never work.
    /// </summary>
    [Fact]
    public void The_wallet_config_omits_stable_balance_off_mainnet()
    {
        var applied = SparkSdkClientFactory.ApplyPostMvpConfig(
            BreezSdkSparkMethods.DefaultConfig(Network.Regtest),
            new SparkConnectOptions(
                StoreId, "mnemonic", null, "key", Network.Regtest,
                new SparkDepositSettings().ToMaxFee(),
                new SparkStableBalanceConfiguration(FakeSparkSdkClient.Usdb, "USDB", 10, null)),
            NullLogger.Instance);

        Assert.Null(applied.stableBalanceConfig);
    }

    /// <summary>
    /// Cross-chain is configured on every mainnet connect, and on no other network.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Set unconditionally on mainnet, whether or not any store sweeps cross-chain</b>, because leaving it
    /// null does not disable the feature cleanly — it makes the route query return an empty array with no error,
    /// which reads as "no route to this chain" and sends a merchant off trying every chain there is.
    /// </para>
    /// <para>
    /// And absent everywhere else, because a regtest connect carrying it throws — which would stop the store's
    /// wallet starting at all.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(Network.Mainnet, true)]
    [InlineData(Network.Regtest, false)]
    public void Cross_chain_is_configured_exactly_where_it_can_work(Network network, bool expected)
    {
        var applied = SparkSdkClientFactory.ApplyPostMvpConfig(
            BreezSdkSparkMethods.DefaultConfig(network),
            new SparkConnectOptions(
                StoreId, "mnemonic", null, "key", network, new SparkDepositSettings().ToMaxFee()),
            NullLogger.Instance);

        Assert.Equal(expected, applied.crossChainConfig is not null);

        if (applied.crossChainConfig is { } crossChain)
        {
            // Set explicitly, not inherited: Spark's fallback here is 100 bps, ten times looser.
            Assert.Equal(SweepSettings.DefaultCrossChainSlippageBps, crossChain.defaultSlippageBps);
        }
    }

    /// <summary>
    /// Neither feature is configured onto a wallet whose background tasks are off.
    /// </summary>
    /// <remarks>
    /// <b>Asserted rather than assumed, because the cost of being wrong lands on a working store.</b> Setting
    /// either with background tasks disabled is a hard init failure — the connect throws, so the store has no
    /// Spark wallet at all and its Lightning goes down with it. It is true today only because this method is
    /// handed a client-mode config, which is a property of a call site rather than a guarantee.
    /// </remarks>
    [Fact]
    public void Neither_feature_is_configured_onto_a_wallet_that_cannot_run_it()
    {
        // Server mode: the one difference that matters is backgroundTasksEnabled = false.
        var serverMode = BreezSdkSparkMethods.DefaultServerConfig(Network.Mainnet);
        Assert.False(serverMode.backgroundTasksEnabled);

        var thrown = Assert.Throws<SparkBackgroundTasksRequiredException>(
            () => SparkSdkClientFactory.ApplyPostMvpConfig(
                serverMode,
                new SparkConnectOptions(
                    StoreId, "mnemonic", null, "key", Network.Mainnet,
                    new SparkDepositSettings().ToMaxFee(),
                    new SparkStableBalanceConfiguration(FakeSparkSdkClient.Usdb, "USDB", 10, null)),
                NullLogger.Instance));

        Assert.Equal("Stable Balance", thrown.Feature);

        // And the plugin's own client-mode config does not trip it, so the guard is not simply always on.
        var clientMode = SparkSdkClientFactory.ApplyPostMvpConfig(
            BreezSdkSparkMethods.DefaultConfig(Network.Mainnet),
            new SparkConnectOptions(
                StoreId, "mnemonic", null, "key", Network.Mainnet,
                new SparkDepositSettings().ToMaxFee(),
                new SparkStableBalanceConfiguration(FakeSparkSdkClient.Usdb, "USDB", 10, null)),
            NullLogger.Instance);

        Assert.NotNull(clientMode.stableBalanceConfig);
    }

    #endregion

    private sealed record TestHarness(
        SparkStableBalanceService Service,
        FakeSparkSdkClient Sdk,
        FakeSparkStoreSettingsStore Settings,
        FakeSparkStoreRuntime Runtime)
    {
        /// <summary>
        /// The handle that is live <em>now</em>.
        /// </summary>
        /// <remarks>
        /// Not the same object as <see cref="Sdk"/> once a write has happened: storing settings reconnects the
        /// wallet. A test asserting on what the wallet ended up doing has to read this one, which is the whole
        /// point of modelling the reconnect at all.
        /// </remarks>
        public FakeSparkSdkClient Live => (FakeSparkSdkClient)Runtime.Clients[StoreId];
    }

    private static TestHarness Harness(bool mainnet = true)
    {
        var sdk = new FakeSparkSdkClient();
        var runtime = new FakeSparkStoreRuntime();

        // Wired to the runtime, so a write reconnects the wallet exactly as SparkService.Set does.
        var settings = new FakeSparkStoreSettingsStore(runtime: runtime);

        settings.Settings[StoreId] = new SparkSettings
        {
            ProtectedMnemonic = "protected",
            PaymentKey = "key"
        };

        runtime.Clients[StoreId] = sdk;

        return new TestHarness(
            new SparkStableBalanceService(
                settings, runtime, mainnet, NullLogger<SparkStableBalanceService>.Instance),
            sdk,
            settings,
            runtime);
    }
}
