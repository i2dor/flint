using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// Queues are bounded and refuse when full; a refused push is held back and re-delivered on a short
/// timer, so a transiently-stalled consumer catches up instead of losing the notification. The
/// allowance is bounded per subscription and by a delivery deadline, after which the operator is told
/// the truth — BTCPay does not re-poll listened invoices, and <c>SparkReconciliationTask</c> only scans
/// unpaid rows, so an already-paid row's push reaches BTCPay only when it next reads the invoice.
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

    /// <summary>Default allowance of refused pushes held back for retry per subscription.</summary>
    private const int DefaultPendingPushCap = 64;

    /// <summary>Default cadence at which refused pushes are re-attempted.</summary>
    private static readonly TimeSpan DefaultPushRetryInterval = TimeSpan.FromSeconds(10);

    /// <summary>Default how long a refused push may wait before the retry gives up and logs the truth.</summary>
    private static readonly TimeSpan DefaultPushRetryLifetime = TimeSpan.FromMinutes(2);

    private readonly ILogger<SparkSettlementBroadcaster> _logger;
    private readonly TimeSpan _pushRetryInterval;
    private readonly TimeSpan _pushRetryLifetime;
    private readonly int _pendingPushCap;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, Subscription>> _subscriptions = new();
    private readonly object _retryGate = new();
    private readonly Timer _retryTimer;
    private bool _retryTimerStarted;
    private long _nextSubscriptionId;

    public SparkSettlementBroadcaster(ILogger<SparkSettlementBroadcaster> logger)
        : this(logger, DefaultPushRetryInterval, DefaultPushRetryLifetime, DefaultPendingPushCap)
    {
    }

    /// <summary>Shorter budgets for tests; the default constructor makes the production singleton.</summary>
    internal SparkSettlementBroadcaster(
        ILogger<SparkSettlementBroadcaster> logger,
        TimeSpan pushRetryInterval,
        TimeSpan pushRetryLifetime,
        int pendingPushCap)
    {
        _logger = logger;
        _pushRetryInterval = pushRetryInterval;
        _pushRetryLifetime = pushRetryLifetime;
        _pendingPushCap = pendingPushCap;
        _retryTimer = new Timer(_ => DrainRetries(DateTimeOffset.UtcNow), null,
            Timeout.Infinite, Timeout.Infinite);
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
                    // The listener's bounded queue is full. Nothing can bank on a quiet recovery: BTCPay
                    // does not re-poll listened invoices (its one-minute timer only expires stale sessions),
                    // and the reconciliation task only scans unpaid rows — this row is already paid, the
                    // push was minted on that very transition. So hold the push back and re-deliver it on a
                    // short timer; a transiently-stalled consumer catches up instead of losing the push. If
                    // the consumer stays wedged past the delivery deadline, the retry gives up and logs
                    // what actually remains: the paid row reaches BTCPay when it next reads the invoice,
                    // typically after a restart of the plugin or the server.
                    if (subscription.EnqueueRetry(settlement))
                    {
                        EnsureRetryTimerRunning();
                        _logger.LogWarning(
                            "A Spark settlement listener for store {StoreId} is not keeping up; delivery of "
                            + "the notification for {PaymentHash} is delayed and will be retried for up to "
                            + "{RetryLifetime}",
                            settlement.StoreId, settlement.PaymentHash, _pushRetryLifetime);
                    }
                    else
                    {
                        _logger.LogError(
                            "A Spark settlement listener for store {StoreId} is not keeping up and has "
                            + "exceeded the retry allowance; giving up on {PaymentHash}. The payment is "
                            + "recorded and will reach BTCPay when it next reads this invoice — typically "
                            + "after a restart of the plugin or the server; the BTCPay invoice may otherwise "
                            + "show unpaid.",
                            settlement.StoreId, settlement.PaymentHash);
                    }
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

    /// <summary>Starts the retry timer on first use; it then ticks for the process lifetime — a ten-second
    /// no-op walk on an idle process is cheaper than the lifecycle races of stopping and restarting it.</summary>
    private void EnsureRetryTimerRunning()
    {
        lock (_retryGate)
        {
            if (_retryTimerStarted)
                return;
            _retryTimerStarted = true;
            _retryTimer.Change(_pushRetryInterval, _pushRetryInterval);
        }
    }

    /// <summary>Re-attempts every held-back push. Internal so tests can drive the timer's work directly.</summary>
    internal void DrainRetries(DateTimeOffset now)
    {
        foreach (var forStore in _subscriptions.Values)
        {
            foreach (var subscription in forStore.Values)
            {
                subscription.DrainPendingRetries(now, _logger);
            }
        }
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
        private readonly object _pendingLock = new();
        // Payment hash -> refused push and when it was first refused. Keyed by payment hash because a given
        // payment is published at most once, so the map is both a dedupe and, under the cap, a memory bound.
        private readonly Dictionary<string, (SparkSettlement Settlement, DateTimeOffset Since)> _pending = new();
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

        /// <summary>Holds a refused push for a later retry. Returns false when the allowance is full, so the
        /// caller can log the honest fallback instead of silently growing memory.</summary>
        internal bool EnqueueRetry(SparkSettlement settlement)
        {
            lock (_pendingLock)
            {
                if (_pending.ContainsKey(settlement.PaymentHash))
                    return true;
                if (_pending.Count >= _owner._pendingPushCap)
                    return false;
                _pending[settlement.PaymentHash] = (settlement, DateTimeOffset.UtcNow);
                return true;
            }
        }

        /// <summary>One retry tick for this subscription: delivers what the consumer now has room for, gives
        /// up on what has waited past the deadline, and drops what belonged to a session that died meanwhile.</summary>
        internal void DrainPendingRetries(DateTimeOffset now, ILogger logger)
        {
            List<(string Hash, SparkSettlement Settlement)> deliver;
            List<(string Hash, SparkSettlement Settlement)> expired;
            lock (_pendingLock)
            {
                if (_pending.Count == 0)
                    return;
                deliver = new List<(string, SparkSettlement)>(_pending.Count);
                expired = new List<(string, SparkSettlement)>(_pending.Count);
                foreach (var (hash, entry) in _pending)
                    (now - entry.Since >= _owner._pushRetryLifetime ? expired : deliver).Add((hash, entry.Settlement));
            }

            // Writes are attempted outside _pendingLock: TryWrite is thread-safe, and holding the lock across
            // it would serialise this subscription's own publishes on the retry tick.
            foreach (var (hash, settlement) in deliver)
            {
                switch (TryWrite(settlement))
                {
                    case SubscriptionWriteResult.Delivered:
                        lock (_pendingLock) { _pending.Remove(hash); }
                        logger.LogInformation(
                            "Delayed Spark settlement notification for {PaymentHash} was delivered once the "
                            + "listener caught up", settlement.PaymentHash);
                        break;
                    case SubscriptionWriteResult.Disposed:
                        // The session is gone; there is nothing left to deliver to, and the paid row reaches
                        // BTCPay when it next reads the invoice.
                        lock (_pendingLock) { _pending.Remove(hash); }
                        break;
                    case SubscriptionWriteResult.Saturated:
                        // Still wedged; the next tick decides.
                        break;
                }
            }

            foreach (var (hash, settlement) in expired)
            {
                lock (_pendingLock) { _pending.Remove(hash); }
                logger.LogError(
                    "Gave up delivering the Spark settlement notification for {PaymentHash} to store "
                    + "{StoreId} after {RetryLifetime}: the listening session has not consumed. The payment "
                    + "is recorded; BTCPay does not re-poll listened invoices, so the invoice will show paid "
                    + "only when it next reads this one — typically after a restart of the plugin or the "
                    + "server.",
                    settlement.PaymentHash, settlement.StoreId, _owner._pushRetryLifetime);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _owner.Unsubscribe(StoreId, Id);
            _channel.Writer.TryComplete();
        }
    }
}
