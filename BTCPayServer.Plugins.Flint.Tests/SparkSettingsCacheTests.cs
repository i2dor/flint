using System.Reflection;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Nothing may edit the settings the service has cached, and a write that fails must change nothing.
/// </summary>
/// <remarks>
/// <para>
/// The cache is not an optimisation detail. <c>SparkSweepEngine</c> reads a store's configuration through it
/// on every pass, so a configuration that reached the cache without reaching the database is a configuration
/// that moves money and does not survive a restart — a fee ceiling raised, a destination changed, or sweeping
/// switched on, all invisible in the stored blob and gone at the next boot.
/// </para>
/// <para>
/// These run against the real <c>SparkService</c> and BTCPay's real <c>IStoreRepository</c> contract, with a
/// repository that fails the way a database fails: the exception comes out <em>before</em> anything is stored.
/// A fake that stored first and threw afterwards would make the divergence between cache and database
/// unobservable, which is the whole thing being checked.
/// </para>
/// </remarks>
public class SparkSettingsCacheTests
{
    private const string StoreId = "cache-store";

    [Fact]
    public async Task Two_reads_do_not_hand_back_the_same_object()
    {
        using var h = await StartedHarness();

        var first = await h.Service.Get(StoreId);
        var second = await h.Service.Get(StoreId);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.NotSame(first!.Sweep, second!.Sweep);
    }

    [Fact]
    public async Task Editing_what_a_read_returned_does_not_edit_the_cache()
    {
        using var h = await StartedHarness();

        var read = await h.Service.Get(StoreId);
        read!.Sweep.Enabled = true;
        read.Sweep.MaxFeePercent = 49;
        read.StableBalance.Enabled = true;

        var again = await h.Service.Get(StoreId);
        Assert.False(again!.Sweep.Enabled);
        Assert.Equal(SweepSettings.DefaultMaxFeePercent, again.Sweep.MaxFeePercent);
        Assert.False(again.StableBalance.Enabled);
    }

    [Fact]
    public async Task Editing_what_was_handed_to_a_write_does_not_edit_the_cache_afterwards()
    {
        using var h = await StartedHarness();

        var written = (await h.Service.Get(StoreId))!;
        written.Sweep.Enabled = true;
        await h.Service.Set(StoreId, written);

        // The caller still holds the object it passed in. Keeping on editing it must not reach the cache.
        written.Sweep.MaxFeePercent = 49;

        var read = await h.Service.Get(StoreId);
        Assert.True(read!.Sweep.Enabled);
        Assert.Equal(SweepSettings.DefaultMaxFeePercent, read.Sweep.MaxFeePercent);
    }

    /// <summary>
    /// The failure that made this a real hazard rather than a theoretical one.
    /// </summary>
    [Fact]
    public async Task A_write_that_fails_to_persist_leaves_the_cache_untouched()
    {
        using var h = await StartedHarness();

        var edited = (await h.Service.Get(StoreId))!;
        edited.Sweep.Enabled = true;
        edited.Sweep.MaxFeePercent = 49;

        h.Stores.FailNextUpdateWith = new InvalidOperationException("the database was unreachable");

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.Set(StoreId, edited));

        var cached = await h.Service.Get(StoreId);
        Assert.False(cached!.Sweep.Enabled);
        Assert.Equal(SweepSettings.DefaultMaxFeePercent, cached.Sweep.MaxFeePercent);

        // And the database agrees with the cache, which is the property that actually matters.
        var stored = h.Stores.Stored<SparkSettings>(StoreId, Constants.StoreSettingsKey);
        Assert.False(stored!.Sweep.Enabled);
    }

    /// <summary>
    /// The same, driven through the surface a merchant actually uses.
    /// </summary>
    /// <remarks>
    /// End to end over the real service rather than over the settings-store fake, because the bug lived in the
    /// interaction: the sweep service applied its edit to the object the service had handed it, so the two
    /// pieces were each defensible alone and wrong together.
    /// </remarks>
    [Fact]
    public async Task A_sweep_configuration_that_fails_to_persist_does_not_reach_the_engine()
    {
        using var h = await StartedHarness();
        var sweepSettings = SweepSettingsServiceOver(h);

        h.Stores.FailNextUpdateWith = new InvalidOperationException("the database was unreachable");

        await Assert.ThrowsAsync<InvalidOperationException>(() => sweepSettings.SaveAsync(
            StoreId,
            new SweepSettingsInput
            {
                Enabled = true,
                DestinationMode = SweepDestinationMode.StaticAddress,
                StaticAddress = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString(),
                MaxFeePercent = 49
            },
            CancellationToken.None));

        // What the engine would read on its next pass.
        var cached = await h.Service.Get(StoreId);
        Assert.False(cached!.Sweep.Enabled);
        Assert.Equal(SweepDestinationMode.StoreWallet, cached.Sweep.DestinationMode);
        Assert.Equal(SweepSettings.DefaultMaxFeePercent, cached.Sweep.MaxFeePercent);
    }

    [Fact]
    public async Task A_sweep_configuration_that_persists_does_reach_the_engine()
    {
        // The other half, so the test above cannot be satisfied by a save that never writes anything.
        using var h = await StartedHarness();
        var sweepSettings = SweepSettingsServiceOver(h);
        var address = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();

        var result = await sweepSettings.SaveAsync(
            StoreId,
            new SweepSettingsInput
            {
                Enabled = true,
                DestinationMode = SweepDestinationMode.StaticAddress,
                StaticAddress = address,
                MaxFeePercent = 2
            },
            CancellationToken.None);

        Assert.Equal(SparkSweepSettingsSaveStatus.Saved, result.Status);

        var cached = await h.Service.Get(StoreId);
        Assert.True(cached!.Sweep.Enabled);
        Assert.Equal(address, cached.Sweep.StaticAddress);

        var stored = h.Stores.Stored<SparkSettings>(StoreId, Constants.StoreSettingsKey);
        Assert.True(stored!.Sweep.Enabled);
    }

    /// <summary>
    /// The callers hold the line on their own, against a store that hands its settings out by reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SparkService.Get</c> now clones, so with the real service in place these two could not corrupt
    /// anything even if they mutated what they were given — which makes the caller-side fix invisible against
    /// it, and invisible is how a defence rots. <see cref="ISparkStoreSettingsStore"/> promises no copy, and
    /// the plugin's own in-memory implementation of it does not make one, so the weakest legal implementation
    /// of the seam is exactly what these are driven against.
    /// </para>
    /// <para>
    /// The write fails <em>before</em> storing, because that is the only shape in which the difference is
    /// observable: after a successful store, the edit is supposed to be there.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_sweep_save_does_not_edit_the_settings_it_was_given_when_the_write_fails()
    {
        var store = ByReferenceStore();
        var sweepSettings = SweepSettingsServiceOver(store, new FakeSparkStoreRuntime());
        store.FailNextSetBeforeStoringWith = new InvalidOperationException("the database was unreachable");

        await Assert.ThrowsAsync<InvalidOperationException>(() => sweepSettings.SaveAsync(
            StoreId,
            new SweepSettingsInput
            {
                Enabled = true,
                DestinationMode = SweepDestinationMode.StaticAddress,
                StaticAddress = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString(),
                MaxFeePercent = 49
            },
            CancellationToken.None));

        var held = store.Settings[StoreId]!;
        Assert.False(held.Sweep.Enabled);
        Assert.Equal(SweepDestinationMode.StoreWallet, held.Sweep.DestinationMode);
        Assert.Equal(SweepSettings.DefaultMaxFeePercent, held.Sweep.MaxFeePercent);
    }

    [Fact]
    public async Task A_stable_balance_save_does_not_edit_the_settings_it_was_given_when_the_write_fails()
    {
        var store = ByReferenceStore();
        var runtime = new FakeSparkStoreRuntime();
        runtime.Clients[StoreId] = new FakeSparkSdkClient();

        var stableBalance = new SparkStableBalanceService(
            store, runtime, mainnet: true, NullLogger<SparkStableBalanceService>.Instance);

        store.FailNextSetBeforeStoringWith = new InvalidOperationException("the database was unreachable");

        await Assert.ThrowsAsync<InvalidOperationException>(() => stableBalance.SaveAsync(
            StoreId,
            new StableBalanceInput
            {
                Enabled = true,
                DisclosureAcknowledged = true,
                TokenIdentifier = StableBalanceSettings.DefaultTokenIdentifier,
                MaxSlippageBps = 500
            },
            CancellationToken.None));

        var held = store.Settings[StoreId]!;
        Assert.False(held.StableBalance.Enabled);
        Assert.Equal(StableBalanceSettings.DefaultMaxSlippageBps, held.StableBalance.MaxSlippageBps);
    }

    /// <summary>
    /// Every stored property is copied, nested sections included and deep, checked by reflection so adding one
    /// without extending a <c>Clone</c> fails here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hand-written list would have to be remembered, which is the thing that gets forgotten — this test's own
    /// list had already missed <c>UnilateralExit</c>. A property missing from <c>Clone</c> is a setting that
    /// silently reverts to its default on the next read — and for <c>ProtectedMnemonic</c> that would be a store
    /// whose wallet stops starting.
    /// </para>
    /// <para>
    /// Every stored property gets a value different from its default, so a dropped property fails. Two properties
    /// copied into each other's slots fail too wherever <see cref="DistinctValues"/> can tell them apart — every
    /// number, string and enum here — but not two plain bools with the same default (<c>StableBalance</c>'s
    /// <c>Enabled</c> and <c>DisclosureAcknowledged</c>, say): each has only one non-default value, so both are set
    /// true and a swap between them reads the same. Each section is walked the same way, which covers
    /// the section's own <c>Clone</c>, and must not be the source's instance: shallow would defeat the point, as
    /// every edit that matters lands on a section rather than on a scalar.
    /// </para>
    /// </remarks>
    [Fact]
    public void Clone_copies_every_property()
    {
        var source = new SparkSettings();
        Fill(source, new DistinctValues());

        AssertCopied(source, source.Clone(), nameof(SparkSettings));
    }

    /// <summary>
    /// The properties a <c>Clone</c> has to carry: every public one with a setter, plus any get-only
    /// auto-property.
    /// </summary>
    /// <remarks>
    /// Computed properties (<c>EffectiveLabel</c> and the like) have no backing field and nothing to copy; if
    /// Clone dropped what one reads, the check on that stored property catches it. A get-only auto-property is
    /// included on purpose: it holds state that Clone cannot assign, so it fails the setter check below rather
    /// than being skipped by a filter.
    /// </remarks>
    private static List<PropertyInfo> StoredProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Where(p => p.CanWrite
                        || type.GetField($"<{p.Name}>k__BackingField",
                            BindingFlags.NonPublic | BindingFlags.Instance) is not null)
            .ToList();

    /// <summary>A settings section: one of the plugin's own classes, cloned deep rather than copied as a value.</summary>
    private static bool IsSection(Type type) =>
        type.IsClass && type != typeof(string) && type.Assembly == typeof(SparkSettings).Assembly;

    private static void Fill(object target, DistinctValues values)
    {
        var type = target.GetType();
        var defaults = Activator.CreateInstance(type)!;
        var properties = StoredProperties(type);
        Assert.NotEmpty(properties);

        foreach (var property in properties)
        {
            Assert.True(
                property.CanWrite,
                $"{type.Name}.{property.Name} has no setter this test can vary, so Clone() could drop it unnoticed.");

            if (IsSection(property.PropertyType))
            {
                var section = Activator.CreateInstance(property.PropertyType)!;
                Fill(section, values);
                property.SetValue(target, section);
                continue;
            }

            property.SetValue(target, values.For(property, property.GetValue(defaults)));
        }
    }

    private static void AssertCopied(object source, object clone, string path)
    {
        Assert.False(ReferenceEquals(source, clone), $"{path} is the source's own instance, not a copy.");

        foreach (var property in StoredProperties(source.GetType()))
        {
            var expected = property.GetValue(source);
            var actual = property.GetValue(clone);

            if (IsSection(property.PropertyType))
            {
                AssertCopied(expected!, actual!, $"{path}.{property.Name}");
                continue;
            }

            Assert.True(
                Equals(expected, actual),
                $"{path}.{property.Name} was not copied by Clone(): expected {expected}, got {actual}.");
        }
    }

    [Fact]
    public void Clone_survives_an_explicit_null_in_a_stored_blob()
    {
        // `"Sweep": null` deserialises past the property initialiser — a hand edit, a restored backup, an older
        // serializer. It already caused an NRE out of a scheduler pass once; Clone must not be the next place.
        var source = new SparkSettings { Sweep = null!, Deposits = null!, StableBalance = null! };

        var clone = source.Clone();

        Assert.NotNull(clone.Sweep);
        Assert.NotNull(clone.Deposits);
        Assert.NotNull(clone.StableBalance);
    }

    private static async Task<SparkServiceHarness> StartedHarness()
    {
        var h = SparkServiceHarness.Create();
        h.SeedStore(StoreId, SparkServiceHarness.MnemonicFor(11));
        await h.Service.StartAsync(CancellationToken.None);
        return h;
    }

    /// <summary>
    /// The sweep settings service over the real <c>SparkService</c>, which is both its settings store and its
    /// runtime.
    /// </summary>
    private static SparkSweepSettingsService SweepSettingsServiceOver(SparkServiceHarness h) =>
        SweepSettingsServiceOver(h.Service, h.Service);

    /// <summary>A configured store behind a settings store that hands its object out by reference.</summary>
    private static FakeSparkStoreSettingsStore ByReferenceStore()
    {
        var store = new FakeSparkStoreSettingsStore();
        store.Settings[StoreId] = new SparkSettings
        {
            ProtectedMnemonic = "protected-blob",
            PaymentKey = "a-payment-key"
        };
        return store;
    }

    private static SparkSweepSettingsService SweepSettingsServiceOver(
        ISparkStoreSettingsStore settingsStore,
        ISparkStoreRuntime runtime)
    {
        var addresses = new FakeSweepAddressSource();
        return new SparkSweepSettingsService(
            settingsStore,
            runtime,
            new SweepDestinationResolver(
                addresses, Network.RegTest, NullLogger<SweepDestinationResolver>.Instance),
            addresses,
            new InMemorySweepRecordStore(),
            NullLogger<SparkSweepSettingsService>.Instance);
    }
}
