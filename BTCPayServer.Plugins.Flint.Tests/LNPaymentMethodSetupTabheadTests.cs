using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The setup-tabhead extension point renders a store's connection string only for the store the request
/// was authorised for — never for the one the bound model names.
/// </summary>
/// <remarks>
/// <para>
/// <c>LightningNodeViewModel.StoreId</c> is an ordinary form-bound settable property, and core's POST of
/// <c>SetupLightningNode</c> re-renders the page through six paths without ever reassigning it from the
/// route or the authorised store. So the model reaching the plugin's partial on a POST can name any store
/// on the server, and before the authorised-store guard the partial resolved the connection string — a
/// bearer spend credential — straight off <c>Model.StoreId</c> and embedded it in page JS.
/// </para>
/// <para>
/// The partial resolves the authorised id from the request and keys every lookup off it alone; the only
/// refusal is a missing authorisation, and the form-bound id is never trusted on any path. These are the
/// suite's first tests that <em>render</em> a view, and they render the real thing through
/// <see cref="SetupTabViewRenderer"/>: the compiled <c>.cshtml</c>, a real <see cref="BTCPayServer.Plugins.Flint.Services.SparkService"/>
/// over fakes, authorisation through BTCPay's own store-data channel. Assertions are on the rendered
/// output only. A regression that resolved off the model would call <c>GetConnectionString</c> for the
/// wrong store and put that store's key in the output, which is exactly what the mismatch case asserts
/// against; the match case holds the resolution honest the other way, so the fix cannot pass by simply
/// never rendering anything.
/// </para>
/// </remarks>
public class LNPaymentMethodSetupTabheadTests
{
    /// <summary>The store whose connection string exists and would be rendered if consulted.</summary>
    private const string WalletStore = "store-with-wallet";

    /// <summary>A different, wallet-less store, used as the authorised one in the mismatch case.</summary>
    private const string AuthorisedStore = "store-authorised";

    /// <summary>A payment key shaped like a generated one, distinctive so its presence in output is unmistakable.</summary>
    private const string PaymentKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task A_model_naming_another_store_than_the_authorised_one_renders_no_connection_string()
    {
        using var harness = SparkServiceHarness.Create();
        harness.SeedStore(WalletStore, SparkServiceHarness.MnemonicFor(1), PaymentKey);
        SetupTabViewRenderer.StartService(harness);

        // The attack: authorisation succeeded for AuthorisedStore, the form-bound model names WalletStore.
        // Rendering keys off the authorised id alone, so what can render is only the authorised store's own
        // wallet-less branch — and the store the form named appears nowhere in the output.
        var html = await SetupTabViewRenderer.RenderTabheadAsync(harness.Service, authorisedStoreId: AuthorisedStore, WalletStore);

        Assert.DoesNotContain("type=flint", html);
        Assert.DoesNotContain(PaymentKey, html);
        Assert.DoesNotContain(WalletStore, html);
        // And the partial is not merely silent: it renders the authorised store's honest "no wallet yet"
        // pill, with its link carrying the authorised id — proof of which store it resolved, not of nothing.
        Assert.Contains("Set up Flint", html);
        Assert.Contains($"/Spark/Setup/{AuthorisedStore}", html);
    }

    [Fact]
    public async Task A_request_carrying_no_authorised_store_renders_nothing()
    {
        using var harness = SparkServiceHarness.Create();
        harness.SeedStore(WalletStore, SparkServiceHarness.MnemonicFor(2), PaymentKey);
        SetupTabViewRenderer.StartService(harness);

        // Nothing was authorised onto the request: the guard must fail closed rather than trust the form.
        var html = await SetupTabViewRenderer.RenderTabheadAsync(harness.Service, authorisedStoreId: null, WalletStore);

        Assert.DoesNotContain("type=flint", html);
        Assert.DoesNotContain(PaymentKey, html);
        Assert.Equal(string.Empty, html.Trim());
    }

    [Fact]
    public async Task The_authorised_store_still_gets_its_connection_string_rendered()
    {
        using var harness = SparkServiceHarness.Create();
        harness.SeedStore(WalletStore, SparkServiceHarness.MnemonicFor(3), PaymentKey);
        SetupTabViewRenderer.StartService(harness);

        // The legitimate flow — authorised store and model agree — must keep working unchanged.
        var html = await SetupTabViewRenderer.RenderTabheadAsync(harness.Service, authorisedStoreId: WalletStore, WalletStore);

        // And it is the real pill: the script that fills core's connection-string field is present.
        Assert.Contains("<script", html);
    }
}
