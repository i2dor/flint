using System;
using BTCPayServer.Plugins.Flint;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The Breez API key is stored obfuscated so it is not a plain string in a public repository.
/// That only helps if the stored form still decodes to the real key, and a botched
/// re-obfuscation would otherwise surface as every mainnet wallet failing to connect.
/// </summary>
public class BreezApiKeyTests
{
    [Fact]
    public void The_obfuscated_key_decodes_to_a_usable_Breez_key()
    {
        var key = Constants.BreezApiKey;

        // A prefix-and-length check is not enough: the mask repeats every 26 bytes, so corrupting
        // one of its characters leaves the first bytes -- and the length -- untouched. Only a
        // digest over the whole value catches a partial mask change, and a digest can be asserted
        // without putting the key back into the source.
        Assert.Equal(
            "95b35cdc8ad89bd62c32fe7145f6f00786d05bebc78cb84c90891d35fbb75232",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant());
    }

    [Fact]
    public void The_key_is_not_stored_as_a_plain_literal()
    {
        // Guards the point of the exercise: if someone pastes the raw key back into the source,
        // the scanners this protects against start finding it again.
        var source = System.IO.File.ReadAllText(
            System.IO.Path.Combine(RepoRoot(), "BTCPayServer.Plugins.Flint", "Constants.cs"));
        Assert.DoesNotContain(Constants.BreezApiKey, source);
    }

    private static string RepoRoot()
    {
        var dir = System.AppContext.BaseDirectory;
        // The solution file, not LICENSE: LICENSE is copied into the build output so it ships inside the
        // .btcpay, which made it match the bin directory before it matched the repository root.
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "BTCPayServer.Plugins.Flint.slnx")))
            dir = System.IO.Directory.GetParent(dir)?.FullName;
        return dir ?? throw new System.InvalidOperationException("repo root not found");
    }
}
