using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Flint.Data;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Periodically deletes payment-hash associations the credit walk can no longer plausibly consult.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this table needs a delete path at all.</b> The indexer writes one row per LN/LNURL prompt mint
/// server-wide — every invoice quoted on the server, not just Flint stores' — and until now nothing ever
/// removed a row, making the plugin's one Postgres table unbounded on a host that otherwise caps its own
/// data (see <c>docs/limitations.md</c> on <c>sdk.log</c> for the other half of that story). The write gate
/// added alongside this task stops rows being minted for servers with no Flint store; this task retires the
/// rows the walk has left behind for every server.
/// </para>
/// <para>
/// <b>The bound is <see cref="SparkInvoiceCreditor.ListableFrom"/>, not an invented horizon.</b> The only
/// reader of this table is the credit gateway's fallback lookup, reached only for settlements the credit walk
/// still lists, and the walk lists nothing older than <c>now − 14 days</c> (<see
/// cref="SparkInvoiceCreditor.CreditRetryHorizon"/> plus <see
/// cref="SparkInvoiceCreditor.AbandonedReportingGrace"/>). One edge keeps the claim short of absolute: the
/// cut deletes by mint time (<c>FirstSeenAt</c>) while the walk lists by settle time, and
/// <c>FirstSeenAt &lt;= SettledAt</c>, so a row deleted at the mint-window floor can still have a settlement
/// that is listable by settle time. That needs a mint-to-settle gap wider than the whole window, is closed
/// in practice by BOLT11 expiry, and is stated in the limitations doc: a payment that arrives more than
/// fourteen days after its prompt was <em>minted</em> reaches a row this task may already have deleted, and
/// is reported unattributable — the same outcome the outage paragraph already accepts for older-than-the-walk
/// payments. Beyond that edge, an association first seen before the boundary buys the walk nothing. The bound
/// is deliberately the walk's own constant rather than a copy: widen the walk's horizons and retention follows
/// automatically.
/// </para>
/// <para>
/// Registered through BTCPay's <c>AddScheduledTask</c> on <see
/// cref="Constants.PaymentHashRetentionInterval"/> — its own task rather than a line appended to
/// <see cref="SparkReconciliationTask"/>, because that pass runs every minute over live wallets and this one
/// is a single indexed <c>DELETE</c> that runs hourly at negligible cost; sharing the task would tie a
/// background janitor to the settlement guarantee's hot loop. <c>AddScheduledTask</c> logs rather
/// than rethrows, so a database outage delays pruning to the next pass instead of faulting anything.
/// </para>
/// </remarks>
public class SparkPaymentHashRetentionTask : IPeriodicTask
{
    private readonly IInvoicePaymentHashIndex _index;
    private readonly ILogger<SparkPaymentHashRetentionTask> _logger;

    public SparkPaymentHashRetentionTask(
        IInvoicePaymentHashIndex index,
        ILogger<SparkPaymentHashRetentionTask> logger)
    {
        _index = index;
        _logger = logger;
    }

    /// <summary>The oldest association a pass keeps: exactly the credit walk's own listing floor.</summary>
    public static DateTimeOffset CutoffFor(DateTimeOffset now) => SparkInvoiceCreditor.ListableFrom(now);

    public async Task Do(CancellationToken cancellationToken)
    {
        var cutoff = CutoffFor(DateTimeOffset.UtcNow);
        var removed = await _index.PruneBeforeAsync(cutoff, cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            _logger.LogInformation(
                "Spark payment-hash retention removed {Removed} association(s) first seen before {Cutoff}",
                removed, cutoff);
        }
    }
}
