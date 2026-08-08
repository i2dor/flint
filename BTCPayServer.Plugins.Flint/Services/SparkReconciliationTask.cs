using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Periodically re-checks every store's unpaid invoices against the Spark service.
/// </summary>
/// <remarks>
/// <para>
/// This is the plugin's settlement guarantee, not a belt-and-braces addition, because neither of the other
/// two paths is reliable:
/// </para>
/// <list type="bullet">
/// <item><description>The SDK's event stream drops completions. A completed receive has been observed
/// emitting only <c>PaymentPending</c> and never <c>PaymentSucceeded</c>, the completion visible only from a
/// later storage read.</description></item>
/// <item><description>BTCPay does not re-poll. It calls <c>GetInvoice</c> once per invoice on creation or
/// activation, and once per invoice when a listening session starts. Its one-minute timer calls only
/// <c>CheckConnections()</c>, which expires stale entries and restarts a <em>dead</em> session; it polls no
/// invoices. Because this plugin's <c>WaitInvoice</c> awaits a channel and never faults, the session never
/// dies, so that restart never happens either.</description></item>
/// </list>
/// <para>
/// Without this task, a single dropped event means an invoice expires unpaid while the sats sit in the
/// merchant's wallet — which is exactly the class of silent loss this plugin exists to avoid.
/// </para>
/// <para>
/// Registered through BTCPay's <c>AddScheduledTask</c>, which runs <see cref="Do"/> on a fixed interval and
/// logs rather than rethrows. <see cref="SparkService"/> additionally runs one pass at startup, to cover
/// events missed while the process was down.
/// </para>
/// <para>
/// <b>A pass is bounded and rotates.</b> Both passes go through the one
/// <see cref="SparkStorePassScheduler"/> that <see cref="SparkService"/> holds, so a store's walk cannot hold
/// one of BTCPay's three shared scheduled-task workers indefinitely and a store late in the list is not
/// starved by the ones ahead of it. The reason a per-call deadline is not already enough is in that class's
/// remarks: this walk examines up to a thousand invoices per store and each one is entitled to its own.
/// </para>
/// </remarks>
public class SparkReconciliationTask : IPeriodicTask
{
    private readonly SparkService _sparkService;
    private readonly ILogger<SparkReconciliationTask> _logger;

    public SparkReconciliationTask(SparkService sparkService, ILogger<SparkReconciliationTask> logger)
    {
        _sparkService = sparkService;
        _logger = logger;
    }

    public async Task Do(CancellationToken cancellationToken)
    {
        var settled = await _sparkService.ReconcileAllStoresAsync(cancellationToken).ConfigureAwait(false);
        if (settled > 0)
        {
            _logger.LogInformation(
                "Spark reconciliation settled {Settled} Lightning invoice(s) across all stores", settled);
        }
    }
}
