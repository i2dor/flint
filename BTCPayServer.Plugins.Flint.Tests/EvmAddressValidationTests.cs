using BTCPayServer.Plugins.Flint.Services;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// EVM address validation, including the EIP-55 checksum.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the checksum is worth implementing rather than leaving to the SDK.</b> Delivery to an EVM address is
/// irreversible and unattributable: there is no bounce, no refund and nobody to ask. The SDK's parser decides
/// the address <em>family</em> — it cannot tell you the merchant typed the address they meant. Two transposed
/// digits produce a string that is 42 characters of valid hex and sends a store's USDT to nobody, permanently.
/// </para>
/// <para>
/// A mixed-case address carries the checksum in its capitalisation, so transposition is detectable; a
/// uniformly-cased one carries no checksum and cannot be checked, which is why those are accepted as-is rather
/// than refused.
/// </para>
/// </remarks>
public class EvmAddressValidationTests
{
    /// <summary>
    /// The canonical EIP-55 vectors from the specification itself.
    /// </summary>
    /// <remarks>
    /// Taken from EIP-55's own test list rather than generated, because the point is to check this
    /// implementation of keccak-256 against an authority. Getting the hash subtly wrong — keccak's padding
    /// differs from SHA3's by one byte — would produce a check that rejects good addresses and accepts bad
    /// ones, which is worse than no check at all.
    /// </remarks>
    [Theory]
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed")]
    [InlineData("0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359")]
    [InlineData("0xdbF03B407c01E7cD3CBea99509d93f8DDDC8C6FB")]
    [InlineData("0xD1220A0cf47c7B9Be7A2E6BA89F429762e7b9aDb")]
    // All-caps and all-lower forms of the first vector: no checksum, so nothing to fail.
    [InlineData("0x52908400098527886E0F7030069857D2E4169EE7")]
    [InlineData("0x27b1fdb04752bbc536007a920d24acb045561c26")]
    // The address the rest of the suite uses.
    [InlineData("0x742d35Cc6634C0532925a3b844Bc454e4438f44e")]
    public void A_correctly_checksummed_address_is_accepted(string address)
    {
        Assert.True(
            SweepDestinationResolver.TryParseEvm(address, out var normalised, out var error),
            $"{address} was rejected: {error}");
        Assert.Equal(address, normalised);
    }

    /// <summary>
    /// A checksummed address with two digits transposed is refused.
    /// </summary>
    /// <remarks>
    /// The mistake this exists for. Both of these are 42 characters of valid hex and differ from a real address
    /// by a swap a human eye slides straight over; both change the expected capitalisation, so the checksum
    /// catches them.
    /// </remarks>
    [Theory]
    // 0x5aAeb60...  with the 6 and 0 of "6053" swapped.
    [InlineData("0x5aAeb6035F3E94C9b9A09f33669435E7Ef1BeAed")]
    // 0x742d35Cc... with the 4 and 2 of "742d" swapped.
    [InlineData("0x724d35Cc6634C0532925a3b844Bc454e4438f44e")]
    // A single wrong digit.
    [InlineData("0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d358")]
    public void A_checksummed_address_with_a_typo_is_refused(string address)
    {
        Assert.False(SweepDestinationResolver.TryParseEvm(address, out var normalised, out var error));
        Assert.Null(normalised);
        Assert.Contains("checksum", error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A uniformly-cased address carries no checksum, so the same typo cannot be caught — and is accepted.
    /// </summary>
    /// <remarks>
    /// Stated explicitly rather than left implicit, because it is the limit of the guard. Plenty of legitimate
    /// addresses are handed around in lower case and refusing them would refuse destinations that work, so this
    /// is a deliberate gap rather than an oversight.
    /// </remarks>
    [Fact]
    public void A_lower_case_address_is_accepted_because_it_carries_no_checksum_to_check()
    {
        Assert.True(SweepDestinationResolver.TryParseEvm(
            "0x724d35cc6634c0532925a3b844bc454e4438f44e", out _, out _));
    }

    [Theory]
    [InlineData(null, "no address")]
    [InlineData("", "no address")]
    [InlineData("   ", "no address")]
    [InlineData("742d35Cc6634C0532925a3b844Bc454e4438f44e", "0x")]
    [InlineData("0x742d35Cc6634C0532925a3b844Bc454e4438f44", "42 characters")]
    [InlineData("0x742d35Cc6634C0532925a3b844Bc454e4438f44ee", "42 characters")]
    [InlineData("0x742d35Cc6634C0532925a3b844Bc454e4438f44z", "hexadecimal")]
    public void A_structurally_wrong_address_is_refused_with_a_reason(string? address, string expected)
    {
        Assert.False(SweepDestinationResolver.TryParseEvm(address, out var normalised, out var error));
        Assert.Null(normalised);
        Assert.Contains(expected, error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The checksum reaches the settings form and the send path, not just this helper.
    /// </summary>
    /// <remarks>
    /// Both surfaces call the same static, and the send path re-validates rather than trusting the save — a
    /// settings blob can arrive from a backup or a hand edit. Asserted through the destination resolver so the
    /// wiring is covered rather than assumed.
    /// </remarks>
    [Fact]
    public async Task A_mistyped_address_is_refused_at_send_time_and_not_only_on_save()
    {
        var resolver = new SweepDestinationResolver(
            new Fakes.FakeSweepAddressSource(),
            NBitcoin.Network.Main,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SweepDestinationResolver>.Instance);

        var resolution = await resolver.ResolveAsync(
            "store-1",
            new SweepSettings
            {
                DestinationMode = SweepDestinationMode.EvmAddress,
                // Stored by some other route: a restored backup, a hand edit, an older validator.
                EvmAddress = "0x724d35Cc6634C0532925a3b844Bc454e4438f44e"
            },
            reserve: false,
            TestContext.Current.CancellationToken);

        Assert.Null(resolution.Destination);
        Assert.Contains("checksum", resolution.RefusalReason!, StringComparison.OrdinalIgnoreCase);
    }
}
