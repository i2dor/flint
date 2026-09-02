using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The periodic half of the cross-store configuration enforcement, now on its own half-hour task rather
/// than riding the one-minute reconciliation pass: a cross-store Lightning configuration written outside
/// HTTP survives at most that interval rather than until the next restart. The sweep's remediation itself
/// is covered by <see cref="SparkLightningConfigSweeperTests"/>; what is under test here is that
/// <see cref="SparkLightningConfigSweepTask.Do"/> actually reaches the sweep, so a future edit that drops
/// the call fails here rather than silently widening the window the task exists to close.
/// </summary>
public class SparkLightningConfigSweepTaskTests
{
    private const string VictimKey = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Do_runs_the_cross_store_sweep_on_the_periodic_pass()
    {
        // The misconfiguration lands after startup — the shape that needs the periodic pass, because nothing
        // about this particular save went through the plugin.
        var configs = FakeStoreLightningConfigStore
            .WithStore("attacker", SparkConnectionString.Format("victim", VictimKey))
            .Add("victim", SparkConnectionString.Format("victim", VictimKey));
        var settings = new FakeSparkStoreSettingsStore();
        settings.Settings["victim"] = new SparkSettings { PaymentKey = VictimKey };

        var task = new SparkLightningConfigSweepTask(
            new SparkLightningConfigSweeper(
                new FakeStoreSource("attacker", "victim"),
                new SparkLightningWiring(configs, NullLogger<SparkLightningWiring>.Instance),
                settings,
                NullLogger<SparkLightningConfigSweeper>.Instance));

        await task.Do(Ct);

        Assert.Null(configs.Stores["attacker"].ConnectionString);
        Assert.NotEqual(VictimKey, settings.Settings["victim"]!.PaymentKey);
    }
}
