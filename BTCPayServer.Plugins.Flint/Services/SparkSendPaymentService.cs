using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Sends outgoing Lightning payments from a store's Spark wallet.
/// </summary>
/// <remarks>
/// Supports two destination formats:
/// <list type="bullet">
///   <item>A BOLT11 invoice: the amount must be embedded unless <c>amountSats</c> is supplied.</item>
///   <item>A Lightning Address (<c>user@domain.com</c>): requires an explicit <c>amountSats</c>.</item>
/// </list>
/// <b>Idempotency:</b> each call mints a fresh UUID idempotency key. The SDK deduplicates by that key
/// within a wallet session, but this service does not persist the key across restarts. A crash during
/// send leaves the outcome unknown until the SDK's own history is queried.
/// </remarks>
public sealed class SparkSendPaymentService(
    ISparkStoreRuntime runtime,
    ISparkStoreSettingsStore settingsStore,
    IHttpClientFactory httpClientFactory,
    ILogger<SparkSendPaymentService> logger)
{
    /// <summary>
    /// Sends a Lightning payment from the store's Spark wallet.
    /// </summary>
    /// <param name="storeId">The BTCPay store whose wallet funds the payment.</param>
    /// <param name="destination">A BOLT11 invoice or a Lightning Address (<c>user@domain.com</c>).</param>
    /// <param name="amountSats">
    /// Required for a Lightning Address destination and for an amountless BOLT11. Ignored when the
    /// BOLT11 carries its own amount.
    /// </param>
    /// <param name="maxFeeSats">
    /// Maximum routing fee the caller will accept, in satoshi. When null, the default cap
    /// (<see cref="Constants.DefaultMaxFeePercent"/> % or <see cref="Constants.DefaultMaxFeeFloorSats"/> sat,
    /// whichever is larger) applies.
    /// </param>
    /// <param name="cancellationToken">Caller-supplied cancellation.</param>
    /// <returns>The outcome; never throws for domain errors.</returns>
    public async Task<SparkSendOutcome> SendAsync(
        string storeId,
        string destination,
        long? amountSats,
        long? maxFeeSats,
        CancellationToken cancellationToken)
    {
        if (await settingsStore.GetAsync(storeId).ConfigureAwait(false) is null)
            return SparkSendOutcome.Failure("spark-not-configured",
                "Flint is not set up for this store.");

        var sdk = await runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
            return SparkSendOutcome.Failure("spark-not-running",
                "The Spark wallet for this store is not running. Try again in a moment.");

        string bolt11;
        if (IsLightningAddress(destination))
        {
            if (amountSats is null or <= 0)
                return SparkSendOutcome.Failure("amount-required",
                    "amountSats is required when paying a Lightning Address.");

            var resolved = await ResolveLightningAddressAsync(
                destination, amountSats.Value, cancellationToken).ConfigureAwait(false);

            if (resolved.Error is not null)
                return SparkSendOutcome.Failure("lightning-address-error", resolved.Error);

            bolt11 = resolved.Bolt11!;
        }
        else
        {
            bolt11 = destination;
        }

        var idempotencyKey = Guid.NewGuid().ToString();

        try
        {
            var result = await sdk.SendBolt11Async(
                    bolt11,
                    amountSats,
                    idempotencyKey,
                    quote => ApproveQuote(quote, amountSats ?? quote.AmountSats, maxFeeSats),
                    completionTimeout: null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.RejectedReason is not null)
                return SparkSendOutcome.Failure("fee-too-high", result.RejectedReason);

            var p = result.Payment!;
            return SparkSendOutcome.Success(new SparkSendPaymentResult(
                PaymentId: idempotencyKey,
                PaymentHash: p.PaymentHash,
                Bolt11: bolt11,
                AmountSats: p.AmountSats,
                FeeSats: p.FeeSats,
                PaidAt: p.Timestamp));
        }
        catch (Exception ex)
        {
            var description = SparkErrors.Describe(ex);

            if (SparkErrors.IsInsufficientFunds(ex))
                return SparkSendOutcome.Failure("insufficient-balance", description);

            if (SparkErrors.IsInvalidInput(ex))
                return SparkSendOutcome.Failure("invalid-invoice", description);

            // Anything else (network, timeout) may be in-flight. Flag it explicitly so callers don't retry blind.
            logger.LogWarning(ex, "Store {StoreId}: send-payment to {Destination} returned unknown outcome",
                storeId, destination);

            return SparkSendOutcome.Failure("unknown", description);
        }
    }

    // ----- fee approval -----

    private static string? ApproveQuote(SparkSendQuote quote, long amountSats, long? maxFeeSats)
    {
        if (quote.FeeSats < 0)
            return string.Format(
                CultureInfo.InvariantCulture,
                "The routing fee quoted by Spark is negative ({0} sat), which cannot be bounded. "
                + "The payment was refused.", quote.FeeSats);

        long cap;
        if (maxFeeSats is not null)
        {
            cap = maxFeeSats.Value;
        }
        else
        {
            cap = Math.Max(
                (long)Math.Floor(amountSats * Constants.DefaultMaxFeePercent / 100d),
                Constants.DefaultMaxFeeFloorSats);
        }

        if (quote.FeeSats <= cap)
            return null;

        return string.Format(
            CultureInfo.InvariantCulture,
            "Routing fee {0} sat exceeds the {1} limit of {2} sat. "
            + "Raise maxFeeSats in the request to allow a higher fee.",
            quote.FeeSats,
            maxFeeSats is not null ? "requested" : "default",
            cap);
    }

    // ----- Lightning Address resolution (LUD-16) -----

    private static bool IsLightningAddress(string destination)
    {
        var at = destination.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at < destination.Length - 1 && !destination.Contains(' ', StringComparison.Ordinal);
    }

    private async Task<(string? Bolt11, string? Error)> ResolveLightningAddressAsync(
        string address, long amountSats, CancellationToken cancellationToken)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        var user = address[..at];
        var domain = address[(at + 1)..];

        var http = httpClientFactory.CreateClient(HttpClientName);

        LnurlPayResponse? lnurl;
        try
        {
            var wellKnown = $"https://{domain}/.well-known/lnurlp/{user}";
            lnurl = await http.GetFromJsonAsync<LnurlPayResponse>(
                wellKnown, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, $"Could not reach the Lightning Address server for {domain}: {ex.Message}");
        }

        if (lnurl?.Callback is null)
            return (null, $"The Lightning Address server for {address} returned an invalid response.");

        var amountMsats = amountSats * 1000L;
        if (lnurl.MinSendable > 0 && amountMsats < lnurl.MinSendable)
            return (null, string.Format(
                CultureInfo.InvariantCulture,
                "Amount {0} sat is below the minimum of {1} sat for this Lightning Address.",
                amountSats, lnurl.MinSendable / 1000));

        if (lnurl.MaxSendable > 0 && amountMsats > lnurl.MaxSendable)
            return (null, string.Format(
                CultureInfo.InvariantCulture,
                "Amount {0} sat exceeds the maximum of {1} sat for this Lightning Address.",
                amountSats, lnurl.MaxSendable / 1000));

        var callbackUrl = $"{lnurl.Callback.TrimEnd('?')}" +
                          (lnurl.Callback.Contains('?', StringComparison.Ordinal) ? "&" : "?") +
                          $"amount={amountMsats}";

        LnurlPayInvoiceResponse? invoiceResponse;
        try
        {
            invoiceResponse = await http.GetFromJsonAsync<LnurlPayInvoiceResponse>(
                callbackUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, $"Failed to fetch invoice from Lightning Address {address}: {ex.Message}");
        }

        if (string.IsNullOrEmpty(invoiceResponse?.Pr))
            return (null, $"The Lightning Address server for {address} did not return an invoice.");

        return (invoiceResponse.Pr, null);
    }

    /// <summary>Named HttpClient for Lightning Address resolution.</summary>
    public const string HttpClientName = "SparkSendPaymentService";

    private sealed class LnurlPayResponse
    {
        [JsonPropertyName("callback")]
        public string? Callback { get; set; }

        [JsonPropertyName("minSendable")]
        public long MinSendable { get; set; }

        [JsonPropertyName("maxSendable")]
        public long MaxSendable { get; set; }

        [JsonPropertyName("tag")]
        public string? Tag { get; set; }
    }

    private sealed class LnurlPayInvoiceResponse
    {
        [JsonPropertyName("pr")]
        public string? Pr { get; set; }
    }
}

/// <summary>The result of a send: either a success with payment details, or a failure with an error code.</summary>
public sealed record SparkSendOutcome
{
    public bool Succeeded { get; private init; }
    public SparkSendPaymentResult? Payment { get; private init; }
    public string? ErrorCode { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static SparkSendOutcome Success(SparkSendPaymentResult payment) =>
        new() { Succeeded = true, Payment = payment };

    public static SparkSendOutcome Failure(string code, string message) =>
        new() { Succeeded = false, ErrorCode = code, ErrorMessage = message };
}

/// <summary>Payment details returned on a successful send.</summary>
public sealed record SparkSendPaymentResult(
    string PaymentId,
    string? PaymentHash,
    string Bolt11,
    long AmountSats,
    long FeeSats,
    DateTimeOffset PaidAt);
