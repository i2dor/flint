using System.Security.Claims;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Flint.Controllers;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using BTCPayServer.Services;
using NBitcoin;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Every action must act on the store BTCPay authorised, not on a store id the request supplied.
/// </summary>
/// <remarks>
/// <para>
/// A regression suite for a real cross-store hole, so it is worth being precise about the mechanism it
/// reproduces. BTCPay's authorisation handler reads the store id from <b>route</b> data. ASP.NET Core model
/// binding, by default, prefers <b>form</b> values over route values. An action whose <c>string storeId</c>
/// parameter had no binding source therefore received the attacker's form value while having been authorised
/// against their own store — so <c>POST /plugins/attacker/spark/setup</c> with a body of
/// <c>storeId=victim&amp;SeedSource=Imported&amp;ImportedMnemonic=…</c> provisioned the <em>victim</em> store
/// with the attacker's seed, and the victim's Lightning invoices were then minted into the attacker's wallet.
/// The same shape destroyed a victim's settings through <c>/remove</c> and overwrote a victim's node
/// configuration through <c>/status/enable-lightning</c>.
/// </para>
/// <para>
/// A unit test cannot drive ASP.NET Core's binder, so it reproduces the <em>outcome</em> of that binding
/// directly: the authorised store in <c>HttpContext</c> is the attacker's, and the value handed to the action is
/// the victim's. That is exactly the state the framework produced, and it is the state the guard has to reject.
/// Authorisation is faked as <em>succeeding</em> throughout, because a fake that refused would make these tests
/// pass for the wrong reason. Verified by mutation: reverting the guard fails six of them.
/// </para>
/// </remarks>
public class SparkControllerStoreScopeTests
{
    private const string AttackerStore = "attacker-store";
    private const string VictimStore = "victim-store";

    private const string AttackerMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private const string VictimNode = "type=lnd-rest;server=https://10.0.0.5:8080/;macaroon=deadbeef";
    private const string VictimPaymentKey = "1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff";

    /// <summary>
    /// The shared harness: both surfaces over one service graph, authorised for <see cref="AttackerStore"/>.
    /// </summary>
    /// <remarks>
    /// Authorisation is faked as <em>succeeding</em> throughout, because a fake that refused would make these
    /// tests pass for the wrong reason. Verified by mutation: reverting the guard fails them.
    /// </remarks>
    private static SparkSurfaceHarness CreateHarness(
        bool configureAttackerStore = false,
        bool mainnet = false) =>
        SparkSurfaceHarness.Create(configureAttackerStore: configureAttackerStore, mainnet: mainnet);

    [Fact]
    public async Task Setup_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = CreateHarness();

        var result = await h.Mvc.Setup(
            VictimStore,
            new SparkSetupViewModel
            {
                SeedSource = SeedSource.Imported,
                ImportedMnemonic = AttackerMnemonic
            },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);

        // The whole point: the victim keeps its own seed and its own node.
        Assert.Equal("victim-protected", h.Settings.Settings[VictimStore]!.ProtectedMnemonic);
        Assert.Equal(VictimPaymentKey, h.Settings.Settings[VictimStore]!.PaymentKey);
        Assert.Equal(VictimNode, h.Lightning.Stores[VictimStore].ConnectionString);
        Assert.Empty(h.Settings.Writes);
        Assert.Empty(h.Lightning.Writes);

        // And nothing even looked at the victim's on-chain wallet on the way through.
        Assert.Empty(h.SeedReader.Reads);
    }

    [Fact]
    public async Task Remove_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = CreateHarness();

        var result = await h.Mvc.RemoveConfirmed(VictimStore);

        Assert.IsType<NotFoundResult>(result);
        Assert.NotNull(h.Settings.Settings[VictimStore]);
        Assert.Empty(h.Settings.Writes);
        Assert.Equal(VictimNode, h.Lightning.Stores[VictimStore].ConnectionString);
    }

    [Fact]
    public async Task EnableLightning_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = CreateHarness();

        // confirmed: true, so the confirmation step cannot be what saves the victim here.
        var result = await h.Mvc.EnableLightning(VictimStore, confirmed: true, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(VictimNode, h.Lightning.Stores[VictimStore].ConnectionString);
        Assert.Empty(h.Lightning.Writes);
    }

    [Fact]
    public async Task Status_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = CreateHarness();

        // A read, but a read of another store's wallet balance and Spark identity all the same.
        Assert.IsType<NotFoundResult>(await h.Mvc.Status(VictimStore, CancellationToken.None));
    }

    [Fact]
    public async Task Remove_confirmation_page_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = CreateHarness();

        Assert.IsType<NotFoundResult>(await h.Mvc.Remove(VictimStore, CancellationToken.None));
    }

    [Fact]
    public async Task Index_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = CreateHarness();

        Assert.IsType<NotFoundResult>(await h.Mvc.Index(VictimStore));
    }

    [Fact]
    public async Task Setup_refuses_when_no_store_was_authorised_at_all()
    {
        var h = CreateHarness();
        h.Mvc.HttpContext.SetStoreData(null);

        Assert.IsType<NotFoundResult>(
            await h.Mvc.Setup(AttackerStore, new SparkSetupViewModel(), CancellationToken.None));
        Assert.Empty(h.Settings.Writes);
    }

    [Fact]
    public async Task Setup_proceeds_for_the_authorised_store()
    {
        // The counterpart the refusals need: the guard has to let a legitimate request through, or every test
        // above would pass against a controller that simply rejected everything.
        var h = CreateHarness();

        var result = await h.Mvc.Setup(
            AttackerStore,
            new SparkSetupViewModel
            {
                SeedSource = SeedSource.Imported,
                ImportedMnemonic = AttackerMnemonic
            },
            CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SparkController.Status), redirect.ActionName);
        Assert.NotNull(h.Settings.Settings[AttackerStore]);
        Assert.Equal(AttackerStore, Assert.Single(h.Lightning.Writes).StoreId);
    }

    [Fact]
    public async Task Setup_turns_sweeping_on_when_the_merchant_asked_for_it()
    {
        var h = CreateHarness();

        var result = await h.Mvc.Setup(
            AttackerStore,
            new SparkSetupViewModel
            {
                SeedSource = SeedSource.Imported,
                ImportedMnemonic = AttackerMnemonic,
                EnableSweeping = true,
                SweepBalanceThresholdSats = 750_000
            },
            CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);

        // The whole point: the merchant leaves setup with sweeping already configured, not with a promise that
        // they can configure it on another page.
        var settings = h.Settings.Settings[AttackerStore];
        Assert.True(settings.Sweep.Enabled);
        Assert.Equal(750_000, settings.Sweep.BalanceThresholdSats);
    }

    [Fact]
    public async Task Setup_still_succeeds_when_sweeping_cannot_be_turned_on_and_says_why()
    {
        // Sweeping defaults to the store's own on-chain wallet, and a store may not have one. That must not
        // unwind a Lightning wallet that provisioned fine -- but silence would be worse than either failure
        // mode: a merchant who ticked the box and read only "Spark is now set up" would believe their balance
        // is being swept when nothing is.
        var h = CreateHarness();
        h.SweepAddresses.Result = SweepAddressResult.NoWallet("This store has no Bitcoin wallet to sweep into.");

        var result = await h.Mvc.Setup(
            AttackerStore,
            new SparkSetupViewModel
            {
                SeedSource = SeedSource.Imported,
                ImportedMnemonic = AttackerMnemonic,
                EnableSweeping = true
            },
            CancellationToken.None);

        // Setup itself succeeded.
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SparkController.Status), redirect.ActionName);
        Assert.NotNull(h.Settings.Settings[AttackerStore]);

        // Sweeping did not, and the merchant is told.
        Assert.False(h.Settings.Settings[AttackerStore].Sweep.Enabled);
        var message = Assert.IsType<string>(h.Mvc.TempData["SuccessMessage"]);
        Assert.Contains("Sweeping was not turned on", message);
    }

    [Fact]
    public async Task The_view_model_is_rebuilt_against_the_authorised_store()
    {
        // StoreId is [BindNever], but the model is also a rendering input for the form's action URL, so a
        // re-render must carry the authorised store and not one the request supplied.
        var h = CreateHarness();

        var result = await h.Mvc.Setup(
            AttackerStore,
            new SparkSetupViewModel
            {
                StoreId = VictimStore,
                SeedSource = SeedSource.Imported,
                ImportedMnemonic = "not a valid phrase at all"
            },
            CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<SparkSetupViewModel>(view.Model);
        Assert.Equal(AttackerStore, vm.StoreId);
    }

    [Fact]
    public async Task EnableLightning_asks_before_replacing_another_node()
    {
        // W3-M4: the warning on the status page is rendered by a GET, and a POST is reachable without it. One
        // click must not discard an LND connection string carrying macaroon material.
        var h = CreateHarness();
        h.Settings.Settings[AttackerStore] = new SparkSettings
        {
            ProtectedMnemonic = "protected",
            PaymentKey = VictimPaymentKey
        };
        h.Lightning.Add(AttackerStore, "type=lnd-rest;server=https://127.0.0.1:8080/;macaroon=abcdef");

        var result = await h.Mvc.EnableLightning(AttackerStore, confirmed: false, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Confirm", view.ViewName);
        Assert.Empty(h.Lightning.Writes);
        Assert.Equal(
            "type=lnd-rest;server=https://127.0.0.1:8080/;macaroon=abcdef",
            h.Lightning.Stores[AttackerStore].ConnectionString);
    }

    [Fact]
    public async Task Sweep_settings_page_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = CreateHarness();

        // A read, but a read of another store's balance, sweep history and destination configuration.
        Assert.IsType<NotFoundResult>(
            await h.Mvc.Sweep(VictimStore, 0, 25, CancellationToken.None));
    }

    [Fact]
    public async Task Saving_sweep_settings_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = CreateHarness();

        var result = await h.Mvc.Sweep(
            VictimStore,
            new SparkSweepViewModel
            {
                Settings = new SweepSettingsInput
                {
                    Enabled = true,
                    DestinationMode = SweepDestinationMode.StaticAddress,
                    // The attacker's own address. Accepting this would point the victim's auto-sweep at it.
                    StaticAddress = FakeSweepAddressSource.RegtestAddresses[2]
                }
            },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(h.Settings.Writes);
        // The victim's sweep configuration is untouched: still off, still pointed at its own wallet.
        var victim = h.Settings.Settings[VictimStore]!;
        Assert.False(victim.Sweep.Enabled);
        Assert.Equal(SweepDestinationMode.StoreWallet, victim.Sweep.DestinationMode);
        Assert.Null(victim.Sweep.StaticAddress);
    }

    [Fact]
    public async Task Previewing_a_sweep_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = CreateHarness();

        Assert.IsType<NotFoundResult>(
            await h.Mvc.SweepPreview(VictimStore, CancellationToken.None));
        // Not even a quote against the victim's wallet, which would leak its balance into the rendered page.
        Assert.Empty(h.SweepAddresses.Calls);
    }

    [Fact]
    public async Task Sweeping_now_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        // The sharpest one in this file: this action moves money.
        var h = CreateHarness();

        var result = await h.Mvc.SweepNow(VictimStore, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(h.SweepRecords.Records);
        Assert.Equal(5_000_000, ((FakeSparkSdkClient)h.Runtime.Clients[VictimStore]).BalanceSats);
        Assert.Empty(((FakeSparkSdkClient)h.Runtime.Clients[VictimStore]).OnchainSendCalls);
    }

    #region Wave 7 pages

    [Fact]
    public async Task The_deposit_page_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = CreateHarness();

        Assert.IsType<NotFoundResult>(await h.Mvc.Deposit(VictimStore, CancellationToken.None));
    }

    [Fact]
    public async Task Claiming_a_deposit_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        // A money-moving action: a claim spends the victim's deposit on a fee.
        var h = CreateHarness();
        var victim = (FakeSparkSdkClient)h.Runtime.Clients[VictimStore];
        victim.UnclaimedDeposits.Add(new SparkDepositInfo(
            "8808985e78ad465c25727d5ad749f60a5787855d4f1ddffebfc4afb4dbde1b37", 0, 60_000, IsMature: true,
            new SparkDepositClaimFailure(SparkDepositClaimFailureKind.MaxFeeExceeded, "too dear", 420)));

        var result = await h.Mvc.ClaimDeposit(
            VictimStore, "8808985e78ad465c25727d5ad749f60a5787855d4f1ddffebfc4afb4dbde1b37", 0, null,
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(victim.ClaimCalls);
        Assert.Single(victim.UnclaimedDeposits);
    }

    [Fact]
    public async Task The_stable_balance_page_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = CreateHarness();

        Assert.IsType<NotFoundResult>(await h.Mvc.StableBalance(VictimStore, CancellationToken.None));
    }

    [Fact]
    public async Task Saving_stable_balance_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        // Enabling this converts the victim's entire balance into a stablecoin.
        var h = CreateHarness(mainnet: true);
        var victim = (FakeSparkSdkClient)h.Runtime.Clients[VictimStore];

        var result = await h.Mvc.StableBalance(
            VictimStore,
            new SparkStableBalanceViewModel
            {
                Settings = new StableBalanceInput { Enabled = true, DisclosureAcknowledged = true }
            },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(h.Settings.Writes);
        Assert.False(h.Settings.Settings[VictimStore]!.StableBalance.Enabled);
        Assert.Empty(victim.StableBalanceCalls);
    }

    [Fact]
    public async Task Re_applying_stable_balance_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = CreateHarness(mainnet: true);
        h.Settings.Settings[VictimStore]!.StableBalance = new StableBalanceSettings
        {
            Enabled = true,
            DisclosureAcknowledged = true
        };
        var victim = (FakeSparkSdkClient)h.Runtime.Clients[VictimStore];

        var result = await h.Mvc.ReapplyStableBalance(VictimStore, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(victim.StableBalanceCalls);
        Assert.Null(victim.StableBalanceActiveLabel);
    }

    /// <summary>
    /// The counterparts the four refusals above need.
    /// </summary>
    /// <remarks>
    /// Without them every assertion in this region would pass against a controller that rejected everything —
    /// which is the failure mode a store-scope suite is most prone to, because refusing is the safe answer to
    /// each individual test.
    /// </remarks>
    [Fact]
    public async Task The_new_pages_proceed_for_the_authorised_store()
    {
        var h = CreateHarness(configureAttackerStore: true, mainnet: true);

        Assert.IsType<ViewResult>(await h.Mvc.Deposit(AttackerStore, CancellationToken.None));
        Assert.IsType<ViewResult>(await h.Mvc.StableBalance(AttackerStore, CancellationToken.None));

        var saved = await h.Mvc.StableBalance(
            AttackerStore,
            new SparkStableBalanceViewModel
            {
                Settings = new StableBalanceInput { Enabled = true, DisclosureAcknowledged = true }
            },
            CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(saved);
        Assert.True(h.Settings.Settings[AttackerStore]!.StableBalance.Enabled);
        Assert.Single(((FakeSparkSdkClient)h.Runtime.Clients[AttackerStore]).StableBalanceCalls);

        // And the victim was untouched throughout.
        Assert.False(h.Settings.Settings[VictimStore]!.StableBalance.Enabled);
    }

    #endregion

    [Fact]
    public async Task Sweeping_now_proceeds_for_the_authorised_store()
    {
        // The counterpart the refusals need. Without it every assertion above would pass against a controller that
        // simply rejected everything.
        var h = CreateHarness();
        h.Settings.Settings[AttackerStore] = new SparkSettings
        {
            ProtectedMnemonic = "protected",
            PaymentKey = VictimPaymentKey,
            Sweep = new SweepSettings { Enabled = true }
        };

        var result = await h.Mvc.SweepNow(AttackerStore, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        var record = Assert.Single(h.SweepRecords.Records).Value;
        Assert.Equal(AttackerStore, record.StoreId);
        Assert.Equal(SweepTrigger.Manual, record.Trigger);
        // And the victim's wallet was never touched on the way through.
        Assert.Equal(5_000_000, ((FakeSparkSdkClient)h.Runtime.Clients[VictimStore]).BalanceSats);
    }

    [Fact]
    public async Task Saving_sweep_settings_leaves_the_stored_seed_alone()
    {
        // The settings blob holds the protected mnemonic alongside the sweep configuration, and this page must
        // never see or rewrite it. A read-modify-write that reconstructed the object would silently destroy the
        // store's wallet.
        var h = CreateHarness();
        h.Settings.Settings[AttackerStore] = new SparkSettings
        {
            ProtectedMnemonic = "attacker-protected",
            PaymentKey = VictimPaymentKey,
            SeedSource = SeedSource.HotWallet,
            ApiKeyOverride = "merchant-key"
        };

        var result = await h.Mvc.Sweep(
            AttackerStore,
            new SparkSweepViewModel
            {
                Settings = new SweepSettingsInput
                {
                    Enabled = true,
                    BalanceThresholdSats = 300_000,
                    MinimumSweepSats = 100_000,
                    MaxFeePercent = 2.5
                }
            },
            CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        var saved = h.Settings.Settings[AttackerStore]!;
        Assert.Equal("attacker-protected", saved.ProtectedMnemonic);
        Assert.Equal(VictimPaymentKey, saved.PaymentKey);
        Assert.Equal(SeedSource.HotWallet, saved.SeedSource);
        Assert.Equal("merchant-key", saved.ApiKeyOverride);
        Assert.True(saved.Sweep.Enabled);
        Assert.Equal(300_000, saved.Sweep.BalanceThresholdSats);
        Assert.Equal(2.5, saved.Sweep.MaxFeePercent);
    }

    [Fact]
    public async Task Saving_an_invalid_sweep_configuration_changes_nothing()
    {
        var h = CreateHarness();
        h.Settings.Settings[AttackerStore] = new SparkSettings
        {
            ProtectedMnemonic = "protected",
            PaymentKey = VictimPaymentKey,
            Sweep = new SweepSettings { Enabled = true, BalanceThresholdSats = 400_000 }
        };

        var result = await h.Mvc.Sweep(
            AttackerStore,
            new SparkSweepViewModel
            {
                Settings = new SweepSettingsInput
                {
                    Enabled = true,
                    DestinationMode = SweepDestinationMode.StaticAddress,
                    // A mainnet address on a regtest server.
                    StaticAddress = "bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq"
                }
            },
            CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Empty(h.Settings.Writes);
        Assert.Equal(400_000, h.Settings.Settings[AttackerStore]!.Sweep.BalanceThresholdSats);
    }

    [Fact]
    public async Task EnableLightning_needs_no_confirmation_to_re_enable_its_own_configuration()
    {
        // The common case — Lightning switched off, or a stale key — must stay one click.
        var h = CreateHarness();
        h.Settings.Settings[AttackerStore] = new SparkSettings
        {
            ProtectedMnemonic = "protected",
            PaymentKey = VictimPaymentKey
        };
        h.Lightning.Add(
            AttackerStore, SparkConnectionString.Format(AttackerStore, VictimPaymentKey), enabled: false);

        var result = await h.Mvc.EnableLightning(AttackerStore, confirmed: false, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Single(h.Lightning.Writes);
    }

    /// <summary>
    /// No view model in this plugin has an inbound store id, whether or not an action binds it today.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule the whole class exists for, stated once against every model rather than one action at a time.
    /// An external audit noticed that two of these models were missing the attribute and called it cosmetic,
    /// which it was — neither is an action parameter. It is also exactly how the original hole got in: a model
    /// nobody binds today becomes a model somebody binds tomorrow, and the property that made it dangerous is
    /// invisible at the callsite that adds the binding.
    /// </para>
    /// <para>
    /// <b>Consistency is the guard.</b> "Every store id on a view model is <c>[BindNever]</c>" is a rule this
    /// test can check; "this one happens not to be reachable" is a fact that has to be re-established by hand
    /// every time an action is added.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_view_model_exposes_a_bindable_store_id()
    {
        var models = typeof(SparkStatusViewModel).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Namespace == typeof(SparkStatusViewModel).Namespace
                        && t.Name.EndsWith("ViewModel", StringComparison.Ordinal))
            .ToList();

        // The reflection has to find something, or the assertion below is vacuous.
        Assert.Contains(typeof(SparkStatusViewModel), models);
        Assert.Contains(typeof(SparkSweepConfirmViewModel), models);

        foreach (var model in models)
        {
            var storeId = model.GetProperty("StoreId");
            if (storeId is null)
                continue;

            Assert.True(
                storeId.GetCustomAttributes(typeof(BindNeverAttribute), inherit: true).Length > 0,
                $"{model.Name}.StoreId is bindable. BTCPay authorises from route data and model binding "
                + "prefers form values, so a store id that can arrive in a body is a store the caller was "
                + "never authorised for.");
        }
    }

    /// <summary>
    /// The controller-level response-cache refusal is load-bearing, and nothing but this test notices
    /// when it is dropped.
    /// </summary>
    /// <remarks>
    /// The setup page deliberately re-renders a rejected import with the recovery phrase just typed, so a
    /// cached page would park a mnemonic on browser or proxy machinery outside the session that typed it.
    /// MVC does not fail if the attribute disappears — every page keeps working — so the only thing
    /// between a future refactor and that leak is this assertion. It checks the class, not the actions:
    /// the attribute is stated once for the controller, which is also how it outlives per-action churn.
    /// </remarks>
    [Fact]
    public void The_controller_refuses_response_caching_at_the_class_level()
    {
        var cache = typeof(SparkController)
            .GetCustomAttributes(typeof(ResponseCacheAttribute), inherit: false)
            .Single();

        Assert.True(
            cache is ResponseCacheAttribute { NoStore: true, Location: ResponseCacheLocation.None },
            "SparkController must carry [ResponseCache(NoStore = true, Location = None)] at class "
            + "level: the setup page re-renders a rejected import with the phrase just typed, and a "
            + "cached response would keep that mnemonic outside the session that typed it.");
    }
}
