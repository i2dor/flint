using System.Net;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

public class SparkSweepWebhookNotifierTests
{
    private static readonly TimeSpan[] NoDelay = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

    private static SweepRecord MinimalRecord() => new()
    {
        IdempotencyKey = "test-key",
        StoreId = "store-1",
        TxId = "abc123",
        AmountSats = 50_000,
        FeeSats = 1_500,
        DestinationAddress = "bc1qtest",
        DestinationMode = SweepDestinationMode.StoreWallet,
        Trigger = SweepTrigger.Manual,
        CompletedAt = DateTimeOffset.UtcNow,
    };

    private static SparkSweepWebhookNotifier Notifier(StubHttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler), NullLogger<SparkSweepWebhookNotifier>.Instance, NoDelay);

    [Fact]
    public async Task Delivers_on_first_attempt()
    {
        var handler = StubHttpMessageHandler.Returning("ok");
        var notifier = Notifier(handler);

        await notifier.NotifyAsync("https://example.com/hook", "store-1", MinimalRecord());

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Retries_on_server_error_and_succeeds()
    {
        // First request: 503, second: 200.
        var handler = StubHttpMessageHandler.Sequence(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);
        var notifier = Notifier(handler);

        await notifier.NotifyAsync("https://example.com/hook", "store-1", MinimalRecord());

        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Retries_on_network_error_and_succeeds()
    {
        var handler = StubHttpMessageHandler.FailOnceThenOK();
        var notifier = Notifier(handler);

        await notifier.NotifyAsync("https://example.com/hook", "store-1", MinimalRecord());

        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Does_not_retry_on_client_error()
    {
        var handler = StubHttpMessageHandler.Sequence(HttpStatusCode.NotFound);
        var notifier = Notifier(handler);

        await notifier.NotifyAsync("https://example.com/hook", "store-1", MinimalRecord());

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        var handler = StubHttpMessageHandler.Failing(HttpStatusCode.ServiceUnavailable);
        var notifier = Notifier(handler);

        await notifier.NotifyAsync("https://example.com/hook", "store-1", MinimalRecord());

        Assert.Equal(SparkSweepWebhookNotifier.MaxAttempts, handler.Requests);
    }

    [Fact]
    public async Task Skips_invalid_url()
    {
        var handler = StubHttpMessageHandler.Returning("ok");
        var notifier = Notifier(handler);

        await notifier.NotifyAsync("ftp://example.com/hook", "store-1", MinimalRecord());

        Assert.Equal(0, handler.Requests);
    }
}
