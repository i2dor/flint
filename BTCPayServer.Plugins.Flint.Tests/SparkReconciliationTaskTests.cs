using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The scheduled task that runs the reconciliation pass and, on the same cadence, the cross-store
/// configuration sweep — the periodic half of the store-binding enforcement, so a cross-store Lightning
/// configuration written outside HTTP survives at most one interval rather than until the next restart.
/// </summary>
/// <remarks>
/// The walk itself is covered by the reconciler and sweeper suites; what is under test here is the wiring —
/// that <see cref="SparkReconciliationTask.Do"/> actually reaches the sweep, so a future edit that drops the
/// call fails here rather than silently widening the window it exists to close.
/// </remarks>
public class SparkReconciliationTaskTests
{
    private const string VictimKey = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 30_000)]
    public async Task Do_runs_the_cross_store_sweep_on_the_periodic_pass()
    {
        using var harness = SparkServiceHarness.Create();
        await harness.Service.StartAsync(Ct);

        // The misconfiguration lands after startup — the shape that needs the periodic pass, because nothing
        // about this particular Save went through the plugin.
        var configs = FakeStoreLightningConfigStore
            .WithStore("attacker", SparkConnectionString.Format("victim", VictimKey))
            .Add("victim", SparkConnectionString.Format("victim", VictimKey));
        var settings = new FakeSparkStoreSettingsStore();
        settings.Settings["victim"] = new SparkSettings { PaymentKey = VictimKey };

        var task = new SparkReconciliationTask(
            harness.Service,
            new SparkLightningConfigSweeper(
                new FakeStoreIdSource("attacker", "victim"),
                new SparkLightningWiring(configs, NullLogger<SparkLightningWiring>.Instance),
                settings,
                NullLogger<SparkLightningConfigSweeper>.Instance),
            NullLogger<SparkReconciliationTask>.Instance);

        await task.Do(Ct);

        Assert.Null(configs.Stores["attacker"].ConnectionString);
        Assert.NotEqual(VictimKey, settings.Settings["victim"]!.PaymentKey);
    }
}
