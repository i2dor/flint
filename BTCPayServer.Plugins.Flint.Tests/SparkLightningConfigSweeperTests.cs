using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The startup backstop for the cross-store connection-string enforcement: configurations that predate the
/// save-time refusal, or that were written without one, are cleared and their victim's payment key is
/// rotated so every leaked copy of the victim's string stops resolving.
/// </summary>
public class SparkLightningConfigSweeperTests
{
    private const string VictimKey = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
    private const string LndConnectionString =
        "type=lnd-rest;server=https://127.0.0.1:8080/;macaroon=abcdef";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SparkLightningConfigSweeper Create(
        FakeStoreLightningConfigStore configs,
        FakeSparkStoreSettingsStore settings,
        params string[] storeIds) => new(
        new FakeStoreIdSource(storeIds),
        new SparkLightningWiring(configs, NullLogger<SparkLightningWiring>.Instance),
        settings,
        NullLogger<SparkLightningConfigSweeper>.Instance);

    private static FakeSparkStoreSettingsStore SettingsWithVictim()
    {
        var settings = new FakeSparkStoreSettingsStore();
        settings.Settings["store-2"] = new SparkSettings { PaymentKey = VictimKey };
        return settings;
    }

    [Fact]
    public async Task Sweep_clears_the_cross_store_configuration_and_rotates_the_victim_key()
    {
        // store-1's Lightning payment method embeds store-2's wallet. The sweep must remove that
        // configuration and mint a fresh key for store-2, rewriting store-2's own configuration so the
        // victim's copy of the string — and the attacker's — both die.
        var configs = FakeStoreLightningConfigStore
            .WithStore("store-1", SparkConnectionString.Format("store-2", VictimKey))
            .Add("store-2", SparkConnectionString.Format("store-2", VictimKey));
        var settings = SettingsWithVictim();
        var sweeper = Create(configs, settings, "store-1", "store-2");

        var result = await sweeper.SweepAsync(Ct);

        Assert.Equal(new SparkLightningConfigSweepResult(1, 1), result);

        // The hijacking configuration is gone.
        Assert.Null(configs.Stores["store-1"].ConnectionString);

        // The victim's key was rotated and its own configuration now carries the fresh string.
        var rotated = settings.Settings["store-2"]!;
        Assert.NotEqual(VictimKey, rotated.PaymentKey);
        Assert.Equal(
            SparkConnectionString.Format("store-2", rotated.PaymentKey),
            configs.Stores["store-2"].ConnectionString);
    }

    [Fact]
    public async Task Sweep_leaves_well_formed_configurations_alone()
    {
        // Own-store strings (current or stale) are not cross-store: nothing to clear, nothing to rotate.
        var configs = FakeStoreLightningConfigStore
            .WithStore("store-1", SparkConnectionString.Format("store-1", VictimKey))
            .Add("store-2", "type=lnd-rest;server=https://127.0.0.1:8080/;macaroon=abcdef");
        var settings = SettingsWithVictim();
        var sweeper = Create(configs, settings, "store-1", "store-2");

        var result = await sweeper.SweepAsync(Ct);

        Assert.Equal(new SparkLightningConfigSweepResult(0, 0), result);
        Assert.Equal(
            SparkConnectionString.Format("store-1", VictimKey),
            configs.Stores["store-1"].ConnectionString);
        Assert.Equal(VictimKey, settings.Settings["store-2"]!.PaymentKey);
    }

    [Fact]
    public async Task Sweep_clears_a_configuration_whose_victim_has_no_spark_settings_without_rotating()
    {
        // A cross-store string pointing at a store with no Spark wallet at all resolves to nothing today, so
        // clearing the configuration is the whole remediation — there is no key to rotate.
        var configs = FakeStoreLightningConfigStore
            .WithStore("store-1", SparkConnectionString.Format("store-2", VictimKey));
        var settings = new FakeSparkStoreSettingsStore();
        var sweeper = Create(configs, settings, "store-1");

        var result = await sweeper.SweepAsync(Ct);

        Assert.Equal(new SparkLightningConfigSweepResult(1, 0), result);
        Assert.Null(configs.Stores["store-1"].ConnectionString);
        Assert.Empty(settings.Writes);
    }

    [Fact]
    public async Task Sweep_restores_the_previous_key_when_the_rotation_is_declined()
    {
        // The wallet-owner guard or an unsupported chain can make SetAsync report "stored but not running";
        // the sweep must not leave the victim half-rotated.
        var configs = FakeStoreLightningConfigStore
            .WithStore("store-1", SparkConnectionString.Format("store-2", VictimKey))
            .Add("store-2", SparkConnectionString.Format("store-2", VictimKey));
        var settings = SettingsWithVictim();
        settings.NextSetDeclinesWith = "the wallet declined to restart";
        var sweeper = Create(configs, settings, "store-1", "store-2");

        var result = await sweeper.SweepAsync(Ct);

        Assert.Equal(new SparkLightningConfigSweepResult(1, 0), result);
        // The cross-store configuration is still cleared; only the rotation was rolled back.
        Assert.Null(configs.Stores["store-1"].ConnectionString);
        Assert.Equal(VictimKey, settings.Settings["store-2"]!.PaymentKey);
    }

    [Fact]
    public async Task Sweep_rotates_a_multiply_referenced_victim_exactly_once()
    {
        // Two stores both drive the same victim wallet; one rotation revokes both leaked copies.
        var configs = FakeStoreLightningConfigStore
            .WithStore("store-1", SparkConnectionString.Format("store-2", VictimKey))
            .Add("store-3", SparkConnectionString.Format("store-2", VictimKey))
            .Add("store-2", SparkConnectionString.Format("store-2", VictimKey));
        var settings = SettingsWithVictim();
        var sweeper = Create(configs, settings, "store-1", "store-2", "store-3");

        var result = await sweeper.SweepAsync(Ct);

        Assert.Equal(new SparkLightningConfigSweepResult(2, 1), result);
        Assert.Null(configs.Stores["store-1"].ConnectionString);
        Assert.Null(configs.Stores["store-3"].ConnectionString);
        // Store-2's settings were written exactly twice: once rotated, and its own wire-up is in the config
        // store, not here.
        Assert.Single(settings.Writes, w => w.StoreId == "store-2");
    }

    [Fact]
    public async Task Sweep_rotates_the_key_but_leaves_a_victim_configuration_that_moved_away_alone()
    {
        // A merchant who switched their Lightning to their own node has a configuration that must not be
        // clobbered; rotating the (now unused) key still revokes the leaked copy of the old string.
        var configs = FakeStoreLightningConfigStore
            .WithStore("store-1", SparkConnectionString.Format("store-2", VictimKey))
            .Add("store-2", LndConnectionString);
        var settings = SettingsWithVictim();
        var sweeper = Create(configs, settings, "store-1", "store-2");

        var result = await sweeper.SweepAsync(Ct);

        Assert.Equal(new SparkLightningConfigSweepResult(1, 1), result);
        Assert.Null(configs.Stores["store-1"].ConnectionString);
        Assert.NotEqual(VictimKey, settings.Settings["store-2"]!.PaymentKey);
        Assert.Equal(LndConnectionString, configs.Stores["store-2"].ConnectionString);
    }

    [Fact]
    public async Task Sweep_keeps_going_when_one_stores_clear_fails()
    {
        // One store whose configuration cannot be cleared must not stop the walk from clearing the next.
        var configs = FakeStoreLightningConfigStore
            .WithStore("store-1", SparkConnectionString.Format("store-2", VictimKey))
            .Add("store-3", SparkConnectionString.Format("store-4", "other-victim-key"));
        configs.FailNextSetWith = new InvalidOperationException("repository failed");
        var settings = new FakeSparkStoreSettingsStore();
        settings.Settings["store-2"] = new SparkSettings { PaymentKey = VictimKey };
        settings.Settings["store-4"] = new SparkSettings { PaymentKey = "other-victim-key" };
        var sweeper = Create(configs, settings, "store-1", "store-3");

        var result = await sweeper.SweepAsync(Ct);

        // store-1's clear hit the injected failure and was swallowed; store-3 was still cleared and its
        // victim still rotated.
        Assert.Equal(new SparkLightningConfigSweepResult(1, 1), result);
        Assert.Equal(
            SparkConnectionString.Format("store-2", VictimKey),
            configs.Stores["store-1"].ConnectionString);
        Assert.Null(configs.Stores["store-3"].ConnectionString);
        Assert.NotEqual("other-victim-key", settings.Settings["store-4"]!.PaymentKey);
    }
}
