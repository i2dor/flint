using System.Net;
using System.Text;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Where the destination catalogue comes from: the provider's route table, what survives the projection, and
/// what happens when the fetch does not.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here touches the network. The projection is asserted against a payload recorded from the live
/// endpoint (see <see cref="RecordedPayloads.OrchestrationRoutes"/>), and every failure mode is arranged with a
/// stub handler — because "the fetch failed" is the case that has to keep the settings page working, and it is
/// not a case a test can wait for.
/// </para>
/// <para>
/// The caching assertions are the ones that matter operationally. Audit finding ApiSurface F1 is request-thread
/// fan-out on a GET, and a picker fed from a per-render fetch would be exactly that against somebody else's
/// public endpoint.
/// </para>
/// </remarks>
public class CrossChainCatalogFetchTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    #region Projection

    [Fact]
    public async Task The_recorded_table_projects_to_the_chains_spark_reaches_with_an_evm_address()
    {
        // Pinned exactly, and it is worth pinning: this list is the difference between a merchant being offered
        // USDC on base and being told the plugin supports six chains and Tether. Fallback order first, then
        // alphabetical, so the default destination stays at the top of the picker.
        var projected = await Project();

        Assert.Equal(
            new[]
            {
                "arbitrum", "optimism", "polygon", "plasma", "ethereum", "bsc",
                "avalanche", "base", "hyperevm", "monad", "robinhood", "sei", "tempo"
            },
            projected.Select(entry => entry.Chain).ToArray());
    }

    [Fact]
    public async Task The_projection_finds_what_the_hardcoded_list_was_missing()
    {
        // The reason any of this exists. The static list named six chains and USDT; the provider carries USDC on
        // eleven of the thirteen, and five chains the list had never heard of.
        var projected = await Project();

        Assert.Equal(
            new[] { "USDT", "ETH", "tBTC", "USDC" },
            Assets(projected, "arbitrum"));

        Assert.Equal(new[] { "cbBTC", "ETH", "USDC" }, Assets(projected, "base"));
        Assert.Equal(new[] { "USDC" }, Assets(projected, "avalanche"));
        Assert.Equal(new[] { "USDT", "POL", "USDC", "USDC.e" }, Assets(projected, "polygon"));
        // USD₮0 is not USDT and does not sort next to it: the character in the middle is U+20AE, and the plugin
        // matches assets exactly for precisely this reason.
        Assert.Equal(new[] { "HYPE", "USDC", "USDe", "USD₮0" }, Assets(projected, "hyperevm"));

        // Every chain the old hardcoded list named is still there, so nothing a store had configured was lost.
        Assert.All(CrossChainCatalog.Fallback, known =>
            Assert.Contains(projected, entry => entry.Chain == known.Chain));
    }

    [Fact]
    public async Task Decimals_and_names_are_read_off_the_route_rather_than_assumed()
    {
        // The single most convincing argument against hardcoding: USDT is 6 decimals everywhere the old list
        // named it except BSC, where it is 18. A catalogue that assumed one number would be wrong about the
        // other, and BSC was on the list.
        var projected = await Project();

        Assert.Equal(6, Asset(projected, "arbitrum", "USDT").Decimals);
        Assert.Equal(18, Asset(projected, "bsc", "USDT").Decimals);
        Assert.Equal(8, Asset(projected, "base", "cbBTC").Decimals);
        Assert.Equal(18, Asset(projected, "arbitrum", "tBTC").Decimals);

        Assert.Equal("Tether", Asset(projected, "ethereum", "USDT").Name);
        Assert.Equal("Coinbase BTC", Asset(projected, "base", "cbBTC").Name);
        Assert.Equal("Bridged USDC", Asset(projected, "polygon", "USDC.e").Name);

        // And the chain's label is the provider's display name, not its routing key.
        Assert.Equal("BNB", projected.Single(entry => entry.Chain == "bsc").Label);
        Assert.Equal("HyperEVM", projected.Single(entry => entry.Chain == "hyperevm").Label);
    }

    [Fact]
    public async Task Nothing_a_merchants_evm_address_could_not_receive_on_is_offered()
    {
        // The chains Spark genuinely reaches and this build still will not list, each for its own reason. Every
        // one of them would render as an option a merchant could pick and then never sweep to, because the
        // address field on that page is 0x-and-twenty-bytes and the save enforces it.
        var projected = await Project();
        var offered = projected.Select(entry => entry.Chain).ToList();

        // No chain id at all.
        Assert.DoesNotContain("bitcoin", offered);
        Assert.DoesNotContain("lightning", offered);
        Assert.DoesNotContain("spark", offered);

        // CAIP-2 namespaced chain ids, so not EVM chain ids.
        Assert.DoesNotContain("solana", offered);
        Assert.DoesNotContain("ton", offered);
        Assert.DoesNotContain("xrp", offered);
        Assert.DoesNotContain("zcash", offered);

        // The two a numeric chain id alone would have let through: tron's 728126428 with base58 addresses, and
        // hypercore's 1337 with short 0x-prefixed asset indices that are not addresses.
        Assert.DoesNotContain("tron", offered);
        Assert.DoesNotContain("hypercore", offered);
    }

    [Fact]
    public async Task Only_routes_leaving_spark_are_offered()
    {
        // The table is every pair the orchestrator carries, in both directions, and a destination reachable
        // from ethereum is not thereby reachable from Spark. Robinhood Chain is where that shows: the
        // orchestrator carries tokenised equities onto it from other sources, and none of them from Spark. An
        // unfiltered projection would put AAPL and TSLA in a BTCPay merchant's sweep picker, where picking one
        // buys them a refusal at the next sweep.
        var recorded = System.Text.Json.JsonDocument.Parse(RecordedPayloads.OrchestrationRoutes);
        var equities = recorded.RootElement.GetProperty("routes").EnumerateArray()
            .Where(route => route.GetProperty("sourceChain").GetString() != "spark")
            .Select(route => route.GetProperty("destinationAsset").GetString())
            .ToHashSet();

        // The recording really does carry them, or this test proves nothing.
        Assert.Contains("AAPL", equities);
        Assert.Contains("TSLA", equities);

        var offered = Assets(await Project(), "robinhood");

        Assert.DoesNotContain("AAPL", offered);
        Assert.DoesNotContain("TSLA", offered);
        Assert.Contains("USDG", offered);
    }

    [Fact]
    public async Task A_chain_is_listed_even_when_the_provider_calls_it_ineligible_for_every_quote_shape()
    {
        // sei's only Spark route carries exactOutEligible and fixedEligible both false. This plugin asks for
        // neither quote shape — a sweep is an ordinary send — so dropping sei on those flags would be the
        // catalogue vetoing a route the provider carries, which is the one thing it must never do.
        var projected = await Project();

        Assert.Contains(projected, entry => entry.Chain == "sei");
    }

    [Fact]
    public async Task A_symbol_the_page_could_not_encode_is_dropped_and_its_chain_survives()
    {
        // The page hands each chain's assets to the browser as a space-separated attribute, so a symbol with a
        // space in it would split into two assets that do not exist and post one of them. Dropping the asset
        // costs an option; keeping it costs a destination nobody chose.
        var projected = await Project(Payload(
            Route("spark", "arbitrum", "GOOD COIN", "1", contract: null),
            Route("spark", "arbitrum", "USDT", "1", contract: null)));

        var arbitrum = Assert.Single(projected);
        Assert.Equal(new[] { "USDT" }, arbitrum.Assets.Select(asset => asset.Symbol).ToArray());
    }

    #endregion

    #region Falling back

    [Fact]
    public void A_catalogue_that_has_never_fetched_anything_is_the_fallback()
    {
        // The cold-cache render, and the permanent state of a server with no outbound network. Reading the
        // catalogue must answer immediately with a usable list rather than block, throw or come back empty.
        var catalog = Catalog(StubHttpMessageHandler.Offline(), out _);

        Assert.Equal(CrossChainCatalog.Fallback, catalog.Snapshot());
        Assert.False(catalog.IsLive);
        Assert.Equal(SweepSettings.DefaultCrossChainChain, catalog.PickerFor(null, null).SelectedChain);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("http-error")]
    [InlineData("garbage")]
    [InlineData("empty")]
    [InlineData("nothing-usable")]
    public async Task Every_way_the_fetch_can_fail_leaves_the_fallback_in_place(string how)
    {
        // Including the two that are not transport failures. A payload that parses to nothing is treated as a
        // failure rather than believed: zero Spark-sourced EVM routes is a changed format, not a changed world,
        // and emptying a working picker on the strength of it would be trusting the parse over the evidence.
        var handler = how switch
        {
            "offline" => StubHttpMessageHandler.Offline(),
            "http-error" => StubHttpMessageHandler.Failing(HttpStatusCode.InternalServerError),
            "garbage" => StubHttpMessageHandler.Returning("<html>not json</html>"),
            "empty" => StubHttpMessageHandler.Returning("""{"routes":[]}"""),
            _ => StubHttpMessageHandler.Returning(Payload(
                Route("bitcoin", "arbitrum", "USDT", "42161", contract: null)))
        };

        var catalog = Catalog(handler, out _);

        Assert.False(await catalog.RefreshAsync());
        Assert.Equal(CrossChainCatalog.Fallback, catalog.Snapshot());
        Assert.False(catalog.IsLive);
    }

    [Fact]
    public async Task A_fetch_that_stops_working_keeps_the_last_answer_rather_than_the_floor()
    {
        // A table read this morning that cannot be read now still describes the provider far better than a list
        // frozen at the last plugin release. Reverting to the floor would take destinations away from the picker
        // because of a network blip.
        var catalog = Catalog(
            StubHttpMessageHandler.OnceThenOffline(RecordedPayloads.OrchestrationRoutes), out var time);

        Assert.True(await catalog.RefreshAsync());
        Assert.Contains(catalog.Snapshot(), entry => entry.Chain == "base");

        // The endpoint goes away, and the TTL expires while it is away.
        time.Advance(CrossChainCatalog.Ttl);
        Assert.False(await catalog.RefreshAsync());

        Assert.True(catalog.IsLive);
        Assert.Contains(catalog.Snapshot(), entry => entry.Chain == "base");
        Assert.NotEqual(CrossChainCatalog.Fallback, catalog.Snapshot());

        // And it is retried on the shorter interval from here, not left for another six hours.
        Assert.False(catalog.RefreshDue);
        time.Advance(CrossChainCatalog.RetryInterval);
        Assert.True(catalog.RefreshDue);
    }

    #endregion

    #region Caching

    [Fact]
    public async Task A_thousand_renders_cause_one_fetch()
    {
        // The whole reason this is a cached service rather than a call. Audit finding ApiSurface F1 is
        // request-thread fan-out on a GET: a store viewer who reloads the settings page in a loop must not be
        // able to turn a BTCPay into a load generator against the provider.
        //
        // The handler holds the first request open, so a second one would have to be a second request rather
        // than merely a fast one.
        var handler = StubHttpMessageHandler.Blocking(RecordedPayloads.OrchestrationRoutes, out var release);
        var catalog = Catalog(handler, out _);

        for (var i = 0; i < 1_000; i++)
            Assert.Equal(CrossChainCatalog.Fallback, catalog.Snapshot());

        await handler.Started.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, handler.Requests);

        release.SetResult();
        await WaitUntil(() => catalog.IsLive);

        for (var i = 0; i < 1_000; i++)
            catalog.Snapshot();

        Assert.Equal(1, handler.Requests);
        Assert.Equal(CrossChainCatalog.RoutesUrl, handler.Urls[0]);
    }

    [Fact]
    public async Task A_successful_fetch_is_not_repeated_until_the_ttl_has_passed()
    {
        var catalog = Catalog(
            StubHttpMessageHandler.Returning(RecordedPayloads.OrchestrationRoutes), out var time);

        Assert.True(await catalog.RefreshAsync());
        Assert.False(catalog.RefreshDue);

        time.Advance(CrossChainCatalog.Ttl - TimeSpan.FromSeconds(1));
        Assert.False(catalog.RefreshDue);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(catalog.RefreshDue);
    }

    [Fact]
    public async Task A_failed_fetch_is_retried_sooner_than_a_successful_one_but_not_on_every_render()
    {
        // Both halves are load-bearing. Retrying every render turns a page a merchant is reloading into a retry
        // loop against an endpoint that is already down; waiting the full TTL means one refused connection at
        // startup shows that merchant the floor for six hours.
        var catalog = Catalog(StubHttpMessageHandler.Offline(), out var time);

        Assert.False(await catalog.RefreshAsync());
        Assert.False(catalog.RefreshDue);

        time.Advance(CrossChainCatalog.RetryInterval - TimeSpan.FromSeconds(1));
        Assert.False(catalog.RefreshDue);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(catalog.RefreshDue);

        Assert.True(CrossChainCatalog.RetryInterval < CrossChainCatalog.Ttl);
    }

    [Fact]
    public void The_first_read_of_a_fresh_catalogue_is_the_one_that_schedules_a_fetch()
    {
        // Nothing is fetched at startup: making BTCPay's boot wait on a third party's HTTP endpoint to draw a
        // picker would be a far worse trade than showing the floor once.
        var catalog = Catalog(StubHttpMessageHandler.Offline(), out _);

        Assert.True(catalog.RefreshDue);
    }

    [Fact]
    public async Task The_catalogue_reads_the_providers_route_table_through_its_own_named_client()
    {
        var handler = StubHttpMessageHandler.Returning(RecordedPayloads.OrchestrationRoutes);
        var factory = new StubHttpClientFactory(handler);

        var catalog = new CrossChainCatalog(
            factory, new StubTimeProvider(Now), NullLogger<CrossChainCatalog>.Instance);

        Assert.True(await catalog.RefreshAsync());
        Assert.Equal(CrossChainCatalog.RoutesUrl, Assert.Single(handler.Urls));
        Assert.Equal(CrossChainCatalog.HttpClientName, Assert.Single(factory.Requested));
    }

    [Fact]
    public async Task A_response_that_never_ends_is_abandoned_rather_than_read_into_memory()
    {
        // A ceiling on a body from a host this plugin does not operate, read on a background thread inside a
        // merchant's server. Without it a hung or hostile endpoint is an out-of-memory fault in BTCPay rather
        // than a picker that fell back to its floor.
        var log = new CapturingLogger<CrossChainCatalog>();
        var catalog = new CrossChainCatalog(
            new StubHttpClientFactory(StubHttpMessageHandler.Endless()), new StubTimeProvider(Now), log);

        Assert.False(await catalog.RefreshAsync());
        Assert.Equal(CrossChainCatalog.Fallback, catalog.Snapshot());

        // The ceiling stopped it, not the deadline. Without that distinction this test would still pass with
        // the ceiling removed — it would simply take the whole fetch timeout to do it, after reading however
        // many gigabytes fitted into fifteen seconds.
        Assert.Contains("byte ceiling this plugin will read", log.AllText, StringComparison.Ordinal);
    }

    #endregion

    #region Plumbing

    private static Task<IReadOnlyList<CrossChainDestination>> Project(string? payload = null) =>
        CrossChainCatalog.ProjectAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(payload ?? RecordedPayloads.OrchestrationRoutes)));

    private static CrossChainCatalog Catalog(StubHttpMessageHandler handler, out StubTimeProvider time)
    {
        time = new StubTimeProvider(Now);
        return new CrossChainCatalog(
            new StubHttpClientFactory(handler), time, NullLogger<CrossChainCatalog>.Instance);
    }

    private static string[] Assets(IReadOnlyList<CrossChainDestination> projected, string chain) =>
        projected.Single(entry => entry.Chain == chain).Assets.Select(asset => asset.Symbol).ToArray();

    private static CrossChainAsset Asset(
        IReadOnlyList<CrossChainDestination> projected, string chain, string symbol) =>
        projected.Single(entry => entry.Chain == chain).Assets.Single(asset => asset.Symbol == symbol);

    /// <summary>A route table built by hand, for the shapes the recorded payload does not happen to contain.</summary>
    private static string Payload(params string[] routes) => $$"""{"routes":[{{string.Join(',', routes)}}]}""";

    private static string Route(
        string source, string chain, string asset, string? chainId, string? contract, int decimals = 6)
    {
        var chainIdJson = chainId is null ? "null" : $"\"{chainId}\"";
        var contractJson = contract is null ? "null" : $"\"{contract}\"";

        return $$"""
            {
              "sourceChain": "{{source}}",
              "destination": {
                "chain": "{{chain}}",
                "asset": "{{asset}}",
                "assetDisplayName": "{{asset}}",
                "chainDisplayName": "{{chain}}",
                "contractAddress": {{contractJson}},
                "decimals": {{decimals}},
                "chainId": {{chainIdJson}}
              }
            }
            """;
    }

    /// <summary>Polls a condition a background refresh will eventually satisfy, rather than sleeping blindly.</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the background refresh never completed");
            await Task.Delay(10);
        }
    }

    #endregion
}
