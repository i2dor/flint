using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Services;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The exit explorer's transport rules: which client serves a URL, and the connect-time address filter that
/// catches what a hostname resolves to — the case <c>TryNormaliseApiUrl</c>'s literal check cannot see.
/// </summary>
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

    [Theory]
    [InlineData("169.254.169.254")] // the cloud metadata service, the target that matters
    [InlineData("224.0.0.1")]
    public async Task The_direct_client_refuses_to_dial_an_address_that_is_never_an_explorer(string host)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = SparkExitFundingExplorer.ConnectFilteredAsync
        };
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync($"http://{host}:80/api", CancellationToken.None));
        Assert.Contains("never a block explorer", ex.ToString());
    }

    [Fact]
    public async Task The_direct_client_still_reaches_loopback_where_a_self_hosted_esplora_lives()
    {
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = 200;
            context.Response.Close();
        });

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = SparkExitFundingExplorer.ConnectFilteredAsync
        };
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync($"http://127.0.0.1:{port}/", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await serve;
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
