using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Where a sweep goes, and — more importantly — when the plugin refuses to decide.
/// </summary>
/// <remarks>
/// The refusals are the point. A store configured to sweep into its own wallet that has no wallet must be told so;
/// falling back to whatever address is left in the settings from an earlier configuration would send a merchant's
/// balance somewhere they had stopped intending it to go.
/// </remarks>
public class SweepDestinationResolverTests
{
    private const string StoreId = "store-1";

    private const string MainnetAddress = "bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SweepDestinationResolver Create(ISweepAddressSource source, Network? network = null) =>
        new(source, network ?? Network.RegTest, NullLogger<SweepDestinationResolver>.Instance);

    [Fact]
    public async Task Store_wallet_mode_reserves_a_labelled_address_and_rotates_it()
    {
        var source = new FakeSweepAddressSource();
        var resolver = Create(source);
        var settings = new SweepSettings { DestinationMode = SweepDestinationMode.StoreWallet };

        var first = await resolver.ResolveAsync(StoreId, settings, reserve: true, Ct);
        var second = await resolver.ResolveAsync(StoreId, settings, reserve: true, Ct);

        Assert.Equal(FakeSweepAddressSource.RegtestAddresses[0], first.Destination!.Address);
        Assert.Equal(FakeSweepAddressSource.RegtestAddresses[1], second.Destination!.Address);
        Assert.True(first.Destination.Rotates);
        Assert.Equal(SweepDestinationKind.BitcoinAddress, first.Destination.Kind);
        // Reserving is what makes it rotate, so the resolver must have asked for a reserving read both times.
        Assert.Equal([(StoreId, true), (StoreId, true)], source.Calls);
    }

    [Fact]
    public async Task A_preview_does_not_consume_an_address()
    {
        var source = new FakeSweepAddressSource();
        var resolver = Create(source);
        var settings = new SweepSettings { DestinationMode = SweepDestinationMode.StoreWallet };

        var peeked = await resolver.ResolveAsync(StoreId, settings, reserve: false, Ct);
        var swept = await resolver.ResolveAsync(StoreId, settings, reserve: true, Ct);

        Assert.Equal([(StoreId, false), (StoreId, true)], source.Calls);
        // The peek saw the address the sweep then actually took, rather than having burned it.
        Assert.Equal(peeked.Destination!.Address, swept.Destination!.Address);
        Assert.Equal(1, source.ReservedCount);
    }

    [Fact]
    public async Task Store_wallet_mode_refuses_when_the_store_has_no_onchain_wallet()
    {
        // The load-bearing refusal. Nothing may be sent anywhere in this state.
        var source = new FakeSweepAddressSource
        {
            Result = SweepAddressResult.NoWallet("This store has no Bitcoin wallet.")
        };
        var resolver = Create(source);

        var resolution = await resolver.ResolveAsync(
            StoreId,
            new SweepSettings
            {
                DestinationMode = SweepDestinationMode.StoreWallet,
                // Present, and deliberately ignored: the merchant selected their store wallet.
                StaticAddress = FakeSweepAddressSource.RegtestAddresses[2]
            },
            reserve: true,
            Ct);

        Assert.Null(resolution.Destination);
        Assert.Equal("This store has no Bitcoin wallet.", resolution.RefusalReason);
    }

    [Fact]
    public async Task Store_wallet_mode_refuses_an_address_its_own_wallet_returned_for_the_wrong_network()
    {
        // Defence in depth against a misconfigured or cross-chain-restored server. Without it the SDK reports
        // "Invalid network" as a generic error, after a sweep record has already been written.
        var source = new FakeSweepAddressSource { Result = SweepAddressResult.Available(MainnetAddress) };
        var resolver = Create(source, Network.RegTest);

        var resolution = await resolver.ResolveAsync(
            StoreId, new SweepSettings { DestinationMode = SweepDestinationMode.StoreWallet }, true, Ct);

        Assert.Null(resolution.Destination);
        Assert.Contains("not valid on", resolution.RefusalReason);
    }

    [Fact]
    public async Task Static_mode_uses_the_configured_address_without_touching_the_wallet()
    {
        var source = new FakeSweepAddressSource();
        var resolver = Create(source);

        var resolution = await resolver.ResolveAsync(
            StoreId,
            new SweepSettings
            {
                DestinationMode = SweepDestinationMode.StaticAddress,
                StaticAddress = $"  {FakeSweepAddressSource.RegtestAddresses[2]}  "
            },
            reserve: true,
            Ct);

        Assert.Equal(FakeSweepAddressSource.RegtestAddresses[2], resolution.Destination!.Address);
        Assert.False(resolution.Destination.Rotates);
        Assert.Empty(source.Calls);
    }

    [Fact]
    public async Task Static_mode_refuses_an_empty_address()
    {
        var resolver = Create(new FakeSweepAddressSource());

        var resolution = await resolver.ResolveAsync(
            StoreId,
            new SweepSettings { DestinationMode = SweepDestinationMode.StaticAddress, StaticAddress = "   " },
            true,
            Ct);

        Assert.Null(resolution.Destination);
        Assert.Contains("no address has been entered", resolution.RefusalReason);
    }

    [Fact]
    public async Task Static_mode_re_validates_the_address_at_send_time()
    {
        // Settings can arrive without passing the form's validation: a backup restored from another chain, a hand
        // edit, a future API. So a mainnet address on a regtest server is refused here and not only on save.
        var resolver = Create(new FakeSweepAddressSource(), Network.RegTest);

        var resolution = await resolver.ResolveAsync(
            StoreId,
            new SweepSettings
            {
                DestinationMode = SweepDestinationMode.StaticAddress,
                StaticAddress = MainnetAddress
            },
            true,
            Ct);

        Assert.Null(resolution.Destination);
        Assert.Contains("not usable on Regtest", resolution.RefusalReason);
    }

    [Theory]
    [InlineData("bcrt1qaxlqvg4vg7vjyczda9jv7mry0wl66cg43znat0", true)]
    [InlineData("2MzQwSSnBHWHqSAqtTVQ6v47XtaisrJa1Vc", true)]
    [InlineData("bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq", false)]
    [InlineData("1BvBMSEYstWetqTFn5Au4m4GFg7xJaNVN2", false)]
    [InlineData("not-an-address", false)]
    [InlineData("", false)]
    [InlineData("bitcoin:bcrt1qaxlqvg4vg7vjyczda9jv7mry0wl66cg43znat0?amount=0.1", false)]
    public void TryParse_accepts_only_bare_addresses_for_this_network(string address, bool expected)
    {
        // The BIP21 case is not pedantry: the SDK rejects a URI with "Unsupported payment method", and silently
        // stripping one would accept a string whose amount and label parameters the merchant may believe are being
        // honoured.
        Assert.Equal(expected, SweepDestinationResolver.TryParse(address, Network.RegTest, out var error));
        Assert.Equal(expected, error is null);
    }

    [Fact]
    public void TryParse_never_echoes_NBitcoins_own_wording()
    {
        // NBitcoin's messages name an encoding the merchant did not choose ("Invalid Bech32 string") and do not
        // distinguish a typo from a mainnet address on a regtest server, which is the mistake worth naming.
        SweepDestinationResolver.TryParse(MainnetAddress, Network.RegTest, out var error);

        Assert.Equal("it is not a valid Bitcoin address for Regtest", error);
    }
}
