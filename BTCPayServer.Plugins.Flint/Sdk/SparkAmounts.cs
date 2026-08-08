using System;
using System.Globalization;
using System.Numerics;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// A Spark token identifier (a <c>btkn1…</c> bech32m string), as a type rather than a bare string.
/// </summary>
/// <remarks>
/// <para>
/// A newtype for one reason: the SDK has three different string-shaped identifiers in play on the same call —
/// a token identifier, a chain name, and an EVM contract address — and <c>PrepareSendPaymentRequest</c> takes
/// the token one as a positional <c>string?</c>. A plain string is assignable from any of them.
/// </para>
/// <para>
/// It is deliberately <b>not</b> validated beyond being non-blank. The plugin ships the mainnet USDB identifier
/// as a default an operator may override (spike §1.6 is explicit that it should be configurable), and the only
/// authority on whether an identifier is real is the SDK itself — <c>GetCrossChainRoutes</c> and
/// <c>FetchConversionLimits</c> both reject an unknown one. A local regex would only be able to reject
/// identifiers that are in fact fine.
/// </para>
/// </remarks>
public readonly record struct SparkTokenIdentifier
{
    public SparkTokenIdentifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        // Lower-cased, not merely trimmed.
        //
        // bech32m is case-insensitive, so an operator pasting the USDB identifier with any uppercase in it has
        // written the same token — but comparison here is ordinal, so the mixed-case form would not match the
        // identifier the wallet reports on its balances. Stable Balance would then look configured, the token
        // balance would never be found, and a cross-chain sweep would silently fall back to the satoshi rail
        // while the merchant's dollars sat untouched in the wallet. Normalising at construction is what makes
        // the ordinal comparison downstream correct rather than lucky.
        Value = value.Trim().ToLowerInvariant();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// The amount a send moves, carrying its own unit.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists because of the sharpest edge in the whole Breez API</b> (spike §2.7, gotcha 2).
/// <c>PrepareSendPaymentRequest</c> takes <c>BigInteger? amount</c> and <c>string? tokenIdentifier</c> as two
/// independent positional parameters, and the <em>unit of the first silently changes with the presence of the
/// second</em>:
/// </para>
/// <list type="bullet">
/// <item><description><c>tokenIdentifier = null</c> ⇒ <c>amount</c> is <b>satoshis</b>.</description></item>
/// <item><description><c>tokenIdentifier</c> set ⇒ <c>amount</c> is <b>token base units</b>, scaled by the
/// token's decimals — 6 for USDB and for USDT on every Orchestra chain except BSC, which is
/// <b>18</b>.</description></item>
/// </list>
/// <para>
/// The literal <c>55000</c> therefore means ~$35 in one call and $0.055 in the other, both typed
/// <c>BigInteger?</c>, with nothing to catch the mistake. Turning Stable Balance on flips a store's sweep from
/// the first form to the second, so the unit of the sweep amount changes underneath a merchant who changed a
/// checkbox.
/// </para>
/// <para>
/// <b>How this makes the mistake unrepresentable.</b> The pair is no longer two parameters: it is one closed
/// union with exactly two cases, and <see cref="SparkSdkClient"/> derives <em>both</em> SDK arguments from it in
/// a single expression (<c>SparkSdkClient.ToSdkAmount</c>). There is no code path that can set an amount without
/// simultaneously deciding the unit, no path that can set a token identifier without an amount in that token's
/// units, and no implicit conversion from a bare number to either case — <see cref="FromSats"/> and
/// <see cref="FromTokenBaseUnits"/> are the only constructors, and they name their unit. A caller that has a
/// number and does not know which case it is cannot compile.
/// </para>
/// <para>
/// It also carries <see cref="Decimals"/> so a display string can be produced without a second lookup, and so
/// nothing has to assume 6. <c>CrossChainRoutePair.decimals</c> is the authority for a cross-chain send and
/// <c>TokenMetadata.decimals</c> for a balance; both are read rather than guessed.
/// </para>
/// </remarks>
public abstract record SparkSendAmount
{
    private SparkSendAmount()
    {
    }

    /// <summary>An amount denominated in satoshi, spent from the wallet's BTC balance.</summary>
    public sealed record Bitcoin : SparkSendAmount
    {
        internal Bitcoin(long sats)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sats);
            Sats = sats;
        }

        public long Sats { get; }

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0:N0} sat", Sats);
    }

    /// <summary>
    /// An amount denominated in a token's base units, spent from that token's balance.
    /// </summary>
    /// <param name="Token">Which token. Also what the SDK's <c>tokenIdentifier</c> argument is set from.</param>
    /// <param name="BaseUnits">
    /// The integer amount in base units — <b>not</b> a decimal quantity and <b>not</b> satoshi. $35.60 of a
    /// 6-decimal token is <c>35_600_000</c>.
    /// </param>
    /// <param name="Decimals">
    /// The token's decimal places, read from the route or from token metadata. Used only for display; never
    /// assume 6, because BSC's USDT is 18.
    /// </param>
    public sealed record Token : SparkSendAmount
    {
        internal Token(SparkTokenIdentifier token, BigInteger baseUnits, uint decimals)
        {
            if (baseUnits <= BigInteger.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(baseUnits), baseUnits, "A token send amount must be positive.");
            }

            Identifier = token;
            BaseUnits = baseUnits;
            Decimals = decimals;
        }

        public SparkTokenIdentifier Identifier { get; }
        public BigInteger BaseUnits { get; }
        public uint Decimals { get; }

        public override string ToString() => $"{FormatBaseUnits(BaseUnits, Decimals)} {Identifier}";
    }

    /// <summary>An amount in satoshi, spent from the BTC balance.</summary>
    public static SparkSendAmount FromSats(long sats) => new Bitcoin(sats);

    /// <summary>An amount in a token's base units, spent from that token's balance.</summary>
    public static SparkSendAmount FromTokenBaseUnits(
        SparkTokenIdentifier token,
        BigInteger baseUnits,
        uint decimals) =>
        new Token(token, baseUnits, decimals);

    /// <summary>
    /// Whether a send of this amount may carry an SDK idempotency key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>False for a token amount, and this is a rejection rather than an omission.</b> The SDK's exact wording
    /// (spike §2.9) is <c>Idempotency key is not supported for payments with a token transfer leg (direct token
    /// send or AMM conversion)</c>, raised as <c>SdkError::InvalidInput</c> — so passing one does not merely lose
    /// deduplication, it fails the send outright.
    /// </para>
    /// <para>
    /// The determinant is the first leg the SDK actually executes, which is exactly what this union names: a
    /// <see cref="Bitcoin"/> amount makes the first leg a Spark <em>sats</em> transfer to the provider's deposit
    /// address (key supported, so the key-becomes-<c>Payment.id</c> crash-safety primitive holds), while a
    /// <see cref="Token"/> amount makes it a Spark <em>token</em> transfer (key rejected). Reading the property
    /// off the amount rather than off a separate flag means the two cannot disagree.
    /// </para>
    /// <para>
    /// What replaces the primitive on the token path is documented on
    /// <c>SweepRecord.ProviderQuoteId</c>: persist the provider's quote id before the send and reconcile by
    /// scanning payments for it, never by re-sending.
    /// </para>
    /// </remarks>
    public bool SupportsIdempotencyKey => this is Bitcoin;

    /// <summary>A human-readable amount with its unit, for a merchant-facing message.</summary>
    public abstract override string ToString();

    /// <summary>
    /// Renders base units as a decimal quantity: <c>35600000</c> at 6 decimals is <c>35.6</c>.
    /// </summary>
    /// <remarks>
    /// Integer arithmetic on <see cref="BigInteger"/> rather than a conversion to <c>double</c> or
    /// <c>decimal</c>. An 18-decimal token's base units exceed <c>decimal</c>'s range for quite ordinary amounts,
    /// and a lossy display of a money figure is how a merchant is told the wrong number.
    /// </remarks>
    public static string FormatBaseUnits(BigInteger baseUnits, uint decimals)
    {
        if (decimals == 0)
            return baseUnits.ToString("N0", CultureInfo.InvariantCulture);

        var negative = baseUnits.Sign < 0;
        var magnitude = BigInteger.Abs(baseUnits);
        var scale = BigInteger.Pow(10, (int)Math.Min(decimals, 64));
        var whole = BigInteger.DivRem(magnitude, scale, out var fraction);

        var fractionText = fraction
            .ToString(CultureInfo.InvariantCulture)
            .PadLeft((int)Math.Min(decimals, 64), '0')
            .TrimEnd('0');

        var text = fractionText.Length == 0
            ? whole.ToString("N0", CultureInfo.InvariantCulture)
            : $"{whole.ToString("N0", CultureInfo.InvariantCulture)}.{fractionText}";

        return negative ? "-" + text : text;
    }
}
