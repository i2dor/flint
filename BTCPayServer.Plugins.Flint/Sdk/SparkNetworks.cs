using NBitcoin;
using SdkNetwork = Breez.Sdk.Spark.Network;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// Maps BTCPay's network onto the SDK's, which only knows two values.
/// </summary>
/// <remarks>
/// <c>Breez.Sdk.Spark.Network</c> is <c>{ Mainnet = 0, Regtest = 1 }</c>. There is no testnet and no
/// signet, so a BTCPay instance on either must be told plainly rather than silently handed a mainnet
/// wallet — which is what would happen with a naive "not mainnet ⇒ regtest" mapping, and is how a
/// merchant ends up with real funds on a test deployment.
/// <para>
/// The SDK's regtest is Lightspark-hosted. BOLT11 issuance works there with no API key (verified),
/// but paying those invoices needs a regtest Lightning sender, so regtest exercises the create path
/// and not the settle path.
/// </para>
/// </remarks>
public static class SparkNetworks
{
    public static bool TryGetSdkNetwork(ChainName? chainName, out SdkNetwork sdkNetwork, out string? error)
    {
        sdkNetwork = SdkNetwork.Mainnet;
        if (chainName is null)
        {
            error = "No Bitcoin network was supplied.";
            return false;
        }

        if (chainName == ChainName.Mainnet)
        {
            sdkNetwork = SdkNetwork.Mainnet;
            error = null;
            return true;
        }

        if (chainName == ChainName.Regtest)
        {
            sdkNetwork = SdkNetwork.Regtest;
            error = null;
            return true;
        }

        error = $"The Spark SDK supports mainnet and regtest only; this server runs on {chainName}.";
        return false;
    }

    public static bool TryGetSdkNetwork(Network? network, out SdkNetwork sdkNetwork, out string? error)
    {
        if (network is null)
        {
            sdkNetwork = SdkNetwork.Mainnet;
            error = "No Bitcoin network was supplied.";
            return false;
        }

        return TryGetSdkNetwork(network.ChainName, out sdkNetwork, out error);
    }

    /// <summary>
    /// The NBitcoin network to parse BOLT11 invoices against, for a supported chain.
    /// </summary>
    public static Network? ToNBitcoinNetwork(ChainName? chainName)
    {
        if (chainName is null)
            return null;
        if (ChainName.Mainnet.Equals(chainName))
            return Network.Main;
        return ChainName.Regtest.Equals(chainName) ? Network.RegTest : null;
    }
}
