using System.Numerics;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Which open quote a completed USDC/USDT receive came from.
/// </summary>
/// <remarks>
/// The SDK hands back no quote id when it quotes and no deposit address when the money arrives, so this is the
/// whole of attribution. A wrong answer credits the wrong invoice with real money; an honest "cannot tell" leaves
/// it in the wallet for a human. Every test here is one of those two, deliberately.
/// </remarks>
public class StablecoinQuoteMatcherTests
{
    private const string BaseUsdc = "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913";

    private static StablecoinQuote Quote(
        string id,
        long asked,
        long expected = 10_000,
        long serviceFee = 80_000,
        string chain = "base",
        string asset = "USDC",
        string? contract = BaseUsdc) => new()
    {
        Id = id,
        StoreId = "store-1",
        InvoiceId = $"invoice-{id}",
        PaymentMethodId = "USDC-FLINT",
        Chain = chain,
        Asset = asset,
        ContractAddress = contract,
        Decimals = 6,
        DepositAddress = $"0xdeposit{id}",
        DepositBaseUnits = asked.ToString(),
        AskedBaseUnits = asked.ToString(),
        PaymentRequest = $"0xdeposit{id}",
        DueAmount = 10m,
        FeeAmount = 0.1m,
        ExpectedReceivedBaseUnits = expected.ToString(),
        DestinationAsset = "BTC",
        ServiceFeeBaseUnits = serviceFee.ToString(),
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
    };

    private static SparkConversionState Arrival(
        long? paid,
        long? estimatedOut = 10_000,
        long? serviceFee = 80_000,
        string chain = "base",
        string asset = "USDC",
        string? contract = BaseUsdc) => new(
        SparkCrossChainProvider.Orchestra,
        SparkConversionStatus.Completed,
        Chain: chain,
        Asset: asset,
        AssetDecimals: 6,
        // The provider's own spelling can differ in case from the route table's; EVM contracts are case-blind.
        AssetContract: contract?.ToLowerInvariant(),
        AssetAmountIn: paid is { } p ? new BigInteger(p) : null,
        EstimatedOut: estimatedOut is { } e ? new BigInteger(e) : null,
        ServiceFeeAmount: serviceFee is { } f ? new BigInteger(f) : null);

    [Fact]
    public void A_unique_fingerprint_names_its_quote()
    {
        var match = StablecoinQuoteMatcher.Match(
            [Quote("a", 10_100_000, expected: 10_000), Quote("b", 10_100_001, expected: 10_050)],
            Arrival(paid: 10_100_001, estimatedOut: 10_050));

        Assert.Equal(StablecoinMatchKind.Matched, match.Kind);
        Assert.Equal("b", match.Quote!.Id);
    }

    [Fact]
    public void A_payer_who_sent_the_wrong_amount_still_paid_the_quote_the_fingerprint_names()
    {
        // The fingerprint is quote-time data frozen by the provider row, so it identifies the address they paid
        // however much they sent. Crediting it records what arrived; refusing would strand a real payment.
        var match = StablecoinQuoteMatcher.Match(
            [Quote("a", 10_100_000, expected: 10_000), Quote("b", 20_200_000, expected: 20_000)],
            Arrival(paid: 9_000_000, estimatedOut: 10_000));

        Assert.Equal(StablecoinMatchKind.Matched, match.Kind);
        Assert.Equal("a", match.Quote!.Id);
    }

    [Theory]
    [InlineData(10_100_001)]
    [InlineData(10_100_000)]
    public void Two_quotes_with_one_fingerprint_are_not_told_apart_by_the_amount_paid(long paid)
    {
        // What the matcher once did, and how it credited the wrong invoice: twin quotes asked 10.1 and 10.100001, and
        // the payer of the second — a Tron QR code carries no amount, so they typed it — sent 10.1. The amount paid is
        // what a wallet or exchange sent, which rounds, and when the provider omits it the SDK reports the quote's own
        // deposit instead; it is not evidence of which address was paid. The service now keeps fingerprints unique,
        // so only quotes from before that can be twins, and those are left for a human.
        var match = StablecoinQuoteMatcher.Match(
            [Quote("a", 10_100_000), Quote("b", 10_100_001)],
            Arrival(paid: paid));

        Assert.Equal(StablecoinMatchKind.Ambiguous, match.Kind);
        Assert.Null(match.Quote);
    }

    [Fact]
    public void A_quote_made_after_the_money_arrived_is_not_a_candidate_for_it()
    {
        var arrivedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var earlier = Quote("a", 10_100_000);
        earlier.CreatedAt = arrivedAt.AddMinutes(-5);
        var later = Quote("b", 10_100_001);
        later.CreatedAt = arrivedAt.AddMinutes(30);

        var match = StablecoinQuoteMatcher.Match([earlier, later], Arrival(paid: 10_100_001), arrivedAt);

        Assert.Equal(StablecoinMatchKind.Matched, match.Kind);
        Assert.Equal("a", match.Quote!.Id);
    }

    [Fact]
    public void An_ambiguous_arrival_stays_ambiguous_as_its_twins_age()
    {
        // The window is the arrival's own. Measured from the clock instead, the older twin would leave it first and the
        // arrival would start "matching" the younger one — whose payer may not have paid at all.
        var arrivedAt = DateTimeOffset.UtcNow.AddHours(-47);
        var older = Quote("a", 10_100_000);
        older.CreatedAt = arrivedAt.AddHours(-2);
        older.ExpiresAt = arrivedAt.AddHours(-1);
        var younger = Quote("b", 10_100_001);
        younger.CreatedAt = arrivedAt.AddMinutes(-10);
        younger.ExpiresAt = arrivedAt.AddMinutes(-5);

        var match = StablecoinQuoteMatcher.Match([older, younger], Arrival(paid: 10_100_001), arrivedAt);

        Assert.Equal(StablecoinMatchKind.Ambiguous, match.Kind);
    }

    [Fact]
    public void A_fingerprint_is_taken_only_by_an_open_quote_on_the_same_route()
    {
        var route = new SparkCrossChainReceiveRoute(
            SparkCrossChainProvider.Orchestra, "base", "8453", "USDC", BaseUsdc.ToLowerInvariant(), 6,
            LandsAsBitcoin: true, LandsAsToken: true, BitcoinLimits: null, Handle: null);
        var quotes = new[] { Quote("a", 10_100_000, expected: 10_000, serviceFee: 80_000) };

        Assert.True(StablecoinQuoteMatcher.FingerprintTaken(quotes, route, 10_000, 80_000));
        Assert.False(StablecoinQuoteMatcher.FingerprintTaken(quotes, route, 10_000, 80_001));
        Assert.False(StablecoinQuoteMatcher.FingerprintTaken(quotes, route with { Chain = "ethereum" }, 10_000, 80_000));
    }

    [Fact]
    public void Two_quotes_with_one_fingerprint_and_an_inexact_payment_are_left_for_a_human()
    {
        var match = StablecoinQuoteMatcher.Match(
            [Quote("a", 10_100_000), Quote("b", 10_100_001)],
            Arrival(paid: 10_000_000));

        Assert.Equal(StablecoinMatchKind.Ambiguous, match.Kind);
        Assert.Null(match.Quote);
        Assert.Equal(2, match.Candidates);
    }

    [Fact]
    public void A_fingerprint_matching_nothing_is_not_attributed_by_amount()
    {
        // The receive came from a quote this plugin did not make — the same recovery phrase receiving in a mobile
        // wallet, say. An equal amount on one of ours is a coincidence, not evidence.
        var match = StablecoinQuoteMatcher.Match(
            [Quote("a", 10_100_000)],
            Arrival(paid: 10_100_000, estimatedOut: 99_999));

        Assert.Equal(StablecoinMatchKind.NoMatch, match.Kind);
    }

    [Theory]
    [InlineData("ethereum", "USDC", BaseUsdc)]
    [InlineData("base", "USDT", BaseUsdc)]
    [InlineData("base", "USDC", "0x0000000000000000000000000000000000000001")]
    public void Only_quotes_on_the_same_route_are_candidates(string chain, string asset, string contract)
    {
        var match = StablecoinQuoteMatcher.Match(
            [Quote("a", 10_100_000)],
            Arrival(paid: 10_100_000, chain: chain, asset: asset, contract: contract));

        Assert.Equal(StablecoinMatchKind.NoMatch, match.Kind);
    }

    [Fact]
    public void Without_a_fingerprint_nothing_attributes_not_even_an_exact_amount()
    {
        // The SDK reports both halves on every Orchestra receive it quoted; one without them is not one of these, and
        // an equal amount on one of ours is no more evidence than it is for twins.
        var quotes = new[] { Quote("a", 10_100_000), Quote("b", 20_200_000) };

        Assert.Equal(StablecoinMatchKind.NoMatch,
            StablecoinQuoteMatcher.Match(quotes, Arrival(paid: 20_200_000, estimatedOut: null, serviceFee: null)).Kind);
        Assert.Equal(StablecoinMatchKind.NoMatch,
            StablecoinQuoteMatcher.Match([Quote("a", 10_100_000)], Arrival(paid: 10_100_000, serviceFee: null)).Kind);
    }

    [Fact]
    public void No_open_quotes_is_no_match()
    {
        Assert.Equal(StablecoinMatchKind.NoMatch, StablecoinQuoteMatcher.Match([], Arrival(paid: 1)).Kind);
    }
}
