using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Services;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The part of the Lightning-config path that no fake covers.
/// </summary>
/// <remarks>
/// <see cref="BTCPayStoreLightningConfigStore"/> needs a store repository, a database and BTCPay's payment
/// handler dictionary, so its behaviour belongs to the manual checklist rather than here. What is both testable
/// and worth testing is the pair of payment-method identifiers it writes under: every other test in the suite
/// runs against a fake of <see cref="IStoreLightningConfigStore"/> and would keep passing if these drifted from
/// what BTCPay's own Lightning handler reads, leaving the plugin writing configuration nothing looks at while
/// reporting success.
/// </remarks>
public class BTCPayStoreLightningConfigStoreTests
{
    [Fact]
    public void It_writes_under_the_payment_method_ids_BTCPay_reads()
    {
        Assert.Equal("BTC-LN", BTCPayStoreLightningConfigStore.LightningId.ToString());
        Assert.Equal("BTC-LNURL", BTCPayStoreLightningConfigStore.LnurlId.ToString());
    }

    [Fact]
    public void Those_ids_are_the_ones_core_derives_for_bitcoin()
    {
        // Stated both ways round on purpose: the literals above document what the strings must be, and this
        // checks the plugin derives them through core's own helpers rather than hard-coding a value a future core
        // change could leave behind.
        Assert.Equal(PaymentTypes.LN.GetPaymentMethodId("BTC"), BTCPayStoreLightningConfigStore.LightningId);
        Assert.Equal(PaymentTypes.LNURL.GetPaymentMethodId("BTC"), BTCPayStoreLightningConfigStore.LnurlId);
    }

    [Fact]
    public void The_two_payment_methods_are_distinct()
    {
        Assert.NotEqual(BTCPayStoreLightningConfigStore.LightningId, BTCPayStoreLightningConfigStore.LnurlId);
    }

    [Fact]
    public void Constructing_it_does_not_resolve_BTCPays_payment_handler_dictionary()
    {
        // The second edge of the cycle that deadlocked BTCPay's startup. Building
        // PaymentMethodHandlerDictionary enumerates every IPaymentMethodHandler, which reaches back into the
        // plugin's own connection-string handler; so this class must stay off that graph and only ask for the
        // dictionary once the container is built. See SparkPluginStartupTests for the whole cycle, and the
        // remarks on BTCPayStoreLightningConfigStore for why the container cannot report it as circular.
        var resolved = 0;

        _ = new BTCPayStoreLightningConfigStore(storeRepository: null!, () =>
        {
            resolved++;
            throw new InvalidOperationException("The dictionary must not be needed at construction time.");
        });

        Assert.Equal(0, resolved);
    }
}
