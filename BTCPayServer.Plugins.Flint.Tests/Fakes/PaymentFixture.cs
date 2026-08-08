using NBitcoin;
using NBitcoin.Crypto;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// A real preimage and its real payment hash.
/// </summary>
/// <remarks>
/// Not decorative. BTCPay's <c>LightningInstanceListener.GetValidPreimage</c> recomputes
/// <c>sha256(preimage)</c> and discards the preimage — logging a warning — when it does not equal the payment
/// hash. Fixtures using arbitrary hex would therefore pass this plugin's tests while producing invoices whose
/// preimage BTCPay silently throws away, so the pair here is generated properly.
/// </remarks>
public static class PaymentFixture
{
    /// <summary>An arbitrary but fixed 32-byte preimage, lower-case hex.</summary>
    public const string Preimage = "4ec10d840654ca609a1aa33dd5db662934f9fb0a3cda6656b9a138409493954e";

    /// <summary>The SHA-256 of <see cref="Preimage"/>, i.e. the payment hash that matches it.</summary>
    public static readonly string PaymentHash = Convert.ToHexStringLower(
        Hashes.SHA256(Convert.FromHexString(Preimage)));

    /// <summary>A different, valid payment hash for tests that need a second invoice.</summary>
    public static readonly string OtherPaymentHash = Convert.ToHexStringLower(
        Hashes.SHA256(Convert.FromHexString(
            "aa03f7557bae8ffda088b42ee758edd048ae8689f87f7de41d9fb3b132341238")));

    /// <summary>
    /// <see cref="PaymentHash"/>, written out independently of the code that computes it.
    /// </summary>
    /// <remarks>
    /// Comparing <see cref="PaymentHash"/> against a fresh <c>Hashes.SHA256</c> call would be tautological — both
    /// sides would be the same expression. This literal is independent of that code: it is the pair the
    /// Lightspark service provider actually produced for the funded regtest self-payment recorded in the spike
    /// notes, and it round-trips through <c>printf … | xxd -r -p | shasum -a 256</c>.
    /// </remarks>
    public const string KnownPaymentHashVector =
        "84e9d106385fb5cb81f3e27c6c60dbab942debaa7358fbd5005e59fd28fddc91";
}
