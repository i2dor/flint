using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Delivers a webhook POST after each successful sweep.
/// </summary>
/// <remarks>
/// Failures are logged as warnings and never retried. The sweep record in the database is the
/// authoritative source of truth; the webhook is a convenience notification only.
/// </remarks>
public class SparkSweepWebhookNotifier
{
    public const string HttpClientName = "SparkSweepWebhook";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SparkSweepWebhookNotifier> _logger;

    public SparkSweepWebhookNotifier(
        IHttpClientFactory httpClientFactory,
        ILogger<SparkSweepWebhookNotifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task NotifyAsync(
        string webhookUrl,
        string storeId,
        SweepRecord record,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            _logger.LogWarning(
                "Store {StoreId}: sweep webhook URL '{Url}' is not a valid http/https URL; notification skipped",
                storeId, webhookUrl);
            return;
        }

        var payload = new
        {
            storeId,
            idempotencyKey = record.IdempotencyKey,
            txId = record.TxId,
            amountSats = record.AmountSats,
            feeSats = record.FeeSats,
            destination = record.DestinationAddress,
            destinationMode = record.DestinationMode.ToString(),
            trigger = record.Trigger.ToString(),
            completedAt = record.CompletedAt
        };

        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var content = new StringContent(
                JsonSerializer.Serialize(payload, SerializerOptions),
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Store {StoreId}: sweep webhook returned {StatusCode}; notification not acknowledged",
                    storeId, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: sweep webhook delivery to '{Url}' failed",
                storeId, webhookUrl);
        }
    }
}
