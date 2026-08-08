using Newtonsoft.Json;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// How <see cref="SparkSettings"/> survives the round trip through the store-settings blob.
/// </summary>
/// <remarks>
/// <para>
/// These settings are persisted by <c>StoreRepository.UpdateSetting</c>, which is Newtonsoft JSON over a text
/// column — so what a store actually holds is whatever the shape of this type was on the day it was written. Two
/// separate defects came from having no test at this seam at all: an explicit <c>"BalanceThresholdSats": 0</c> from
/// a wave that shipped the property with no initialiser silently overriding the new default, and an explicit
/// <c>"Sweep": null</c> deserialising past a <c>= new()</c> initialiser into a <c>NullReferenceException</c> on a
/// scheduler pass.
/// </para>
/// <para>
/// The JSON literals below are deliberately hand-written rather than produced by serialising the current type. A
/// round trip through today's shape cannot reproduce yesterday's blob, which is exactly the thing that goes wrong.
/// </para>
/// </remarks>
public class SparkSettingsSerializationTests
{
    /// <summary>
    /// Newtonsoft with default settings, which is what BTCPay's store-settings repository uses.
    /// </summary>
    private static T? Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json);

    [Fact]
    public void A_blob_written_by_the_current_shape_round_trips_unchanged()
    {
        var original = new SparkSettings
        {
            ProtectedMnemonic = "protected-blob",
            PaymentKey = "payment-key",
            SeedSource = SeedSource.HotWallet,
            ApiKeyOverride = "merchant-key",
            Sweep = new SweepSettings
            {
                Enabled = true,
                BalanceThresholdSats = 4_000_000,
                ReserveSats = 50_000,
                MinimumSweepSats = 1_000_000,
                ConfirmationSpeed = SweepConfirmationSpeed.Slow,
                MaxFeePercent = 1.25,
                MaxFeeFlatSats = 30_000,
                DrainWhenSweeping = false,
                DestinationMode = SweepDestinationMode.StaticAddress,
                StaticAddress = "bcrt1qtxwcjjvf4ny9wsw9emgnpazey2vde3xhnyqpw0"
            }
        };

        var read = Deserialize<SparkSettings>(JsonConvert.SerializeObject(original))!;

        Assert.Equal("protected-blob", read.ProtectedMnemonic);
        Assert.Equal("payment-key", read.PaymentKey);
        Assert.Equal(SeedSource.HotWallet, read.SeedSource);
        Assert.Equal("merchant-key", read.ApiKeyOverride);
        Assert.True(read.Sweep.Enabled);
        Assert.Equal(4_000_000, read.Sweep.BalanceThresholdSats);
        Assert.Equal(50_000, read.Sweep.ReserveSats);
        Assert.Equal(1_000_000, read.Sweep.MinimumSweepSats);
        Assert.Equal(SweepConfirmationSpeed.Slow, read.Sweep.ConfirmationSpeed);
        Assert.Equal(1.25, read.Sweep.MaxFeePercent);
        Assert.Equal(30_000, read.Sweep.MaxFeeFlatSats);
        Assert.False(read.Sweep.DrainWhenSweeping);
        Assert.Equal(SweepDestinationMode.StaticAddress, read.Sweep.DestinationMode);
        Assert.Equal("bcrt1qtxwcjjvf4ny9wsw9emgnpazey2vde3xhnyqpw0", read.Sweep.StaticAddress);
    }

    [Fact]
    public void A_blob_with_no_sweep_section_gets_the_defaults()
    {
        var read = Deserialize<SparkSettings>(
            """{"ProtectedMnemonic":"protected-blob","PaymentKey":"key","SeedSource":0}""")!;

        Assert.NotNull(read.Sweep);
        Assert.False(read.Sweep.Enabled);
        Assert.Equal(SweepSettings.DefaultBalanceThresholdSats, read.Sweep.EffectiveBalanceThresholdSats);
    }

    [Fact]
    public void An_explicit_null_sweep_section_deserialises_to_null_despite_the_initialiser()
    {
        // Pinning the language behaviour the NullReferenceException came from, so nobody "simplifies" the coalescing
        // in the engine and the controller back out on the grounds that the property is non-nullable.
        var read = Deserialize<SparkSettings>(
            """{"ProtectedMnemonic":"protected-blob","PaymentKey":"key","Sweep":null}""")!;

        Assert.Null(read.Sweep);
    }

    [Fact]
    public void A_pre_Wave4_blob_carries_an_explicit_zero_threshold_which_reads_as_unset()
    {
        // Exactly the shape Wave 3 wrote: the property existed with no initialiser, so it serialised as zero. An
        // explicit zero wins over a property initialiser on deserialize, which is why the engine and the settings
        // form read EffectiveBalanceThresholdSats rather than the raw property.
        var read = Deserialize<SparkSettings>(
            """
            {
              "ProtectedMnemonic": "protected-blob",
              "PaymentKey": "key",
              "SeedSource": 0,
              "Sweep": { "Enabled": false, "BalanceThresholdSats": 0, "ReserveSats": 0, "FallbackDestination": null }
            }
            """)!;

        Assert.Equal(0, read.Sweep.BalanceThresholdSats);
        Assert.Equal(SweepSettings.DefaultBalanceThresholdSats, read.Sweep.EffectiveBalanceThresholdSats);
    }

    [Fact]
    public void A_pre_Wave4_blob_gets_the_new_defaults_for_properties_it_never_had()
    {
        // The complement of the test above: a property that did not exist in the old shape is absent from the blob,
        // so its initialiser does apply. That asymmetry between "absent" and "explicitly zero" is the whole trap.
        var read = Deserialize<SparkSettings>(
            """
            {
              "PaymentKey": "key",
              "Sweep": { "Enabled": false, "BalanceThresholdSats": 0, "ReserveSats": 0 }
            }
            """)!;

        Assert.Equal(SweepSettings.DefaultMinimumSweepSats, read.Sweep.MinimumSweepSats);
        Assert.Equal(SweepSettings.DefaultMaxFeePercent, read.Sweep.MaxFeePercent);
        Assert.Equal(SweepConfirmationSpeed.Medium, read.Sweep.ConfirmationSpeed);
        Assert.Equal(SweepDestinationMode.StoreWallet, read.Sweep.DestinationMode);
        Assert.True(read.Sweep.DrainWhenSweeping);
        Assert.Null(read.Sweep.MaxFeeFlatSats);
    }

    [Fact]
    public void The_dropped_FallbackDestination_property_is_ignored_rather_than_fatal()
    {
        // Wave 4 replaced FallbackDestination with DestinationMode + StaticAddress. Nothing could ever have set it —
        // no UI wrote it — but a blob mentioning it must still load rather than throwing on an unknown member.
        var read = Deserialize<SparkSettings>(
            """{"Sweep":{"FallbackDestination":"bcrt1qold","Enabled":false}}""")!;

        Assert.Equal(SweepDestinationMode.StoreWallet, read.Sweep.DestinationMode);
        Assert.Null(read.Sweep.StaticAddress);
    }

    [Fact]
    public void A_null_settings_blob_is_null_rather_than_a_default_configuration()
    {
        // What "this store has not configured Spark" looks like on the wire. It must not become an empty-but-present
        // configuration, which the engine would treat as a store to consider sweeping.
        Assert.Null(Deserialize<SparkSettings>("null"));
    }

    [Fact]
    public void The_effective_threshold_only_substitutes_for_a_non_positive_value()
    {
        // So a merchant's own small-but-deliberate threshold is not silently replaced.
        Assert.Equal(1, new SweepSettings { BalanceThresholdSats = 1 }.EffectiveBalanceThresholdSats);
        Assert.Equal(
            SweepSettings.DefaultBalanceThresholdSats,
            new SweepSettings { BalanceThresholdSats = 0 }.EffectiveBalanceThresholdSats);
        Assert.Equal(
            SweepSettings.DefaultBalanceThresholdSats,
            new SweepSettings { BalanceThresholdSats = -5 }.EffectiveBalanceThresholdSats);
    }
}
