using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The cross-store Lightning configuration sweep: BTCPay's launcher fires <see cref="Do"/> immediately at
/// startup, then re-runs it on a slow timer as the backstop to the save-time refusal.
/// </summary>
/// <remarks>
/// <para>
/// Registered through BTCPay's <c>AddScheduledTask</c>, whose launcher queues every task when the host
/// starts — so this pass IS the startup sweep — and then requeues it on the interval, logging rather than
/// rethrowing. <see cref="SparkService"/> deliberately does not run its own startup pass: two sweeps at
/// boot would walk the store table twice and double the window in which a victim's payment key could be
/// rotated twice.
/// </para>
/// <para>
/// <b>Why this interval.</b> Every HTTP path to a store's Lightning configuration is refused at save time
/// (<c>SparkLightningClient.Validate</c>), so what this pass exists to catch is a configuration that arrived
/// outside HTTP — a direct database edit, another plugin. That needs a backstop, not a race: half an hour
/// bounds how long one can survive between restarts for one store-table load per pass. It previously rode the
/// one-minute reconciliation cadence, which bought thirty times the walk for nothing this task does not.
/// </para>
/// <para>
/// Idempotent: once a cross-store configuration is cleared and its victim's key rotated, later passes find
/// nothing to do.
/// </para>
/// </remarks>
public class SparkLightningConfigSweepTask : IPeriodicTask
{
    private readonly SparkLightningConfigSweeper _configSweeper;

    public SparkLightningConfigSweepTask(SparkLightningConfigSweeper configSweeper)
    {
        _configSweeper = configSweeper;
    }

    public async Task Do(CancellationToken cancellationToken)
    {
        await _configSweeper.SweepAsync(cancellationToken).ConfigureAwait(false);
    }
}
