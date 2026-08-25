using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using NBitcoin;
using NBitcoin.Crypto;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The load-bearing details of the real credit gateway, tested away from BTCPay's database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are here rather than as an integration test.</b> <see cref="BTCPayInvoiceCreditGateway"/> writes
/// through core's <c>InvoiceRepository</c> and <c>PaymentService</c>, which need a real
/// <c>ApplicationDbContext</c> and a Postgres database to construct — out of reach of this suite. What can be
/// tested, and what actually carries the risk, is the pure logic those calls are wrapped around: a byte-order
/// convention, a unit conversion, and a decision about whose preimage may be written where. Every one of them
/// was hand-verified against core and nothing else, which is exactly the state a regression hides in.
/// </para>
/// <para>
/// The remaining seam — that the container really can hand this class the deferred <c>PaymentService</c> and
/// <c>PaymentMethodHandlerDictionary</c> it resolves on first use — is covered in
/// <see cref="SparkPluginStartupTests"/>, because that needs BTCPay's own container and this does not.
/// </para>
/// </remarks>
public class BTCPayInvoiceCreditGatewayTests
{
    private static uint256 HashOf(string preimageHex) =>
        new(Convert.ToHexStringLower(Hashes.SHA256(Convert.FromHexString(preimageHex))));

    // -----------------------------------------------------------------------------------------------------------
    // Preimage validation: two byte orders, and they are not interchangeable
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_preimage_that_hashes_to_the_payment_hash_is_stored_in_BTCPays_byte_order()
    {
        // The convention core uses for this field, replicated — and the reversal in it is not cosmetic. What is
        // stored is a uint256 built from the *reversed* wire bytes, because uint256 takes its bytes
        // little-endian and renders them big-endian; the two cancel, and the field reads back as the preimage
        // hex. That readback is the whole point: LUD-21 verify serves this value to the payer as their proof of
        // payment. Drop the reversal and the payment is still credited, the field still looks populated, and
        // every proof it serves is byte-reversed garbage that nothing in a running server would flag.
        var paymentHash = HashOf(PaymentFixture.Preimage);

        var validity = BTCPayInvoiceCreditGateway.ValidatePreimage(
            PaymentFixture.Preimage, paymentHash, out var stored);

        Assert.Equal(BTCPayInvoiceCreditGateway.PreimageValidity.Valid, validity);
        Assert.NotNull(stored);
        Assert.Equal(PaymentFixture.Preimage, stored.ToString());

        // And the check a payer performs on what they were served: hash it, and land back on the payment hash.
        Assert.Equal(
            paymentHash.ToString(),
            Convert.ToHexStringLower(Hashes.SHA256(Convert.FromHexString(stored.ToString()))));
    }

    [Fact]
    public void The_known_fixture_pair_is_the_pair_this_validation_accepts()
    {
        // Against the vector recorded out of band — the pair the service provider actually produced for the
        // funded regtest self-payment — so this suite's fixture and core's check are pinned to each other rather
        // than to the same expression evaluated twice.
        var paymentHash = new uint256(PaymentFixture.KnownPaymentHashVector);

        Assert.Equal(
            BTCPayInvoiceCreditGateway.PreimageValidity.Valid,
            BTCPayInvoiceCreditGateway.ValidatePreimage(PaymentFixture.Preimage, paymentHash, out _));
    }

    [Fact]
    public void A_preimage_that_does_not_hash_to_the_payment_hash_is_dropped_rather_than_stored()
    {
        // A preimage is a proof. Storing an unverifiable one as though it had been verified would let a future
        // dispute be settled with a value nothing ever checked. Dropping it costs only the proof — the payment
        // is recorded regardless — and the caller logs it at operator level.
        var wrongHash = HashOf(
            "aa03f7557bae8ffda088b42ee758edd048ae8689f87f7de41d9fb3b132341238");

        var validity = BTCPayInvoiceCreditGateway.ValidatePreimage(
            PaymentFixture.Preimage, wrongHash, out var stored);

        Assert.Equal(BTCPayInvoiceCreditGateway.PreimageValidity.Mismatched, validity);
        Assert.Null(stored);
    }

    /// <remarks>
    /// The expected outcome travels as its name rather than as the enum value, because the enum is internal and
    /// an xUnit theory method has to be public. Named through <c>nameof</c> so a rename is a compile error
    /// rather than a test that silently stops distinguishing anything.
    /// </remarks>
    [Theory]
    // The service provider's HTLC details make the preimage optional, so absent is ordinary, not an error.
    [InlineData(null, nameof(BTCPayInvoiceCreditGateway.PreimageValidity.Absent))]
    [InlineData("", nameof(BTCPayInvoiceCreditGateway.PreimageValidity.Absent))]
    // Too short, too long, and the right length but not hex. None of them can be checked against a hash, so
    // none is reported as a mismatch — that distinction is what decides whether an operator is warned.
    [InlineData("4ec10d84", nameof(BTCPayInvoiceCreditGateway.PreimageValidity.Malformed))]
    [InlineData(
        "4ec10d840654ca609a1aa33dd5db662934f9fb0a3cda6656b9a138409493954e00",
        nameof(BTCPayInvoiceCreditGateway.PreimageValidity.Malformed))]
    [InlineData(
        "zzc10d840654ca609a1aa33dd5db662934f9fb0a3cda6656b9a138409493954e",
        nameof(BTCPayInvoiceCreditGateway.PreimageValidity.Malformed))]
    public void An_unusable_preimage_is_told_apart_from_a_wrong_one(string? preimage, string expected)
    {
        var validity = BTCPayInvoiceCreditGateway.ValidatePreimage(
            preimage, HashOf(PaymentFixture.Preimage), out var stored);

        Assert.Equal(expected, validity.ToString());
        Assert.Null(stored);
    }

    // -----------------------------------------------------------------------------------------------------------
    // Whose preimage may be written onto the prompt
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_prompt_preimage_is_filled_in_only_for_the_bolt11_the_prompt_is_offering()
    {
        // The guard that makes one backfill correct in two opposite situations. On an ordinary checkout the
        // prompt is offering the BOLT11 that was paid, and LUD-21 verify serves proof-of-payment out of that
        // field — so it has to be written, because core's listener writes it only when core's own insert wins
        // and this path usually wins instead. On a superseded BOLT11 the prompt is offering the replacement,
        // whose preimage this is not, and writing it would hand a payer a proof for an invoice they never paid.
        var paid = HashOf(PaymentFixture.Preimage);
        var replacement = HashOf("aa03f7557bae8ffda088b42ee758edd048ae8689f87f7de41d9fb3b132341238");

        Assert.True(BTCPayInvoiceCreditGateway.ShouldBackfillPromptPreimage(
            promptPaymentHash: paid, promptPreimage: null, paymentHash: paid));
        Assert.False(BTCPayInvoiceCreditGateway.ShouldBackfillPromptPreimage(
            promptPaymentHash: replacement, promptPreimage: null, paymentHash: paid));
    }

    [Fact]
    public void A_prompt_that_already_has_a_preimage_is_left_alone()
    {
        // Core's own condition, and it means the same thing here: whoever filled it in got there first, and
        // their value is the one that matches the prompt.
        var paid = HashOf(PaymentFixture.Preimage);

        Assert.False(BTCPayInvoiceCreditGateway.ShouldBackfillPromptPreimage(
            promptPaymentHash: paid, promptPreimage: paid, paymentHash: paid));
    }

    [Fact]
    public void A_prompt_with_no_payment_hash_at_all_is_left_alone()
    {
        // The field is nullable on core's details type. Absent is not "matches anything".
        Assert.False(BTCPayInvoiceCreditGateway.ShouldBackfillPromptPreimage(
            promptPaymentHash: null, promptPreimage: null, paymentHash: HashOf(PaymentFixture.Preimage)));
    }

    // -----------------------------------------------------------------------------------------------------------
    // The amount, and the id
    // -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(1_000L, "0.00000001")]
    [InlineData(100_000L, "0.000001")]
    [InlineData(100_000_000_000L, "1")]
    // Not a whole satoshi. BTCPay's payment amount is BTC-denominated, so a millisatoshi remainder has to
    // survive the conversion rather than being truncated away from the merchant.
    [InlineData(1_500L, "0.000000015")]
    public void The_credited_amount_is_the_received_millisatoshis_expressed_in_BTC(long msat, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            BTCPayInvoiceCreditGateway.ToBtc(msat));
    }

    [Fact]
    public void The_amount_conversion_is_the_one_core_uses()
    {
        // Through LightMoney rather than by dividing, so it cannot drift from core's own arithmetic — which is
        // what decides whether the merchant's invoice reads as fully paid.
        Assert.Equal(
            LightMoney.MilliSatoshis(123_456_789).ToDecimal(LightMoneyUnit.BTC),
            BTCPayInvoiceCreditGateway.ToBtc(123_456_789));
    }

    [Fact]
    public void The_payment_id_is_the_lower_case_payment_hash_core_itself_would_write()
    {
        // The whole exactly-once guarantee rests on this. Core's listener ids a Lightning payment with
        // paymentHash.ToString(); this gateway ids it with the hash string it was given, which the record store
        // guarantees is lower-case hex. If those two ever differed in case, the inserts would not collide on the
        // payments primary key and a merchant could be credited twice for one payment.
        var hash = PaymentFixture.PaymentHash;

        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.Equal(hash, uint256.Parse(hash).ToString());
    }

    // -----------------------------------------------------------------------------------------------------------
    // Where a payment hash is looked for
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_payment_hash_is_looked_for_under_lightning_first_and_LNURL_second()
    {
        // Order, not membership. A plain Lightning checkout always indexes the hash under BTC-LN, so probing it
        // first makes the common case one query. BTC-LNURL is probed second because BTCPay indexes an LNURL
        // prompt's hash only when LUD-21 is enabled — which this plugin forces on when it provisions a store, so
        // for a Flint store both rails are covered.
        Assert.Equal(
            [
                PaymentTypes.LN.GetPaymentMethodId("BTC").ToString(),
                PaymentTypes.LNURL.GetPaymentMethodId("BTC").ToString()
            ],
            BTCPayInvoiceCreditGateway.CreditablePaymentMethods.Select(p => p.ToString()).ToArray());
    }

    [Fact]
    public void The_probed_payment_methods_are_the_strings_the_credit_decision_passes_back()
    {
        // The decision layer carries the payment method as a string so it needs no BTCPay types, and the gateway
        // parses it back on the way in. A round trip, because crediting under the wrong payment method would
        // insert a second payment for the same money rather than colliding with core's.
        foreach (var paymentMethodId in BTCPayInvoiceCreditGateway.CreditablePaymentMethods)
            Assert.Equal(paymentMethodId, PaymentMethodId.Parse(paymentMethodId.ToString()));

        Assert.Equal(
            [FakeInvoiceCreditGateway.LightningPaymentMethodId, FakeInvoiceCreditGateway.LnurlPaymentMethodId],
            BTCPayInvoiceCreditGateway.CreditablePaymentMethods.Select(p => p.ToString()).ToArray());
    }
}
