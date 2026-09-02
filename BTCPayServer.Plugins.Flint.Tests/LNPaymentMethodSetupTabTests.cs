using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The setup-tab extension point resolves the store it reports on from the request's authorisation —
/// never from the store the bound model names.
/// </summary>
/// <remarks>
/// <para>
/// <c>LightningNodeViewModel.StoreId</c> is an ordinary form-bound settable property, and core's POST of
/// <c>SetupLightningNode</c> re-renders the page without ever reassigning it from the route or the
/// authorised store, so the model reaching this partial on a POST can name any store on the server. The
/// pane reports two pieces of store state — whether the store's Spark wallet is configured, and whether
/// it is running — so a partial that keyed off the model would hand an attacker a free oracle for
/// another store's wallet state, links and all. The partial instead refuses only a missing authorisation
/// and computes both states, and every link, off the authorised id alone; the form-bound id is never
/// trusted on any path.
/// </para>
/// <para>
/// These tests render the real compiled view through <see cref="SetupTabViewRenderer"/> — same discovery,
/// fakes and authorisation channel as the tabhead's tests — and assert on rendered output only. A
/// regression that resolved off the model would render the wallet store's configured copy and its status
/// links, which is exactly what the mismatch case asserts against; the render case holds the resolution
/// honest the other way, so the guard cannot pass by simply never rendering anything. The not-running
/// case exists because "running" is the half of the pane's state that a model-keyed lookup could leak
/// even when the connection string itself stayed hidden.
/// </para>
/// </remarks>
public class LNPaymentMethodSetupTabTests
{
    /// <summary>The store whose Spark wallet exists and would be reported on if consulted.</summary>
    private const string WalletStore = "store-with-wallet";

    /// <summary>A different, wallet-less store, used as the authorised one in the mismatch case.</summary>
    private const string AuthorisedStore = "store-authorised";

    /// <summary>A payment key shaped like a generated one, distinctive so its presence in output is unmistakable.</summary>
    private const string PaymentKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>The configured copy, distinctive to the pane's else-branch.</summary>
    private const string ConfiguredCopy = "Saving with";

    /// <summary>The warning distinctive to the pane's not-running branch.</summary>
    private const string NotRunningWarning = "not running at the moment";

    [Fact]
    public async Task A_request_carrying_no_authorised_store_renders_nothing()
    {
        using var harness = SparkServiceHarness.Create();
        harness.SeedStore(WalletStore, SparkServiceHarness.MnemonicFor(1), PaymentKey);
        SetupTabViewRenderer.StartService(harness);

        // Nothing was authorised onto the request: the guard must fail closed rather than trust the form.
        var html = await SetupTabViewRenderer.RenderTabAsync(harness.Service, authorisedStoreId: null, WalletStore);

        Assert.DoesNotContain(WalletStore, html);
        Assert.Equal(string.Empty, html.Trim());
    }

    [Fact]
    public async Task A_model_naming_another_store_discloses_neither_its_configuration_nor_its_running_state()
    {
        using var harness = SparkServiceHarness.Create();
        harness.SeedStore(WalletStore, SparkServiceHarness.MnemonicFor(2), PaymentKey);
        SetupTabViewRenderer.StartService(harness);

        // The attack shape: authorisation succeeded for the wallet-less AuthorisedStore, the form-bound
        // model names WalletStore. The pane's two state questions must be asked of the authorised store
        // alone — so neither the wallet store's configured copy nor its running state may appear, and
        // its id may not ride into a generated link.
        var html = await SetupTabViewRenderer.RenderTabAsync(harness.Service, authorisedStoreId: AuthorisedStore, WalletStore);

        Assert.DoesNotContain(WalletStore, html);
        Assert.DoesNotContain(PaymentKey, html);
        Assert.DoesNotContain(ConfiguredCopy, html);
        Assert.DoesNotContain(NotRunningWarning, html);
        // What does render is the authorised store's own not-configured pane, with its setup link keyed
        // off the authorised id — proof the partial resolved the request's store, not a blanket nothing.
        Assert.Contains("This store has no Spark wallet yet", html);
        Assert.Contains($"/Spark/Setup/{AuthorisedStore}", html);
    }

    [Fact]
    public async Task The_authorised_store_with_a_running_wallet_gets_the_full_pane_rendered()
    {
        using var harness = SparkServiceHarness.Create();
        harness.SeedStore(WalletStore, SparkServiceHarness.MnemonicFor(3), PaymentKey);
        SetupTabViewRenderer.StartService(harness);

        // The legitimate flow — authorised store and model agree on a store whose wallet is up: the
        // configured copy and the running store's own status links, with no warning and, as the pane has
        // always promised, no connection string.
        var html = await SetupTabViewRenderer.RenderTabAsync(harness.Service, authorisedStoreId: WalletStore, WalletStore);

        Assert.Contains(ConfiguredCopy, html);
        Assert.Contains($"/Spark/Status/{WalletStore}", html);
        Assert.DoesNotContain(NotRunningWarning, html);
        Assert.DoesNotContain(PaymentKey, html);
    }

    [Fact]
    public async Task An_authorised_store_whose_wallet_is_configured_but_not_running_says_so()
    {
        using var harness = SparkServiceHarness.Create();
        harness.SeedStore(WalletStore, SparkServiceHarness.MnemonicFor(4), PaymentKey);
        // The SDK connect for this store never returns, so startup leaves it configured but with no
        // client up — the one state pair that distinguishes this pane from the plain configured one.
        harness.Sdk.HangFor.Add(WalletStore);
        SetupTabViewRenderer.StartService(harness);

        // Both state questions were asked of the authorised store: configured, yes; running, no — and
        // the warning names the same store's status page the rest of the pane links to.
        var html = await SetupTabViewRenderer.RenderTabAsync(harness.Service, authorisedStoreId: WalletStore, WalletStore);

        Assert.Contains(ConfiguredCopy, html);
        Assert.Contains(NotRunningWarning, html);
        Assert.Contains($"/Spark/Status/{WalletStore}", html);
        Assert.DoesNotContain(PaymentKey, html);
    }
}
