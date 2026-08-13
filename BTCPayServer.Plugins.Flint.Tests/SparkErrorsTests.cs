using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Sdk;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Error translation. The SDK's exceptions are UniFFI-generated, with the payload in a public field named
/// <c>v1</c> and <c>Message</c> synthesised as <c>"@v1=…"</c>. None of that may reach a merchant.
/// </summary>
public class SparkErrorsTests
{
    [Fact]
    public void Describe_strips_the_UniFFI_prefix()
    {
        var described = SparkErrors.Describe(new SdkException.SparkException("@v1=Tree service error: no funds"));

        Assert.Equal("Tree service error: no funds", described);
    }

    [Fact]
    public void Describe_never_returns_a_prefixed_message_for_any_SDK_error()
    {
        Exception[] errors =
        [
            new SdkException.SparkException("@v1=spark"),
            new SdkException.InvalidInput("@v1=bad input"),
            new SdkException.NetworkException("@v1=offline"),
            new SdkException.StorageException("@v1=no rows"),
            new SdkException.ChainServiceException("@v1=chain"),
            new SdkException.LnurlException("@v1=lnurl"),
            new SdkException.Signer("@v1=signer"),
            new SdkException.InvalidUuid("@v1=uuid"),
            new SdkException.Generic("@v1=generic"),
            // tokenIdentifier is null for a sat shortfall, set only when a token balance is short.
            new SdkException.InsufficientFunds(null),
            new SdkException.MissingUtxo("tx", 0),
            new SdkException.OptimizationAlreadyRunning()
        ];

        foreach (var error in errors)
        {
            var described = SparkErrors.Describe(error);
            Assert.False(string.IsNullOrWhiteSpace(described));
            Assert.DoesNotContain("@v1=", described);
        }
    }

    [Fact]
    public void Describe_gives_a_plain_message_for_a_disposed_instance()
    {
        var described = SparkErrors.Describe(new ObjectDisposedException("BreezSdk"));

        Assert.Contains("no longer running", described);
    }

    [Fact]
    public void Describe_handles_a_null_payload()
    {
        Assert.False(string.IsNullOrWhiteSpace(SparkErrors.Describe(new SdkException.SparkException(null!))));
    }

    [Fact]
    public void IsInsufficientFunds_matches_the_typed_variant_and_the_string_form()
    {
        // The typed variant was never actually observed being thrown; an unfunded send arrives as a
        // SparkException whose text contains "insufficient funds".
        Assert.True(SparkErrors.IsInsufficientFunds(new SdkException.InsufficientFunds(null)));
        // Since SDK 0.22.0 the variant carries the identifier of whichever balance was short; a token
        // shortfall must classify the same way, because the plugin only ever spends sats and would
        // otherwise report a token error as "state unknown".
        Assert.True(SparkErrors.IsInsufficientFunds(new SdkException.InsufficientFunds("btkn1exampletoken")));
        Assert.True(SparkErrors.IsInsufficientFunds(
            new SdkException.SparkException("@v1=Tree service error: insufficient funds")));
        Assert.False(SparkErrors.IsInsufficientFunds(new SdkException.SparkException("@v1=Invalid network")));
        Assert.False(SparkErrors.IsInsufficientFunds(new InvalidOperationException("insufficient funds")));
    }

    [Fact]
    public void IsInvalidInput_identifies_a_locally_rejected_request()
    {
        // Distinguishes "nothing was sent, safe to retry differently" from "state unknown".
        Assert.True(SparkErrors.IsInvalidInput(new SdkException.InvalidInput(
            "@v1=Amount is below the minimum of 294 sats required for this address")));
        Assert.False(SparkErrors.IsInvalidInput(new SdkException.NetworkException("@v1=timeout")));
        Assert.False(SparkErrors.IsInvalidInput(new TimeoutException()));
    }

    [Fact]
    public void IsExpiredFeeQuote_identifies_a_stale_coop_exit_quote()
    {
        // A ~60-second TTL makes this a routine condition to re-prepare through, not a failure to report.
        // It arrives as prose, so there is nothing typed to match on.
        Assert.True(SparkErrors.IsExpiredFeeQuote(new SdkException.SparkException(
            "@v1=Service error: service provider error: graphql error: The coop exit fee quote has expired, " +
            "please request a new quote.")));
        Assert.False(SparkErrors.IsExpiredFeeQuote(
            new SdkException.SparkException("@v1=Tree service error: insufficient funds")));
    }

    [Fact]
    public void IsNotFound_matches_the_no_rows_storage_error()
    {
        // GetPayment on an unknown id throws rather than returning null, and this is the only way to tell
        // "not found" from a genuine storage failure.
        Assert.True(SparkErrors.IsNotFound(
            new SdkException.StorageException("@v1=Underlying implementation error: Query returned no rows")));
        Assert.False(SparkErrors.IsNotFound(new SdkException.StorageException("@v1=Connection error")));
        Assert.False(SparkErrors.IsNotFound(new SdkException.SparkException("@v1=no rows")));
    }
}
