using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The cross-chain destination pickers, as the sweep page actually renders them.
/// </summary>
public class SweepDestinationPickerTests
{
    private const string Store = SparkSurfaceHarness.AttackerStore;
    private const string Evm = "0x742d35Cc6634C0532925a3b844Bc454e4438f44e";

    [Fact]
    public async Task The_page_offers_a_chain_and_asset_this_build_does_not_list()
    {
        // The end of the path the catalogue's own test starts: a store configured through the API, which takes
        // both fields as free text, opening the page. Its destination has to be there to select — a picker that
        // dropped it would post arbitrum/USDT the next time this merchant saved a fee limit.
        var h = CreateHarness(chain: "zksync", asset: "USDC");

        var vm = await ReadPage(h);

        Assert.Equal("zksync", vm.SelectedChain);
        Assert.Equal("USDC", vm.SelectedAsset);
        Assert.Contains(vm.ChainOptions, option => option.Chain == "zksync");
        Assert.Contains(vm.AssetOptions, asset => asset.Symbol == "USDC");
    }

    [Fact]
    public async Task Saving_an_unrelated_field_leaves_an_unlisted_destination_alone()
    {
        // The other half, and the one that costs money: the form round-trips what the picker rendered, so a
        // merchant raising their fee limit must not also be moving their sweeps to a chain they never chose.
        var h = CreateHarness(chain: "zksync", asset: "USDC");
        var vm = await ReadPage(h);

        var edited = Posted(vm);
        edited.MaxFeePercent = 1.5;

        var result = await h.Mvc.Sweep(Store, new SparkSweepViewModel { Settings = edited }, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        var saved = h.Settings.Settings[Store]!.Sweep;
        Assert.Equal("zksync", saved.EvmChain);
        Assert.Equal("USDC", saved.EvmAsset);
        Assert.Equal(1.5, saved.MaxFeePercent);
    }

    [Fact]
    public async Task A_destination_saved_in_another_case_stays_on_its_own_chain()
    {
        // The failure this rules out is silent and expensive: "POLYGON" matches no option by exact value, the
        // browser selects the first one instead, and the merchant's next save moves their sweeps to arbitrum.
        var h = CreateHarness(chain: "POLYGON", asset: "usdt");
        var vm = await ReadPage(h);

        Assert.Equal("polygon", vm.SelectedChain);
        Assert.Equal("USDT", vm.SelectedAsset);

        var result = await h.Mvc.Sweep(Store, new SparkSweepViewModel { Settings = Posted(vm) }, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("polygon", h.Settings.Settings[Store]!.Sweep.EvmChain);
        Assert.Equal("USDT", h.Settings.Settings[Store]!.Sweep.EvmAsset);
    }

    [Fact]
    public async Task The_pickers_are_populated_with_the_wallet_stopped_and_the_network_gone()
    {
        // The cold-cache render, on a server that cannot reach anything: the harness's catalogue is wired to a
        // handler that always fails, so this is the built-in fallback and nothing else. The page already tells
        // merchants these settings can still be saved with the wallet down, and a picker that needed either the
        // SDK or the provider's endpoint to draw itself could not honour that.
        var h = CreateHarness(chain: null, asset: null);
        h.Runtime.Clients.Remove(Store);

        var vm = await ReadPage(h);

        Assert.False(vm.WalletRunning);
        Assert.False(h.CrossChainCatalog.IsLive);
        Assert.Equal(CrossChainCatalog.Fallback.Count, vm.ChainOptions.Count);
        Assert.Equal(SweepSettings.DefaultCrossChainChain, vm.SelectedChain);
        Assert.NotEmpty(vm.AssetOptions);
    }

    [Fact]
    public async Task What_the_catalogue_fetched_is_what_the_page_offers()
    {
        // The other end of the same wire, and the reason the catalogue is fetched at all: once the route table
        // has been read, the page offers the chains and assets that are actually in it rather than the six the
        // plugin shipped with.
        var h = CreateHarness(chain: null, asset: null, routes: RecordedPayloads.OrchestrationRoutes);
        Assert.True(await h.CrossChainCatalog.RefreshAsync());

        var vm = await ReadPage(h);

        Assert.Contains(vm.ChainOptions, option => option.Chain == "base");
        Assert.Contains(vm.ChainOptions, option => option.Chain == "avalanche");
        Assert.Contains(
            vm.ChainOptions.Single(option => option.Chain == "arbitrum").Assets,
            asset => asset.Symbol == "USDC");

        // And the default is still what a store with nothing configured opens on.
        Assert.Equal(SweepSettings.DefaultCrossChainChain, vm.SelectedChain);
        Assert.Equal(SweepSettings.DefaultCrossChainAsset, vm.SelectedAsset);
    }

    [Fact]
    public async Task Rendering_the_page_asks_neither_the_wallet_nor_the_provider_for_routes()
    {
        // Audit finding F1: a provider round trip per GET is amplification any store viewer can loop. The SDK's
        // route table is read once per sweep, where it decides something, and never to draw a list — and the
        // orchestrator's is read on a schedule of its own, off the request thread, at most once per interval.
        var h = CreateHarness(chain: null, asset: null, routes: RecordedPayloads.OrchestrationRoutes);
        await h.CrossChainCatalog.RefreshAsync();

        var before = h.CrossChainRequests.Requests;

        for (var i = 0; i < 25; i++)
            await ReadPage(h);

        Assert.DoesNotContain("sdk:cc-routes", h.WriteLog.Entries);
        Assert.Equal(before, h.CrossChainRequests.Requests);

        // And that marker is a live one, not a string this test could never match: the preview on the same
        // store does consult the route table, because there it decides whether the sweep can happen at all.
        await h.Mvc.SweepPreview(Store, CancellationToken.None);
        Assert.Contains("sdk:cc-routes", h.WriteLog.Entries);
    }

    /// <summary>A mainnet store with a cross-chain destination already configured.</summary>
    /// <param name="routes">
    /// What the provider's route table would answer, or null for a server that cannot reach it at all. Null is
    /// the default because the settings page has to work in that state, and because a test that did not ask for
    /// a live catalogue should not silently get one.
    /// </param>
    private static SparkSurfaceHarness CreateHarness(string? chain, string? asset, string? routes = null)
    {
        var h = SparkSurfaceHarness.Create(mainnet: true, crossChainRoutes: routes);

        h.Settings.Settings[Store] = new SparkSettings
        {
            ProtectedMnemonic = "attacker-protected",
            PaymentKey = SparkSurfaceHarness.VictimPaymentKey,
            SeedSource = SeedSource.Generated,
            Sweep = new SweepSettings
            {
                BalanceThresholdSats = 1_000_000,
                MinimumSweepSats = SweepSettings.DefaultCrossChainMinimumSweepSats,
                MaxFeePercent = 3,
                DestinationMode = SweepDestinationMode.EvmAddress,
                EvmAddress = Evm,
                EvmChain = chain,
                EvmAsset = asset
            }
        };

        return h;
    }

    /// <summary>
    /// The form as a browser would send it back: a select posts the <em>option's</em> value, which is the one
    /// the page rendered as selected — not whatever string the settings happen to hold.
    /// </summary>
    private static SweepSettingsInput Posted(SparkSweepViewModel vm)
    {
        var input = vm.Settings;
        input.EvmChain = vm.SelectedChain;
        input.EvmAsset = vm.SelectedAsset;
        return input;
    }

    private static async Task<SparkSweepViewModel> ReadPage(SparkSurfaceHarness h)
    {
        var result = await h.Mvc.Sweep(Store, 0, 25, CancellationToken.None);
        return Assert.IsType<SparkSweepViewModel>(Assert.IsType<ViewResult>(result).Model);
    }
}
