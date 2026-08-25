using BTCPayServer.Plugins.Flint.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Fan-out semantics for <c>WaitInvoice</c>.
/// </summary>
/// <remarks>
/// BTCPay's <c>LightningInstanceListener</c> may hold more than one listening session against the same
/// connection string, and each session discards any notification whose id it does not recognise. So this
/// must be a broadcast: a work-queue would let one session consume and then throw away a notification
/// another session was waiting for, and the invoice would only settle on the next minute's poll.
/// </remarks>
public class SparkSettlementBroadcasterTests
{
    private static SparkSettlementBroadcaster Create() => new(NullLogger<SparkSettlementBroadcaster>.Instance);

    private static SparkSettlement Settlement(string storeId = "store-1", string? hash = null) => new(
        storeId,
        hash ?? new string('a', 64),
        "lnbcrt1",
        100_000,
        100_000,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch.AddHours(1),
        null);

    [Fact]
    public async Task Every_subscriber_of_a_store_receives_every_settlement()
    {
        var broadcaster = Create();
        using var first = broadcaster.Subscribe("store-1");
        using var second = broadcaster.Subscribe("store-1");
        var settlement = Settlement();

        broadcaster.Publish(settlement);

        Assert.Same(settlement, await first.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Same(settlement, await second.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Subscribers_of_other_stores_receive_nothing()
    {
        var broadcaster = Create();
        using var mine = broadcaster.Subscribe("store-1");
        using var theirs = broadcaster.Subscribe("store-2");

        broadcaster.Publish(Settlement("store-1"));

        await mine.ReadAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await theirs.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task Settlements_are_delivered_in_order()
    {
        var broadcaster = Create();
        using var subscription = broadcaster.Subscribe("store-1");
        var first = Settlement(hash: new string('a', 64));
        var second = Settlement(hash: new string('b', 64));

        broadcaster.Publish(first);
        broadcaster.Publish(second);

        Assert.Same(first, await subscription.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Same(second, await subscription.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_disposed_subscription_stops_receiving_and_does_not_block_the_others()
    {
        var broadcaster = Create();
        var disposed = broadcaster.Subscribe("store-1");
        using var live = broadcaster.Subscribe("store-1");
        disposed.Dispose();

        broadcaster.Publish(Settlement());

        Assert.NotNull(await live.ReadAsync(TestContext.Current.CancellationToken));
        // The disposed subscription's channel is completed, so a read fails rather than hanging.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await disposed.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Disposing_twice_leaves_the_remaining_subscribers_working()
    {
        // Idempotent disposal is only interesting if it does not corrupt the broadcaster's bookkeeping, so the
        // assertion is that a sibling subscription still receives afterwards.
        var broadcaster = Create();
        var subscription = broadcaster.Subscribe("store-1");
        using var sibling = broadcaster.Subscribe("store-1");

        subscription.Dispose();
        subscription.Dispose();
        broadcaster.Publish(Settlement());

        Assert.NotNull(await sibling.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Publishing_with_no_subscribers_does_not_queue_for_a_later_subscriber()
    {
        // BTCPay only listens while it has invoices to watch, so this is the normal case for a settlement that
        // arrives out of the blue. It must not throw, and it must not be buffered for whoever subscribes next:
        // the settlement is already persisted and is reported through GetInvoice or reconciliation instead.
        var broadcaster = Create();
        broadcaster.Publish(Settlement());

        using var later = broadcaster.Subscribe("store-1");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await later.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task A_stalled_subscriber_is_capped_rather_than_growing_without_limit()
    {
        var broadcaster = Create();
        using var subscription = broadcaster.Subscribe("store-1");

        // Well past the 256-entry per-subscription capacity, with nothing being read. Publishing must stay
        // non-blocking throughout: the caller is the settlement consumer loop, shared by every store.
        for (var i = 0; i < 400; i++)
            broadcaster.Publish(Settlement(hash: i.ToString("x64")));

        var drained = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (drained.Count < 400)
                drained.Add((await subscription.ReadAsync(cts.Token)).PaymentHash);
        }
        catch (OperationCanceledException)
        {
            // Expected once the bounded queue is empty.
        }

        // Capped, in order, and starting from the oldest: overflow is refused rather than evicting entries
        // that a consumer may be about to catch up on.
        // Exactly the capacity, in order, from the oldest: overflow is refused rather than evicting entries a
        // consumer may be about to catch up on. Asserting the exact count is the point — an InRange(1, 256)
        // would pass for any capacity at all, including a queue that dropped 399 of 400.
        Assert.Equal(256, drained.Count);
        Assert.Equal(Enumerable.Range(0, 256).Select(i => i.ToString("x64")).ToArray(), drained.ToArray());
    }


    [Fact]
    public async Task A_saturated_settlement_is_retried_once_the_consumer_catches_up()
    {
        // The refusal is not the end of the story: BTCPay does not re-poll listened invoices and the
        // reconciliation task's settlement walk only scans unpaid rows, so a refused push that is simply
        // dropped is a notification nobody ever receives. The invoice is still credited — the credit path
        // routes the settlement onto it without any listener (SparkInvoiceCreditor) — but that takes a
        // reconciliation pass, where re-delivering here takes milliseconds. So the broadcaster must hold the
        // push and re-deliver it when the listener drains.
        var broadcaster = Create();
        using var subscription = broadcaster.Subscribe("store-1");

        // Fill the bounded queue so the next publish is refused and held for retry.
        for (var i = 0; i < 256; i++)
            broadcaster.Publish(Settlement(hash: i.ToString("x64")));

        var retried = Settlement(hash: "f".PadLeft(64, 'f'));
        broadcaster.Publish(retried);

        // The consumer catches up, draining the queue…
        for (var i = 0; i < 256; i++)
            await subscription.ReadAsync(TestContext.Current.CancellationToken);

        // …and the retry tick re-delivers what was refused.
        broadcaster.DrainRetries(DateTimeOffset.UtcNow);

        Assert.Same(retried, await subscription.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_retry_that_never_delivers_is_given_up_on_after_the_deadline()
    {
        // A consumer wedged past the delivery deadline is not going to drain, so the retry must stop
        // retrying and say so rather than hold memory or deliver absurdly late.
        var broadcaster = new SparkSettlementBroadcaster(
            NullLogger<SparkSettlementBroadcaster>.Instance,
            pushRetryInterval: TimeSpan.FromMilliseconds(100),
            pushRetryLifetime: TimeSpan.FromMilliseconds(50),
            pendingPushCap: 4);
        using var subscription = broadcaster.Subscribe("store-1");

        for (var i = 0; i < 256; i++)
            broadcaster.Publish(Settlement(hash: i.ToString("x64")));
        for (var i = 0; i < 4; i++)
            broadcaster.Publish(Settlement(hash: (i + 1_000).ToString("x64")));

        // The consumer catches up, but the retry deadline has already passed.
        for (var i = 0; i < 256; i++)
            await subscription.ReadAsync(TestContext.Current.CancellationToken);
        broadcaster.DrainRetries(DateTimeOffset.UtcNow.AddMinutes(1));

        // Nothing more arrives: the held pushes were given up on rather than delivered late.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await subscription.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task Many_concurrent_subscribers_all_receive()
    {
        var broadcaster = Create();
        var subscriptions = Enumerable.Range(0, 50).Select(_ => broadcaster.Subscribe("store-1")).ToList();
        try
        {
            broadcaster.Publish(Settlement());

            foreach (var subscription in subscriptions)
                Assert.NotNull(await subscription.ReadAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            foreach (var subscription in subscriptions)
                subscription.Dispose();
        }
    }
}
