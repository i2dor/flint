using System.Numerics;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// A fee ceiling that is not a finite number must not become no fee ceiling.
/// </summary>
/// <remarks>
/// <para>
/// Every fee guard here is arithmetic on a <c>double</c> that came from outside: JSON deserialises
/// <c>1e400</c> to positive infinity without complaining, and a hand-edited or restored settings blob can
/// carry <c>NaN</c> straight past a range check written as <c>&lt; 0 || &gt; 100</c>, because every
/// comparison against NaN is false. What follows is a multiplication into a ceiling that money is measured
/// against.
/// </para>
/// <para>
/// It is safe today, and the point of these tests is that it stays safe by construction rather than by
/// luck. The two properties that make it work are easy to break without noticing: .NET saturates a
/// double-to-long conversion rather than wrapping it, so infinity becomes <c>long.MaxValue</c> and not
/// <c>long.MinValue</c>; and the hard backstop is applied with <c>Math.Min</c> after the merchant's own
/// figure, so an infinite ceiling is reduced to the 50% line instead of replacing it. Reorder those two, or
/// swap the saturating cast for a checked or wrapping one, and an unbounded fee becomes reachable from a
/// settings blob.
/// </para>
/// </remarks>
public class SparkFeeGuardExtremeValueTests
{
    private const string Evm = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";

    private static readonly DateTimeOffset Origin = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Values a <c>double</c> can hold that a percentage should not.
    /// </summary>
    /// <remarks>
    /// <c>1e400</c> is the literal from the audit; C# will not compile it as a <c>double</c>, but
    /// <c>JsonConvert</c> produces exactly <see cref="double.PositiveInfinity"/> from it, so that is what is
    /// pinned. <c>1e308</c> is finite and does <em>not</em> overflow on its own — it overflows once multiplied
    /// by a recipient amount, which is the operation the guard actually performs.
    /// </remarks>
    public static TheoryData<double> Extremes =>
    [
        double.PositiveInfinity,
        double.NegativeInfinity,
        double.NaN,
        1e308,
        -1e308,
        -1.0,
        double.MaxValue,
        double.Epsilon
    ];

    /// <summary>
    /// The literal from the audit really does deserialise to infinity, rather than being rejected.
    /// </summary>
    /// <remarks>
    /// Pinned because it is the premise of everything below. If BTCPay's serializer ever started refusing it,
    /// these tests would be guarding against an input that can no longer arrive — worth knowing rather than
    /// discovering.
    /// </remarks>
    [Fact]
    public void The_JSON_literal_1e400_arrives_as_infinity()
    {
        var input = Newtonsoft.Json.JsonConvert.DeserializeObject<SweepSettingsInput>(
            """{"maxFeePercent": 1e400}""", Fakes.ApiJson.Settings);

        Assert.NotNull(input);
        Assert.True(double.IsPositiveInfinity(input!.MaxFeePercent));
    }

    /// <summary>
    /// Validation refuses an infinite percentage outright, and lets NaN through.
    /// </summary>
    /// <remarks>
    /// The asymmetry is the interesting half and is deliberately pinned rather than fixed: NaN fails every
    /// comparison, so <c>&lt; 0 || &gt; 100</c> cannot see it. It is safe only because every guard downstream
    /// also fails its comparison against NaN and falls back to a real default — which is what the tests below
    /// assert. If that ever stops being true, this is the door.
    /// </remarks>
    [Fact]
    public void Validation_refuses_an_infinite_percentage_but_cannot_see_NaN()
    {
        Assert.Contains(
            Validate(double.PositiveInfinity),
            error => error.Field == nameof(SweepSettingsInput.MaxFeePercent));

        Assert.DoesNotContain(
            Validate(double.NaN),
            error => error.Field == nameof(SweepSettingsInput.MaxFeePercent));
    }

    /// <summary>
    /// However the percentage got there, the cooperative-exit guard still refuses an outsized fee.
    /// </summary>
    /// <remarks>
    /// The quote is a 60% fee: outside the 50% backstop by a margin no configuration may lift. Every value in
    /// <see cref="Extremes"/> must land on a refusal, whether by clamping to the backstop or by failing its
    /// own comparison and falling back to the default.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Extremes))]
    public void No_percentage_a_double_can_hold_lifts_the_coop_exit_fee_ceiling(double percent)
    {
        var settings = new SweepSettings { MaxFeePercent = percent, MaxFeeFlatSats = null };

        // 100,000 gross, 60,000 fee, so the destination receives 40,000 — a 150% fee against what arrives.
        var quote = new SparkOnchainQuote(
            100_000, 60_000, FeesIncluded: true, new SparkOnchainFeeQuote("q", Origin, 60_000, 60_000, 60_000));

        var refusal = SparkSweepEngine.ApproveQuote(settings, quote);

        Assert.NotNull(refusal);
        Assert.Equal(SweepRefusalCode.FeeAboveLimit, refusal!.Code);
    }

    /// <summary>
    /// And an infinite flat ceiling alongside it is no better.
    /// </summary>
    /// <remarks>
    /// <c>MaxFeeFlatSats</c> is a <c>long</c>, so it cannot be infinite — but pairing the largest one there is
    /// with an infinite percentage is the closest a stored blob can get to "no limit at all", and it is the
    /// shape the Wave 4 mutation found once already.
    /// </remarks>
    [Fact]
    public void An_infinite_percentage_beside_the_largest_flat_ceiling_still_refuses()
    {
        var settings = new SweepSettings
        {
            MaxFeePercent = double.PositiveInfinity,
            MaxFeeFlatSats = long.MaxValue
        };

        var quote = new SparkOnchainQuote(
            100_000, 60_000, FeesIncluded: true, new SparkOnchainFeeQuote("q", Origin, 60_000, 60_000, 60_000));

        Assert.Equal(SweepRefusalCode.FeeAboveLimit, SparkSweepEngine.ApproveQuote(settings, quote)?.Code);
    }

    /// <summary>
    /// The same, on the cross-chain rail, which computes its ceiling differently.
    /// </summary>
    /// <remarks>
    /// A separate arithmetic path: it clamps the <em>percentage</em> with <c>Math.Min</c> before comparing,
    /// where the cooperative-exit guard clamps the resulting satoshi figure. Both have to hold, and a test
    /// covering one says nothing about the other.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Extremes))]
    public void No_percentage_a_double_can_hold_lifts_the_cross_chain_fee_ceiling(double percent)
    {
        var settings = new SweepSettings { MaxFeePercent = percent, MaxFeeFlatSats = null };

        // A 60% spread: 1,000,000 in, 400,000 out.
        var refusal = SparkSweepEngine.ApproveCrossChainQuote(
            settings, SparkSendAmount.FromSats(500_000), Quote(1_000_000, 400_000), sweepableSats: 500_000);

        Assert.NotNull(refusal);
        Assert.Equal(SweepRefusalCode.FeeAboveLimit, refusal!.Code);
    }

    /// <summary>
    /// The guards are still ordinary guards: a fee inside the line is allowed.
    /// </summary>
    /// <remarks>
    /// Without this, a refusal on every input would satisfy every test above — including a mutation that
    /// simply refuses everything, which would be a plugin that never sweeps.
    /// </remarks>
    [Fact]
    public void A_fee_inside_the_backstop_is_still_allowed()
    {
        var settings = new SweepSettings { MaxFeePercent = 40, MaxFeeFlatSats = null };

        // 100,000 gross, 25,000 fee, 75,000 delivered: 33% of what arrives, inside the merchant's 40% and
        // inside the 50% backstop. The cross-chain quote below is a 30% spread, likewise inside both.
        var quote = new SparkOnchainQuote(
            100_000, 25_000, FeesIncluded: true, new SparkOnchainFeeQuote("q", Origin, 25_000, 25_000, 25_000));

        Assert.Null(SparkSweepEngine.ApproveQuote(settings, quote));
        Assert.Null(SparkSweepEngine.ApproveCrossChainQuote(
            settings, SparkSendAmount.FromSats(500_000), Quote(1_000_000, 700_000), sweepableSats: 500_000));
    }

    /// <summary>
    /// A caller-supplied NaN on a Lightning payout falls back to the plugin's default, not to no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A different call site with a different contract, and worth being precise about which. Here the
    /// percentage comes from the <c>PayInvoiceParams</c> of a payout the merchant configured, so an
    /// <em>explicit</em> very large limit is the caller authorising it — <c>MaxFeeFlat</c> is a
    /// <c>LightMoney</c> with no ceiling either, so a hard cap here would be a new restriction rather than a
    /// closed hole.
    /// </para>
    /// <para>
    /// NaN is not an explicit limit, though. It is the absence of a usable one, and it must land on the
    /// plugin's own default rather than on <c>long.MaxValue</c> — which is what "and &gt; 0" achieves, since
    /// every comparison against NaN is false.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    public void A_Lightning_payout_with_no_usable_percentage_falls_back_to_the_default_limit(double percent)
    {
        // 1,000 sat payment: the default 3% ceiling is 30 sat, and the flat floor is 25, so 30 applies.
        var quote = new SparkSendQuote(1_000, 40, "payment-hash");

        var refusal = SparkLightningClient.ApproveFee(
            quote, new PayInvoiceParams { MaxFeePercent = percent }, amountSats: 1_000);

        Assert.NotNull(refusal);
        Assert.Contains("default limit", refusal);

        // And the default really is what applied, rather than the fee simply being enormous.
        Assert.Null(SparkLightningClient.ApproveFee(
            new SparkSendQuote(1_000, 30, "payment-hash"),
            new PayInvoiceParams { MaxFeePercent = percent },
            amountSats: 1_000));
    }

    private static IReadOnlyList<SparkSweepSettingsError> Validate(double percent)
    {
        var input = new SweepSettingsInput
        {
            Enabled = true,
            DestinationMode = SweepDestinationMode.StaticAddress,
            StaticAddress = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString(),
            MaxFeePercent = percent
        };

        return input.Validate(Network.RegTest)
            .Select(pair => new SparkSweepSettingsError(pair.Field, pair.Error))
            .ToList();
    }

    private static SparkCrossChainQuote Quote(int assetIn, int estimatedOut) => new(
        new SparkCrossChainRoute(
            SparkCrossChainProvider.Orchestra, "arbitrum", "42161", "USDT", null, 6,
            [SparkCrossChainSource.Bitcoin], "handle"),
        Evm,
        AmountInSats: 500_000,
        AssetAmountIn: new BigInteger(assetIn),
        EstimatedOut: new BigInteger(estimatedOut),
        FeeAmount: new BigInteger(assetIn - estimatedOut),
        ServiceFeeAmount: BigInteger.Zero,
        ServiceFeeAsset: "USDC",
        SourceTransferFeeSats: 0,
        ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(60),
        ProviderQuoteId: "q",
        ProviderDepositAddress: "spark1");
}
