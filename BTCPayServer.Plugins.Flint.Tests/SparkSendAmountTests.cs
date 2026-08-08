using System.Numerics;
using System.Reflection;
using BTCPayServer.Plugins.Flint.Sdk;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The unit-carrying send amount, and the trap it exists to make unrepresentable.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard.</b> <c>PrepareSendPaymentRequest</c> takes <c>BigInteger? amount</c> and
/// <c>string? tokenIdentifier</c> as two independent positional parameters, and the unit of the first silently
/// changes with the presence of the second: satoshi with no token identifier, token base units with one. The
/// literal <c>55000</c> therefore means about $35 in one call and $0.055 in the other, both typed
/// <c>BigInteger?</c>, with nothing in the type system to catch the difference. Turning Stable Balance on moves
/// a store's sweep from the first form to the second.
/// </para>
/// <para>
/// <b>What is asserted here is not that the conversion is correct — it is that the mistake cannot be
/// expressed.</b> A test that only checked <c>ToSdkAmount</c>'s output would pass just as happily against a
/// design where the two arguments were still independent. So these tests go after the structure: that the union
/// is closed, that no constructor takes a bare number without naming its unit, and that both SDK arguments come
/// out of one expression over one value.
/// </para>
/// </remarks>
public class SparkSendAmountTests
{
    /// <summary>
    /// The same literal means different things, and the type says which.
    /// </summary>
    /// <remarks>
    /// The direct statement of the hazard: one number, two units, two entirely different sums of money. Written
    /// as a single test on purpose, so the two readings sit next to each other in the source the way they do not
    /// in the SDK's signature.
    /// </remarks>
    [Fact]
    public void The_same_number_is_a_different_amount_of_money_in_each_unit()
    {
        var sats = SparkSendAmount.FromSats(55_000);
        var tokens = SparkSendAmount.FromTokenBaseUnits(FakeUsdb, 55_000, decimals: 6);

        // About $35 at the rate the spike measured.
        Assert.Equal("55,000 sat", sats.ToString());
        // Five and a half cents.
        Assert.Equal($"0.055 {FakeUsdb}", tokens.ToString());

        // And the SDK arguments they produce differ in the second element, which is the whole point: the unit is
        // not carried by the number, it is carried by whether the identifier is there.
        Assert.Equal((new BigInteger(55_000), null), SparkSdkClient.ToSdkAmount(sats));
        Assert.Equal((new BigInteger(55_000), FakeUsdb.Value), SparkSdkClient.ToSdkAmount(tokens));
    }

    /// <summary>
    /// Both SDK arguments are produced by one expression over one value.
    /// </summary>
    /// <remarks>
    /// This is the structural claim that makes the mitigation real rather than a convention. If the amount and
    /// the token identifier could be assembled separately anywhere, the pairing could be got wrong somewhere —
    /// so the assertion is that every case of the union yields a pair, and that the pairing is total: a
    /// satoshi amount never yields an identifier, and a token amount always does.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryAmountShape))]
    public void An_amount_always_yields_a_matching_identifier(SparkSendAmount amount, bool expectsIdentifier)
    {
        var (value, identifier) = SparkSdkClient.ToSdkAmount(amount);

        Assert.True(value > BigInteger.Zero);
        Assert.Equal(expectsIdentifier, identifier is not null);

        // A token amount's identifier is its own, never some other token's.
        if (amount is SparkSendAmount.Token token)
            Assert.Equal(token.Identifier.Value, identifier);
    }

    public static TheoryData<SparkSendAmount, bool> EveryAmountShape() => new()
    {
        { SparkSendAmount.FromSats(1), false },
        { SparkSendAmount.FromSats(55_000), false },
        { SparkSendAmount.FromTokenBaseUnits(FakeUsdb, 1, 6), true },
        { SparkSendAmount.FromTokenBaseUnits(FakeUsdb, 35_600_000, 6), true },
        // An 18-decimal token, which is BSC's USDT and the case where assuming 6 goes wrong by 10^12.
        { SparkSendAmount.FromTokenBaseUnits(FakeUsdb, BigInteger.Pow(10, 19), 18), true }
    };

    /// <summary>
    /// The union is closed as far as C# permits, and what it does not permit fails loudly instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two properties, and being honest about the gap between them matters more than a green tick.
    /// </para>
    /// <para>
    /// <b>What is enforced.</b> The base's own declared constructor is private and both cases are sealed, so
    /// nothing outside this hierarchy can add a case through the ordinary route, and no existing case can be
    /// subclassed to smuggle different behaviour past a type check.
    /// </para>
    /// <para>
    /// <b>What is not, and cannot be.</b> A positional record gets a compiler-generated <em>protected copy
    /// constructor</em>, and C# offers no way to suppress it — so a determined caller in another assembly can
    /// still derive by chaining to it. That is why the third property below exists: the switch that turns an
    /// amount into SDK arguments <b>throws</b> on an unrecognised case rather than falling through to a default.
    /// A hypothetical third case therefore fails at the point money would move, loudly, instead of being
    /// silently interpreted as satoshi — which is the outcome that actually matters.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_third_kind_of_amount_can_be_defined_or_silently_misread()
    {
        Assert.True(typeof(SparkSendAmount).IsAbstract);

        var declared = typeof(SparkSendAmount)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(c => !c.IsStatic)
            // The record copy constructor, which the compiler emits as protected and which cannot be removed.
            // Excluded by shape rather than by count, so a real second constructor added later is not skipped.
            .Where(c => c.GetParameters() is not [{ } only] || only.ParameterType != typeof(SparkSendAmount))
            .ToList();

        Assert.NotEmpty(declared);
        Assert.All(declared, c => Assert.True(
            c.IsPrivate,
            "SparkSendAmount declares a constructor that is not private, so a third case can be added from "
            + "outside. The unit of an SDK send is decided by switching over these cases."));

        Assert.True(typeof(SparkSendAmount.Bitcoin).IsSealed);
        Assert.True(typeof(SparkSendAmount.Token).IsSealed);

        // And the backstop for the gap C# leaves: an unrecognised case is refused, not assumed to be satoshi.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SparkSdkClient.ToSdkAmount(new AmountFromAnotherAssembly(SparkSendAmount.FromSats(1))));
    }

    /// <summary>
    /// The case C# cannot prevent: a third variant chained through the record copy constructor.
    /// </summary>
    /// <remarks>
    /// Declared here rather than described in prose, so the claim that it is refused is a fact about the
    /// running code rather than an assertion about the language.
    /// </remarks>
    private sealed record AmountFromAnotherAssembly : SparkSendAmount
    {
        public AmountFromAnotherAssembly(SparkSendAmount original) : base(original)
        {
        }

        public override string ToString() => "an amount with no unit at all";
    }

    /// <summary>
    /// Neither case can be built from a bare number without naming a unit.
    /// </summary>
    /// <remarks>
    /// The other half of unrepresentability. A public constructor taking a <c>long</c>, or an implicit
    /// conversion from one, would let a caller produce an amount without deciding what it meant — which is
    /// exactly the state the SDK leaves you in.
    /// </remarks>
    [Fact]
    public void An_amount_cannot_be_built_from_a_bare_number()
    {
        foreach (var type in new[] { typeof(SparkSendAmount.Bitcoin), typeof(SparkSendAmount.Token) })
        {
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

            Assert.DoesNotContain(
                type.GetMethods(BindingFlags.Public | BindingFlags.Static),
                m => m.Name is "op_Implicit" or "op_Explicit");
        }

        // The two factories both name their unit, and there is no third.
        var factories = typeof(SparkSendAmount)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(SparkSendAmount))
            .Select(m => m.Name)
            .ToHashSet();

        Assert.Equal(new HashSet<string> { nameof(SparkSendAmount.FromSats), nameof(SparkSendAmount.FromTokenBaseUnits) },
            factories);
    }

    /// <summary>
    /// An amount knows whether its send may carry an idempotency key.
    /// </summary>
    /// <remarks>
    /// Read off the amount rather than held as a separate flag, so the two cannot disagree — and they are the
    /// same fact: the SDK's rejection turns on whether the first leg is a token transfer, which is precisely
    /// what the union names. See <c>SparkCrossChainSendTests</c> for the behaviour this drives.
    /// </remarks>
    [Fact]
    public void Only_a_satoshi_amount_may_carry_an_idempotency_key()
    {
        Assert.True(SparkSendAmount.FromSats(55_000).SupportsIdempotencyKey);
        Assert.False(SparkSendAmount.FromTokenBaseUnits(FakeUsdb, 35_600_000, 6).SupportsIdempotencyKey);
    }

    /// <summary>
    /// A non-positive amount is refused at construction.
    /// </summary>
    /// <remarks>
    /// Refused where the value is made rather than where it is used, because "send zero" and "send minus one"
    /// have no meaning on either rail and a zero that reached a prepare would be interpreted as an amountless
    /// request rather than rejected.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_amount_cannot_be_constructed(long value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SparkSendAmount.FromSats(value));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SparkSendAmount.FromTokenBaseUnits(FakeUsdb, value, 6));
    }

    /// <summary>
    /// A token identifier is case-insensitive, because bech32m is.
    /// </summary>
    /// <remarks>
    /// <b>The failure this prevents is silent and expensive.</b> The identifier is an operator-overridable
    /// setting, so it gets pasted by hand — and bech32m is case-insensitive, so a paste carrying any uppercase
    /// is the same token. Comparison against the wallet's reported balances is ordinal, though, so a mixed-case
    /// identifier would match nothing: Stable Balance would look configured, the token balance would never be
    /// found, and a cross-chain sweep would quietly fall back to the satoshi rail while the merchant's dollars
    /// sat untouched. Normalising at construction is what makes the ordinal comparison downstream correct
    /// rather than lucky.
    /// </remarks>
    [Theory]
    [InlineData("BTKN1XGRVJWEY5NGCAGVAP2DZZVSY4UK8UA9X69K82DWVT5E7EF9DRM9QZTUX87")]
    [InlineData("Btkn1XgrvJwey5ngcagvap2dzzvsy4uk8ua9x69k82dwvt5e7ef9drm9qztux87")]
    [InlineData("  btkn1xgrvjwey5ngcagvap2dzzvsy4uk8ua9x69k82dwvt5e7ef9drm9qztux87  ")]
    public void A_token_identifier_is_the_same_token_whatever_its_case(string pasted)
    {
        Assert.Equal(FakeUsdb, new SparkTokenIdentifier(pasted));
    }

    /// <summary>
    /// And that equality is what a token balance lookup depends on.
    /// </summary>
    /// <remarks>
    /// The end the normalisation exists for: the wallet reports its balances under the identifier the SDK uses,
    /// and the store's configured identifier has to find them.
    /// </remarks>
    [Fact]
    public void A_balance_is_found_under_an_identifier_pasted_in_the_wrong_case()
    {
        var info = new SparkNodeInfo(
            "02aa", 500_000,
            [new SparkTokenBalance(FakeUsdb, 35_600_000, "USDB", "Bitcoin USD", 6, IsFreezable: true)]);

        var configured = new SparkTokenIdentifier(
            StableBalanceSettings.DefaultTokenIdentifier.ToUpperInvariant());

        Assert.NotNull(info.TokenBalance(configured));
    }

    [Fact]
    public void A_blank_token_identifier_cannot_be_constructed()
    {
        Assert.Throws<ArgumentException>(() => new SparkTokenIdentifier(""));
        Assert.Throws<ArgumentException>(() => new SparkTokenIdentifier("   "));
    }

    /// <summary>
    /// Base units render as a decimal quantity, exactly, at any scale.
    /// </summary>
    /// <remarks>
    /// Integer arithmetic on <see cref="BigInteger"/> rather than a conversion through <c>double</c> or
    /// <c>decimal</c>: an 18-decimal token's base units exceed <c>decimal</c>'s range for quite ordinary
    /// amounts, and a lossy render of a money figure is how a merchant is shown the wrong number. The
    /// 18-decimal case below is the one that would fail against either shortcut.
    /// </remarks>
    [Theory]
    [InlineData("35600000", 6u, "35.6")]
    [InlineData("35721666", 6u, "35.721666")]
    [InlineData("1", 6u, "0.000001")]
    [InlineData("1000000", 6u, "1")]
    [InlineData("0", 6u, "0")]
    [InlineData("1234567890000", 6u, "1,234,567.89")]
    // 18 decimals — BSC's USDT. Ten ether-scale units, exactly.
    [InlineData("10000000000000000000", 18u, "10")]
    [InlineData("12345678901234567890", 18u, "12.34567890123456789")]
    [InlineData("42", 0u, "42")]
    public void Base_units_render_exactly(string baseUnits, uint decimals, string expected)
    {
        Assert.Equal(expected, SparkSendAmount.FormatBaseUnits(BigInteger.Parse(baseUnits), decimals));
    }

    private static readonly SparkTokenIdentifier FakeUsdb =
        new(StableBalanceSettings.DefaultTokenIdentifier);
}
