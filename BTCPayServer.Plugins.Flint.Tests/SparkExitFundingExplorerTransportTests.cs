using BTCPayServer.Plugins.Flint.Services;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Which of the exit explorer's two clients serves a URL.
/// </summary>
/// <remarks>
/// The direct client's connect-time address filter and its refusal to follow redirects are asserted through the
/// real container in <c>SparkPluginStartupTests</c>, because they exist only in the plugin's registration.
/// </remarks>
public class SparkExitFundingExplorerTransportTests
{
    [Theory]
    [InlineData("http://explorerzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.onion/api/address/x/utxo")]
    [InlineData("http://EXPLORER.ONION/api/v1/fees/recommended")]
    public void An_onion_explorer_goes_through_the_SOCKS_client(string url) =>
        Assert.Equal(SparkExitFundingExplorer.OnionHttpClientName, SparkExitFundingExplorer.ClientNameFor(url));

    [Theory]
    [InlineData("https://mempool.space/api/v1/fees/recommended")]
    [InlineData("http://127.0.0.1:3002/address/x/utxo")]
    [InlineData("http://onion.example.com/api")]
    [InlineData("not a url")]
    public void Anything_else_uses_the_direct_client(string url) =>
        Assert.Equal(SparkExitFundingExplorer.HttpClientName, SparkExitFundingExplorer.ClientNameFor(url));
}
