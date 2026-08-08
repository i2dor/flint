using System.Security.Claims;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// How seed reuse behaves when BTCPay's hot-wallet service cannot be reached.
/// </summary>
/// <remarks>
/// The happy path needs NBXplorer, a store and a real derivation scheme, so it belongs to the manual
/// checklist rather than here. What <em>is</em> worth pinning down in a unit test is the degradation: the
/// reader is deliberately resolved from the service provider per call so a core release that moves
/// <c>HotwalletSafe</c> greys out one setup option instead of making the whole controller unresolvable and
/// taking the status and removal pages down with it.
/// </remarks>
public class BTCPayHotWalletSeedReaderTests
{
    private static ClaimsPrincipal SignedIn() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-1")], "Identity.Application"));

    [Fact]
    public async Task Reports_unavailable_when_core_does_not_provide_the_hot_wallet_service()
    {
        var reader = new BTCPayHotWalletSeedReader(
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<BTCPayHotWalletSeedReader>.Instance);

        var result = await reader.ReadAsync(SignedIn(), "store-1");

        Assert.Equal(HotWalletSeedStatus.Unavailable, result.Status);
        Assert.False(result.IsAvailable);
        Assert.Null(result.Mnemonic);

        // The setup page renders this under a greyed-out option, so it has to say something a merchant can act
        // on rather than being empty.
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public async Task An_unavailable_reader_cannot_be_mistaken_for_a_usable_seed_by_the_setup_flow()
    {
        // The consequence that matters, rather than the shape of the record: SparkController gates the hot-wallet
        // path on IsAvailable, so a status of Unavailable must make IsAvailable false even though the reader
        // returned a perfectly well-formed result. Asserting the record stored its own constructor arguments
        // would prove nothing.
        var reader = new BTCPayHotWalletSeedReader(
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<BTCPayHotWalletSeedReader>.Instance);

        var result = await reader.ReadAsync(SignedIn(), "store-1");

        Assert.False(result.IsAvailable);

        // And IsAvailable is not merely a restatement of the status: it also requires a seed to actually be
        // present, so a future status change cannot make a null mnemonic look usable.
        Assert.False(HotWalletSeedResult.NotAvailable(HotWalletSeedStatus.Available, "no seed though").IsAvailable);
        Assert.True(HotWalletSeedResult.Found("abandon about").IsAvailable);
    }
}
