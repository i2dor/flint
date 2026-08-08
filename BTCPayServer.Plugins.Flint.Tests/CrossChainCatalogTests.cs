using BTCPayServer.Plugins.Flint.Services;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// What the sweep page's chain and asset pickers offer, and what they must never take away.
/// </summary>
/// <remarks>
/// <para>
/// These are the rules that hold over <em>any</em> catalogue — the fetched one, a stale one, or the built-in
/// fallback — which is why every one of them runs against a list handed in rather than against whatever the
/// service currently holds. <see cref="CrossChainCatalogFetchTests"/> covers where that list comes from.
/// </para>
/// <para>
/// The catalogue is a convenience: <see cref="CrossChainRouteResolver"/> re-reads the live route table before
/// every send, so nothing here authorises a destination. What it can do is <em>lose</em> one — and that is what
/// these tests are for. A closed picker that cannot render a store's saved chain either substitutes a different
/// destination for it or blocks every unrelated setting on the page behind it, and the first of those redirects
/// money.
/// </para>
/// </remarks>
public class CrossChainCatalogTests
{
    private static IReadOnlyList<CrossChainDestination> Catalogue => CrossChainCatalog.Fallback;

    private static CrossChainPicker Picker(string? chain, string? asset) =>
        CrossChainPicker.Over(Catalogue, chain, asset);

    [Fact]
    public void The_default_destination_is_offered()
    {
        // If a catalogue edit dropped arbitrum, or renamed its asset, the picker would open on something other
        // than what a store with nothing configured actually sweeps to.
        var picker = Picker(null, null);

        var chain = Assert.Single(picker.Chains, entry => entry.Chain == SweepSettings.DefaultCrossChainChain);
        Assert.Contains(chain.Assets, asset => asset.Symbol == SweepSettings.DefaultCrossChainAsset);
        Assert.Equal(SweepSettings.DefaultCrossChainChain, picker.SelectedChain);
        Assert.Equal(SweepSettings.DefaultCrossChainAsset, picker.SelectedAsset);
        Assert.Equal(SweepSettings.DefaultCrossChainChain, CrossChainCatalog.EffectiveChain(null));
        Assert.Equal(SweepSettings.DefaultCrossChainAsset, CrossChainCatalog.EffectiveAsset(" "));
    }

    [Fact]
    public void Every_offered_chain_carries_an_asset()
    {
        // An empty asset list renders an empty select, which posts a blank asset — and a blank asset is stored,
        // then silently resolved to USDT by EffectiveCrossChainAsset at the point of sending.
        Assert.NotEmpty(Catalogue);
        Assert.All(Catalogue, entry => Assert.NotEmpty(entry.Assets));
        Assert.All(Catalogue, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Chain)));
        Assert.All(Catalogue, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Label)));
        Assert.All(Catalogue, entry => Assert.All(entry.Assets, asset =>
            Assert.False(string.IsNullOrWhiteSpace(asset.Symbol))));
    }

    [Fact]
    public void A_saved_chain_this_build_does_not_know_is_still_offered()
    {
        // The case that costs money if it is wrong. A store configured through the API — which takes both fields
        // as free text — or before a chain left this list, or while the catalogue was stale, must find its own
        // destination in the picker. If it is absent the browser selects the first option instead, and saving an
        // unrelated field on this page would move that store's sweeps from a chain it chose to arbitrum without
        // saying so.
        var picker = Picker("zksync", "USDC");

        var saved = Assert.Single(picker.Chains, entry => entry.Chain == "zksync");
        Assert.Equal(new[] { "USDC" }, saved.Assets.Select(asset => asset.Symbol).ToArray());
        Assert.Equal("zksync", CrossChainCatalog.EffectiveChain("zksync"));

        // Added to the catalogue rather than substituted for part of it.
        Assert.Equal(Catalogue.Count + 1, picker.Chains.Count);
        Assert.All(Catalogue, known => Assert.Contains(picker.Chains, entry => entry.Chain == known.Chain));
    }

    [Fact]
    public void A_saved_asset_the_chain_does_not_carry_is_still_offered_on_it()
    {
        var picker = Picker(SweepSettings.DefaultCrossChainChain, "USDC");

        var chain = Assert.Single(picker.Chains, entry => entry.Chain == SweepSettings.DefaultCrossChainChain);
        Assert.Contains(chain.Assets, asset => asset.Symbol == "USDC");

        // The chain's own assets survive alongside it — this is an addition, not a replacement.
        Assert.Contains(chain.Assets, asset => asset.Symbol == SweepSettings.DefaultCrossChainAsset);
        Assert.Equal(Catalogue.Count, picker.Chains.Count);

        // And only that chain gains it: an asset one chain happens to carry says nothing about the others.
        Assert.All(
            picker.Chains.Where(entry => entry.Chain != SweepSettings.DefaultCrossChainChain),
            entry => Assert.DoesNotContain(entry.Assets, asset => asset.Symbol == "USDC"));
    }

    [Fact]
    public void An_asset_this_build_did_not_supply_reports_no_decimals_rather_than_a_guess()
    {
        // Zero is "not reported", and it has to stay that way. Nothing computes with these — the send path reads
        // decimals off the live route — but a plausible-looking 6 invented here is the sort of number somebody
        // eventually divides by, and USDT is 18 decimals on BSC.
        var picker = Picker("zksync", "USDC");

        Assert.Equal(0, picker.SelectedAssetEntry.Decimals);
        Assert.Equal("USDC", picker.SelectedAssetEntry.Name);
    }

    [Theory]
    [InlineData("ARBITRUM", "usdt")]
    [InlineData("Arbitrum", "Usdt")]
    [InlineData("  arbitrum  ", " USDT ")]
    public void A_saved_value_that_differs_only_in_case_or_spacing_adds_nothing(string chain, string asset)
    {
        // The route resolver matches case-insensitively, so a store saved as ARBITRUM/usdt is on exactly the
        // route the catalogue already lists. Adding a second option for it would show the merchant their own
        // chain twice and make the duplicate look like a different destination.
        var picker = Picker(chain, asset);

        Assert.Equal(Catalogue.Count, picker.Chains.Count);
        Assert.Equal(
            Catalogue.Single(entry => entry.Chain == SweepSettings.DefaultCrossChainChain).Assets,
            picker.Assets);
    }

    [Theory]
    [InlineData("POLYGON", "usdt", "polygon", "USDT")]
    [InlineData("  Optimism ", " uSdT ", "optimism", "USDT")]
    [InlineData("zksync", "usdc", "zksync", "usdc")]
    public void What_the_picker_selects_is_the_spelling_the_list_uses(
        string chain, string asset, string expectedChain, string expectedAsset)
    {
        // An <option> is selected by exact value while the route table matches case-insensitively, so a store
        // saved as POLYGON must resolve to the "polygon" option. Comparing the raw field instead would match
        // nothing, leave the browser selecting the first option, and quietly post arbitrum the next time this
        // merchant saved a fee limit — their money onto a chain they never chose.
        //
        // An unlisted chain keeps its own spelling, because there is no other spelling of it to prefer.
        var picker = Picker(chain, asset);

        Assert.Equal(expectedChain, picker.SelectedChain);
        Assert.Equal(expectedAsset, picker.SelectedAsset);

        // And the value the picker selects is one the picker actually renders, which is the point of all of it.
        Assert.Contains(picker.Chains, entry => entry.Chain == picker.SelectedChain);
        Assert.Contains(picker.Assets, entry => entry.Symbol == picker.SelectedAsset);
    }

    [Fact]
    public void Every_explorer_is_an_https_base_url_an_address_appends_to()
    {
        // Both halves matter to the link that gets built. A URL without the trailing slash silently concatenates
        // the address onto the path segment before it, and a scheme other than https on a page served over TLS
        // is a link a browser may refuse to follow at all.
        foreach (var explorer in Catalogue
                     .Select(entry => entry.AddressExplorer)
                     .Where(url => url is not null))
        {
            Assert.StartsWith("https://", explorer, StringComparison.Ordinal);
            Assert.EndsWith("/", explorer, StringComparison.Ordinal);
            Assert.True(Uri.IsWellFormedUriString(explorer, UriKind.Absolute), explorer);
        }
    }

    [Fact]
    public void A_chain_with_no_explorer_is_still_a_chain_you_can_sweep_to()
    {
        // The explorer is display-only metadata. A chain nobody has confirmed an explorer for must lose its
        // link and nothing else — dropping it from the picker would remove a destination over a cosmetic gap.
        // sei is the live example: the projection offers it and this build knows no explorer for it.
        var unlinked = new CrossChainDestination("sei", "Sei", [new CrossChainAsset("USDC", "USDC", 6)]);
        CrossChainDestination[] catalogue = [unlinked, .. Catalogue];

        var picker = CrossChainPicker.Over(catalogue, "sei", "USDC");

        Assert.Null(unlinked.AddressExplorer);
        Assert.Equal("sei", picker.SelectedChain);
        Assert.Equal("USDC", picker.SelectedAsset);
        Assert.Equal(catalogue.Length, picker.Chains.Count);

        foreach (var chain in new[] { "sei", "monad", "hyperevm", "robinhood", "tempo" })
        {
            Assert.Null(CrossChainCatalog.AddressLinkFor(
                chain, "0x742d35Cc6634C0532925a3b844Bc454e4438f44e"));
        }
    }

    [Theory]
    [InlineData(null, "0xabc")]
    [InlineData("arbitrum", null)]
    [InlineData("arbitrum", "  ")]
    [InlineData("zksync", "0xabc")]
    public void There_is_no_link_to_a_chain_or_address_this_build_cannot_place(string? chain, string? address)
    {
        // Notably including a chain the catalogue does not list: a store can save one, and guessing an explorer
        // for it would point the merchant at the wrong chain's site while their money sat on another.
        Assert.Null(CrossChainCatalog.AddressLinkFor(chain, address));
    }

    [Theory]
    [InlineData(" ARBITRUM ", "https://arbiscan.io/address/")]
    [InlineData("plasma", "https://plasmascan.to/address/")]
    [InlineData("base", "https://basescan.org/address/")]
    [InlineData("avalanche", "https://snowtrace.io/address/")]
    public void A_link_is_the_chains_explorer_with_the_address_on_the_end(string chain, string expected)
    {
        // Pinned per chain rather than generically, because the failure this rules out is silent: a merchant
        // sent to a live explorer for the wrong chain is told with apparent authority that their money is not
        // there. Plasma, base and avalanche are the three added after the first catalogue shipped.
        const string address = "0x742d35Cc6634C0532925a3b844Bc454e4438f44e";

        Assert.Equal(expected + address, CrossChainCatalog.AddressLinkFor(chain, $" {address} "));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("zksync", "USDC")]
    [InlineData("arbitrum", "USDC")]
    [InlineData("arbitrum", null)]
    public void The_asset_in_force_is_always_one_the_picker_offers(string? chain, string? asset)
    {
        // The invariant the view relies on: whatever the form holds, the asset select contains the value it is
        // about to render as selected. Without it the select silently posts its first option instead.
        var picker = Picker(chain, asset);

        Assert.NotEmpty(picker.Assets);
        Assert.Contains(picker.Assets, entry => entry.Symbol == CrossChainCatalog.EffectiveAsset(asset));

        // And the chain it was read off is one the picker offers, so the two selects cannot disagree.
        Assert.Contains(picker.Chains, entry => entry.Chain == CrossChainCatalog.EffectiveChain(chain));
    }

    [Fact]
    public void The_offline_picker_is_the_fallback_on_its_default_destination()
    {
        // What a view model nobody filled in renders. Two empty selects would post a blank chain and a blank
        // asset, which are stored and then silently resolved to the defaults at the point of sending.
        Assert.Equal(CrossChainCatalog.Fallback, CrossChainPicker.Offline.Chains);
        Assert.Equal(SweepSettings.DefaultCrossChainChain, CrossChainPicker.Offline.SelectedChain);
        Assert.Equal(SweepSettings.DefaultCrossChainAsset, CrossChainPicker.Offline.SelectedAsset);
    }
}
