using BTCPayServer.Data;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The ownership rules for a store's Lightning payment method.
/// </summary>
/// <remarks>
/// These are the tests that stand between a merchant and losing their own Lightning node's configuration
/// because they once tried Spark. Every "does not clear" case here is a real way that could happen.
/// </remarks>
public class SparkLightningWiringTests
{
    private const string StoreId = "store-1";
    private const string OtherStoreId = "store-2";
    private const string PaymentKey = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private static SparkLightningWiring Create(FakeStoreLightningConfigStore configStore) =>
        new(configStore, NullLogger<SparkLightningWiring>.Instance);

    [Fact]
    public async Task Enable_writes_the_stores_own_connection_string()
    {
        var configStore = FakeStoreLightningConfigStore.WithStore(StoreId);

        Assert.True(await Create(configStore).EnableAsync(StoreId, PaymentKey));

        var written = Assert.Single(configStore.Writes);
        Assert.Equal(StoreId, written.StoreId);
        Assert.Equal(SparkConnectionString.Format(StoreId, PaymentKey), written.ConnectionString);
    }

    [Fact]
    public async Task Enable_reports_failure_for_a_store_that_no_longer_exists()
    {
        // The window is real: a store can be deleted between the setup page rendering and its form posting.
        var configStore = new FakeStoreLightningConfigStore();

        Assert.False(await Create(configStore).EnableAsync(StoreId, PaymentKey));
    }

    [Fact]
    public async Task Clear_removes_our_own_configuration()
    {
        var configStore = FakeStoreLightningConfigStore.WithStore(
            StoreId, SparkConnectionString.Format(StoreId, PaymentKey));

        Assert.True(await Create(configStore).ClearIfOursAsync(StoreId));

        var written = Assert.Single(configStore.Writes);
        Assert.Null(written.ConnectionString);
    }

    [Fact]
    public async Task Clear_removes_a_stale_configuration_of_ours()
    {
        // A leftover from an earlier setup: still ours, and already dead — the handler rejects a key that does
        // not match the settings. Leaving it is what makes checkout fail with "not configured for this store"
        // instead of telling the merchant their wallet was removed.
        var configStore = FakeStoreLightningConfigStore.WithStore(
            StoreId, SparkConnectionString.Format(StoreId, "aaaabbbbccccdddd"));

        Assert.True(await Create(configStore).ClearIfOursAsync(StoreId));
        Assert.Null(Assert.Single(configStore.Writes).ConnectionString);
    }

    [Fact]
    public async Task Clear_recognises_our_configuration_however_it_was_typed()
    {
        // A merchant who retyped or reformatted the connection string still gets it cleaned up. Comparing the
        // raw strings byte for byte would leave this behind.
        var configStore = FakeStoreLightningConfigStore.WithStore(
            StoreId, $"type=FlInT;{SparkConnectionString.PaymentKeyKey}={PaymentKey.ToUpperInvariant()};"
                     + $"{SparkConnectionString.StoreIdKey}={StoreId}");

        Assert.True(await Create(configStore).ClearIfOursAsync(StoreId));
    }

    [Theory]
    // Somebody else's node. The case this whole mechanism exists to protect.
    [InlineData("type=lnd-rest;server=https://127.0.0.1:8080/;macaroon=abcdef")]
    // A malformed string of our own type: not evidence of what the merchant meant, so left alone.
    [InlineData("type=flint;store-id=store-1")]
    public async Task Clear_leaves_a_configuration_that_is_not_ours_alone(string connectionString)
    {
        var configStore = FakeStoreLightningConfigStore.WithStore(StoreId, connectionString);

        Assert.False(await Create(configStore).ClearIfOursAsync(StoreId));
        Assert.Empty(configStore.Writes);
        Assert.Equal(connectionString, configStore.Stores[StoreId].ConnectionString);
    }

    [Fact]
    public async Task Clear_leaves_the_internal_node_alone()
    {
        var configStore = FakeStoreLightningConfigStore.WithStore(StoreId, isInternalNode: true);

        Assert.False(await Create(configStore).ClearIfOursAsync(StoreId));
        Assert.Empty(configStore.Writes);
    }

    [Fact]
    public async Task Clear_leaves_another_stores_spark_configuration_alone()
    {
        // ClearIfOursAsync runs while a store's own Spark settings are being removed, so it only ever clears
        // this store's own configuration. A cross-store configuration is not ours to clear at that moment —
        // that is the configuration sweep's job.
        var configStore = FakeStoreLightningConfigStore.WithStore(
            StoreId, SparkConnectionString.Format(OtherStoreId, PaymentKey));

        Assert.False(await Create(configStore).ClearIfOursAsync(StoreId));
        Assert.Empty(configStore.Writes);
    }

    [Fact]
    public async Task Clear_cross_store_removes_a_configuration_pointing_at_another_store()
    {
        // The sweep's remediation: a valid string embedding a different store id is the one configuration
        // that is broken by definition, so it is cleared and the victim's id is reported.
        var configStore = FakeStoreLightningConfigStore.WithStore(
            StoreId, SparkConnectionString.Format(OtherStoreId, PaymentKey));

        var victim = await Create(configStore).ClearCrossStoreAsync(StoreId);

        Assert.Equal(OtherStoreId, victim);
        Assert.Null(Assert.Single(configStore.Writes).ConnectionString);
    }

    [Theory]
    // Our own store: not cross-store.
    [InlineData("type=flint;store-id=" + StoreId + ";key=0f1e2d3c4b5a69788796a5b4c3d2e1f0")]
    // Somebody else's node: never cleared by this method.
    [InlineData("type=lnd-rest;server=https://127.0.0.1:8080/;macaroon=abcdef")]
    // Our type but malformed: the owner is unknown, so left alone.
    [InlineData("type=flint;store-id=" + StoreId)]
    public async Task Clear_cross_store_leaves_everything_else_alone(string connectionString)
    {
        var configStore = FakeStoreLightningConfigStore.WithStore(StoreId, connectionString);

        Assert.Null(await Create(configStore).ClearCrossStoreAsync(StoreId));
        Assert.Empty(configStore.Writes);
    }

    [Fact]
    public async Task Clear_cross_store_leaves_a_missing_or_internal_node_configuration_alone()
    {
        // A store with no Lightning payment method, and BTCPay's internal node, have nothing cross-store.
        var none = new FakeStoreLightningConfigStore();
        Assert.Null(await Create(none).ClearCrossStoreAsync(StoreId));

        var internalNode = FakeStoreLightningConfigStore.WithStore(StoreId, isInternalNode: true);
        Assert.Null(await Create(internalNode).ClearCrossStoreAsync(StoreId));
        Assert.Empty(internalNode.Writes);
    }

    [Fact]
    public async Task Clear_does_nothing_when_there_is_no_lightning_configuration()
    {
        var configStore = FakeStoreLightningConfigStore.WithStore(StoreId);

        Assert.False(await Create(configStore).ClearIfOursAsync(StoreId));
        Assert.Empty(configStore.Writes);
    }

    [Fact]
    public async Task Clear_does_nothing_when_the_store_is_gone()
    {
        var configStore = new FakeStoreLightningConfigStore();

        Assert.False(await Create(configStore).ClearIfOursAsync(StoreId));
        Assert.Empty(configStore.Writes);
    }

    [Fact]
    public async Task Inspect_distinguishes_a_current_configuration_from_a_stale_one()
    {
        var current = FakeStoreLightningConfigStore.WithStore(
            StoreId, SparkConnectionString.Format(StoreId, PaymentKey));
        var stale = FakeStoreLightningConfigStore.WithStore(
            StoreId, SparkConnectionString.Format(StoreId, "aaaabbbbccccdddd"));

        Assert.Equal(
            SparkLightningWiringState.Spark,
            (await Create(current).InspectAsync(StoreId, PaymentKey)).State);
        Assert.Equal(
            SparkLightningWiringState.StaleSpark,
            (await Create(stale).InspectAsync(StoreId, PaymentKey)).State);
    }

    [Fact]
    public async Task Inspect_reports_a_configuration_excluded_from_checkout()
    {
        // A configured wallet that silently takes no payments is the failure a merchant cannot diagnose, so
        // the status page has to be able to say it.
        var configStore = FakeStoreLightningConfigStore.WithStore(
            StoreId, SparkConnectionString.Format(StoreId, PaymentKey), enabled: false);

        var report = await Create(configStore).InspectAsync(StoreId, PaymentKey);

        Assert.Equal(SparkLightningWiringState.Spark, report.State);
        Assert.False(report.EnabledForCheckout);
    }

    [Fact]
    public async Task Inspect_reports_a_missing_store()
    {
        var report = await Create(new FakeStoreLightningConfigStore()).InspectAsync(StoreId, PaymentKey);

        Assert.Equal(SparkLightningWiringState.StoreNotFound, report.State);
    }

    [Fact]
    public void Inspect_classifies_a_loaded_store_row_without_an_id_lookup()
    {
        // What the sweep now does per store: classify straight off the row it already loaded, with no
        // per-store FindStore behind it. The store's own id — not the caller's — is what gets compared
        // against the connection string.
        var configStore = FakeStoreLightningConfigStore.WithStore(
            StoreId, SparkConnectionString.Format(StoreId, PaymentKey));
        var report = Create(configStore).Inspect(new StoreData { Id = StoreId }, PaymentKey);

        Assert.Equal(SparkLightningWiringState.Spark, report.State);
        Assert.True(report.EnabledForCheckout);
        Assert.Equal(0, configStore.IdReads);
    }

    /// <summary>
    /// Every state <c>Classify</c> can return, by name.
    /// </summary>
    /// <remarks>
    /// Status.cshtml switches on all seven and renders different merchant advice for each — and decides whether
    /// to offer the repair button — so a mis-mapping (OtherNode reported as NotConfigured, say) would show wrong
    /// advice about somebody's Lightning node with nothing failing. Called directly rather than through
    /// InspectAsync, because the mapping is the thing under test.
    /// </remarks>
    [Theory]
    // No such store.
    [InlineData(null, false, null, SparkLightningWiringState.StoreNotFound)]
    // A store with no Lightning payment method at all.
    [InlineData("", false, null, SparkLightningWiringState.NotConfigured)]
    // BTCPay's internal node. Checked before the connection string, which is null for it.
    [InlineData("", true, null, SparkLightningWiringState.InternalNode)]
    // Somebody else's node.
    [InlineData("", false, "type=lnd-rest;server=https://127.0.0.1:8080/;macaroon=abcdef",
        SparkLightningWiringState.OtherNode)]
    // Our type, but malformed: not evidence of intent, so treated as not ours.
    [InlineData("", false, "type=flint;store-id=store-1", SparkLightningWiringState.OtherNode)]
    // Ours, current key.
    [InlineData("", false, "type=flint;store-id=store-1;key=" + PaymentKey, SparkLightningWiringState.Spark)]
    // Ours, but a key the settings no longer hold.
    [InlineData("", false, "type=flint;store-id=store-1;key=aaaabbbbccccdddd",
        SparkLightningWiringState.StaleSpark)]
    // Another store's Spark wallet: refused at save time, cleared by the configuration sweep.
    [InlineData("", false, "type=flint;store-id=store-2;key=" + PaymentKey,
        SparkLightningWiringState.OtherStoreSpark)]
    public void Classify_maps_every_configuration_to_its_own_state(
        string? storeExists,
        bool isInternalNode,
        string? connectionString,
        SparkLightningWiringState expected)
    {
        // A null first argument means "there is no such store", which the real config store reports as a null
        // config rather than an empty one.
        var config = storeExists is null
            ? null
            : new StoreLightningConfig(isInternalNode, connectionString, true);

        Assert.Equal(expected, SparkLightningWiring.Classify(StoreId, PaymentKey, config));
    }

    [Fact]
    public void Classify_treats_a_spark_configuration_as_ours_when_no_key_is_known()
    {
        // What the teardown path does: it classifies while the settings are being removed, so it has no key to
        // compare, and current-versus-stale is a distinction without a difference there.
        var config = new StoreLightningConfig(false, SparkConnectionString.Format(StoreId, "anything-at-all"), true);

        Assert.Equal(
            SparkLightningWiringState.Spark,
            SparkLightningWiring.Classify(StoreId, paymentKey: null, config));
    }

    [Fact]
    public async Task Enable_makes_a_previously_excluded_configuration_current()
    {
        var configStore = FakeStoreLightningConfigStore.WithStore(
            StoreId, "type=lnd-rest;server=https://127.0.0.1:8080/;macaroon=abcdef", enabled: false);
        var wiring = Create(configStore);

        Assert.True(await wiring.EnableAsync(StoreId, PaymentKey));

        var report = await wiring.InspectAsync(StoreId, PaymentKey);
        Assert.Equal(SparkLightningWiringState.Spark, report.State);
        Assert.True(report.EnabledForCheckout);
    }
}
