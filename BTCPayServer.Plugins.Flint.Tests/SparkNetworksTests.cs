using BTCPayServer.Plugins.Flint.Sdk;
using NBitcoin;
using Xunit;
using SdkNetwork = Breez.Sdk.Spark.Network;

namespace BTCPayServer.Plugins.Flint.Tests;

public class SparkNetworksTests
{
    [Fact]
    public void Mainnet_maps_to_the_SDK_mainnet()
    {
        Assert.True(SparkNetworks.TryGetSdkNetwork(Network.Main, out var sdkNetwork, out var error));
        Assert.Equal(SdkNetwork.Mainnet, sdkNetwork);
        Assert.Null(error);
    }

    [Fact]
    public void Regtest_maps_to_the_SDK_regtest()
    {
        Assert.True(SparkNetworks.TryGetSdkNetwork(Network.RegTest, out var sdkNetwork, out var error));
        Assert.Equal(SdkNetwork.Regtest, sdkNetwork);
        Assert.Null(error);
    }

    [Fact]
    public void Testnet_and_signet_are_rejected_rather_than_treated_as_regtest()
    {
        // The whole point: the SDK only has Mainnet and Regtest, so a naive "not mainnet means regtest"
        // mapping would hand a testnet BTCPay a real mainnet wallet.
        foreach (var network in new[] { Network.TestNet, Network.TestNet4 })
        {
            Assert.False(SparkNetworks.TryGetSdkNetwork(network, out _, out var error));
            Assert.NotNull(error);
            Assert.Contains("mainnet and regtest only", error);
        }

        // NBitcoin has no ChainName.Signet constant in this version, but the mapping must still reject it.
        foreach (var chainName in new[] { ChainName.Testnet, new ChainName("Signet") })
        {
            Assert.False(SparkNetworks.TryGetSdkNetwork(chainName, out _, out var error));
            Assert.NotNull(error);
            Assert.Contains("mainnet and regtest only", error);
        }
    }

    [Fact]
    public void A_null_network_is_rejected()
    {
        Assert.False(SparkNetworks.TryGetSdkNetwork((Network?)null, out _, out var error));
        Assert.NotNull(error);

        Assert.False(SparkNetworks.TryGetSdkNetwork((ChainName?)null, out _, out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ToNBitcoinNetwork_covers_only_the_supported_chains()
    {
        Assert.Same(Network.Main, SparkNetworks.ToNBitcoinNetwork(ChainName.Mainnet));
        Assert.Same(Network.RegTest, SparkNetworks.ToNBitcoinNetwork(ChainName.Regtest));
        Assert.Null(SparkNetworks.ToNBitcoinNetwork(ChainName.Testnet));
        Assert.Null(SparkNetworks.ToNBitcoinNetwork(null));
    }
}

/// <summary>
/// The wallet fingerprint that keys the single-instance guard.
/// </summary>
/// <remarks>
/// The SDK's hazard is per wallet, not per store: two live instances on one seed corrupt its non-WAL SQLite
/// storage even when their storage directories differ, and both happily mint invoices. Keying the guard on the
/// store would miss the case that makes this reachable in practice — the same BTCPay hot-wallet seed reused on
/// two stores of one server.
/// </remarks>
public class SparkWalletKeyTests
{
    private const string Mnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Fact]
    public void The_same_seed_on_the_same_network_yields_the_same_key()
    {
        Assert.Equal(
            Services.SparkService.DeriveWalletKey(Mnemonic, null, SdkNetwork.Mainnet),
            Services.SparkService.DeriveWalletKey(Mnemonic, null, SdkNetwork.Mainnet));
    }

    [Theory]
    [InlineData("  {0}\n")]
    [InlineData("{0}")]
    public void Cosmetic_differences_in_the_same_seed_do_not_change_the_key(string format)
    {
        // The guard exists to stop two live SDK instances landing on one wallet. Hashing the raw string would
        // let the same seed pasted with a trailing newline, an internal double space, a tab, or different
        // casing produce a different key — and then exactly the corruption this prevents.
        var variants = new[]
        {
            Mnemonic,
            $"  {Mnemonic}\n",
            Mnemonic.Replace(" ", "  "),
            Mnemonic.Replace(" ", "\t"),
            Mnemonic.ToUpperInvariant(),
            $"\n{Mnemonic.Replace(" ", "   ").ToUpperInvariant()}  "
        };

        var keys = variants
            .Select(v => Services.SparkService.DeriveWalletKey(v, null, SdkNetwork.Mainnet))
            .Distinct()
            .ToList();

        Assert.Single(keys);
        _ = format;
    }

    [Fact]
    public void A_passphrase_changes_the_key()
    {
        // A passphrase changes the Spark identity entirely, so two stores with the same words but different
        // passphrases are genuinely different wallets and must not block each other.
        Assert.NotEqual(
            Services.SparkService.DeriveWalletKey(Mnemonic, null, SdkNetwork.Mainnet),
            Services.SparkService.DeriveWalletKey(Mnemonic, "x", SdkNetwork.Mainnet));
    }

    [Fact]
    public void An_unparseable_seed_still_yields_a_consistent_key()
    {
        // Such a seed will fail to connect anyway, but the guard must stay self-consistent rather than throw.
        const string nonsense = "not   a  valid MNEMONIC at all";

        Assert.Equal(
            Services.SparkService.DeriveWalletKey(nonsense, null, SdkNetwork.Mainnet),
            Services.SparkService.DeriveWalletKey($" not a valid mnemonic at   all\n", null, SdkNetwork.Mainnet));
    }

    [Fact]
    public void Trailing_whitespace_does_not_change_the_key()
    {
        Assert.Equal(
            Services.SparkService.DeriveWalletKey(Mnemonic, null, SdkNetwork.Mainnet),
            Services.SparkService.DeriveWalletKey($"  {Mnemonic}\n", null, SdkNetwork.Mainnet));
    }

    [Fact]
    public void A_different_seed_or_network_yields_a_different_key()
    {
        var mainnet = Services.SparkService.DeriveWalletKey(Mnemonic, null, SdkNetwork.Mainnet);

        Assert.NotEqual(mainnet, Services.SparkService.DeriveWalletKey(Mnemonic, null, SdkNetwork.Regtest));
        Assert.NotEqual(mainnet, Services.SparkService.DeriveWalletKey(
            "legal winner thank year wave sausage worth useful legal winner thank yellow",
            null,
            SdkNetwork.Mainnet));
    }

    [Fact]
    public void The_key_does_not_contain_the_seed()
    {
        // It is a fingerprint held in memory, never logged or persisted, but it must not be a way to leak the
        // mnemonic even so.
        var key = Services.SparkService.DeriveWalletKey(Mnemonic, null, SdkNetwork.Mainnet);

        Assert.Equal(64, key.Length);
        Assert.DoesNotContain("abandon", key);
    }
}
