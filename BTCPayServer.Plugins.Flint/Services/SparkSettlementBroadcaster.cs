using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// A settled receive, as handed to BTCPay's Lightning listener.
/// </summary>
/// <param name="AmountReceivedMsat">
/// What actually arrived, never the invoiced amount. BTCPay credits
/// <c>AmountReceived ?? Amount</c>, so getting this wrong overcredits or undercredits the merchant.
/// </param>
public sealed record SparkSettlement(
    string StoreId,
    string PaymentHash,
    string Bolt11,
    long? AmountMsat,
    long AmountReceivedMsat,
    DateTimeOffset SettledAt,
    DateTimeOffset ExpiresAt,
    string? Preimage);

/// <summary>One consumer's view of a store's settlement stream.</summary>
public interface ISparkSettlementSubscription : IDisposable
{
    /// <summary>Waits for the next settlement for this store.</summary>
    ValueTask<SparkSettlement> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Subscribes to a store's settlement stream.</summary>
public interface ISparkSettlementSubscriber
{
    ISparkSettlementSubscription Subscribe(string storeId);
}

/// <summary>
/// Fans settlements out to every live <c>WaitInvoice</c> consumer for a store.
/// </summary>
/// <remarks>
/// <para>
/// Every subscription gets its own queue and every subscription sees every settlement. This is not a
/// work queue: BTCPay may hold more than one listening session against the same connection string, and
/// each session filters notifications against its own set of listened invoices
/// (<c>LightningInstanceListener.Listen</c> skips any notification whose id it does not recognise).
/// A shared queue would let one session consume — and then discard — a notification another session
/// was waiting for, and that invoice would then wait for the next reconciliation pass.
/// </para>
/// <para>
/// Queues are bounded and refuse (with a warning) when full, so a wedged consumer degrades into lost
/// notifications — recovered by <c>SparkReconciliationTask</c>, which is the plugin's settlement guarantee —
/// instead of unbounded memory growth.
/// </para>
/// </remarks>
public class SparkSettlementBroadcaster : ISparkSettlementSubscriber
{
    /// <summary>
    /// Per-subscription queue depth. BTCPay's listener consumes in a tight loop, so anything queued at
    /// all means the consumer is stalled; this only has to absorb a burst.
    /// </summary>
    private const int SubscriptionCapacity = 256;

    /// <summary>
    /// Above this many concurrent subscriptions for one store something is leaking sessions. Logged
    /// rather than enforced: refusing to subscribe would break checkout, which is worse.
    /// </summary>
    private const int SubscriptionWarningThreshold = 32;

    private readonly ILogger<SparkSettlementBroadcaster> _logger;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, Subscription>> _subscriptions = new();
    private long _nextSubscriptionId;

    public SparkSettlementBroadcaster(ILogger<SparkSettlementBroadcaster> logger)
    {
        _logger = logger;
    }

    public ISparkSettlementSubscription Subscribe(string storeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var forStore = _subscriptions.GetOrAdd(storeId, _ => new ConcurrentDictionary<long, Subscription>());
        var subscription = new Subscription(
            Interlocked.Increment(ref _nextSubscriptionId),
            storeId,
            this,
            SubscriptionCapacity);
        forStore[subscription.Id] = subscription;

        if (forStore.Count > SubscriptionWarningThreshold)
        {
            _logger.LogWarning(
                "Store {StoreId} has {Count} concurrent Spark settlement listeners; sessions may be leaking",
                storeId, forStore.Count);
        }

        return subscription;
    }

    /// <summary>
    /// Delivers a settlement to every current subscriber of the store. Never throws.
    /// </summary>
    public void Publish(SparkSettlement settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        if (!_subscriptions.TryGetValue(settlement.StoreId, out var forStore) || forStore.IsEmpty)
        {
            // No listener attached (BTCPay only listens while it has invoices to watch). The settlement is
            // already persisted, so the next GetInvoice lookup or reconciliation pass reports it.
            _logger.LogDebug(
                "No Spark settlement listener for store {StoreId}; {PaymentHash} is already recorded and will be "
                + "reported when BTCPay next asks",
                settlement.StoreId, settlement.PaymentHash);
            return;
        }

        foreach (var subscription in forStore.Values)
        {
            switch (subscription.TryWrite(settlement))
            {
                case SubscriptionWriteResult.Delivered:
                    continue;

                case SubscriptionWriteResult.Disposed:
                    // A session that shut down between the snapshot of this dictionary and the write. Routine
                    // — BTCPay disposes a listening session as soon as it has no invoices left to watch — and
                    // reporting it as saturation would be phantom noise in an operator's log.
                    _logger.LogTrace(
                        "Skipped a disposed Spark settlement listener for store {StoreId}", settlement.StoreId);
                    continue;

                default:
                    _logger.LogWarning(
                        "A Spark settlement listener for store {StoreId} is not keeping up; dropped a "
                        + "notification for {PaymentHash}, which will be recovered by the reconciliation task",
                        settlement.StoreId, settlement.PaymentHash);
                    continue;
            }
        }
    }

    private void Unsubscribe(string storeId, long id)
    {
        if (!_subscriptions.TryGetValue(storeId, out var forStore))
            return;
        forStore.TryRemove(id, out _);
        // Left in place when empty on purpose: stores come and go far less often than listening
        // sessions do, and removing the inner dictionary would race with a concurrent Subscribe.
    }

    /// <summary>Why a settlement could or could not be handed to one subscription.</summary>
    private enum SubscriptionWriteResult
    {
        Delivered,

        /// <summary>The consumer has fallen behind and its bounded queue is full.</summary>
        Saturated,

        /// <summary>The session was disposed. Routine, not a problem.</summary>
        Disposed
    }

    private sealed class Subscription : ISparkSettlementSubscription
    {
        private readonly SparkSettlementBroadcaster _owner;
        private readonly Channel<SparkSettlement> _channel;
        private int _disposed;

        public Subscription(long id, string storeId, SparkSettlementBroadcaster owner, int capacity)
        {
            Id = id;
            StoreId = storeId;
            _owner = owner;
            _channel = Channel.CreateBounded<SparkSettlement>(new BoundedChannelOptions(capacity)
            {
                // Wait, paired with TryWrite only: that is the combination where a full queue surfaces as
                // TryWrite returning false, which the broadcaster logs. The DropOldest and DropWrite modes
                // return true and evict silently. Nothing ever blocks, because WriteAsync is never called.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        }

        public long Id { get; }
        public string StoreId { get; }

        public SubscriptionWriteResult TryWrite(SparkSettlement settlement)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return SubscriptionWriteResult.Disposed;
            return _channel.Writer.TryWrite(settlement)
                ? SubscriptionWriteResult.Delivered
                // A completed writer also lands here, which is the same disposal race one instruction later;
                // it is reported as saturation only because the two are indistinguishable at this point, and
                // the consequence either way is that reconciliation picks the settlement up.
                : SubscriptionWriteResult.Saturated;
        }

        public ValueTask<SparkSettlement> ReadAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAsync(cancellationToken);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _owner.Unsubscribe(StoreId, Id);
            _channel.Writer.TryComplete();
        }
    }
}
