using System.Reflection;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The sweep settings' defaults and their <see cref="SweepSettings.Clone"/> contract.
/// </summary>
public class SweepSettingsTests
{
    [Fact]
    public void Clone_copies_every_property()
    {
        // The hazard this pins is real and silent: SparkStoreProvisioner carries a store's sweep settings across a
        // seed change by cloning them, so a property missing from Clone() is a setting that quietly reverts to its
        // default the next time a merchant replaces their seed. Written by reflection rather than field by field
        // precisely so that adding a property without extending Clone() fails here — a hand-written assertion list
        // would have to be remembered too, which is the thing that was already forgotten once.
        // Every readable instance property, not only the settable ones. Filtering on CanWrite would silently skip a
        // property added later as init-only or with a private setter — which Clone() would then also fail to copy,
        // and this test would report success. Computed properties are excluded by name because they have nothing to
        // copy, and each exclusion has to be justified here rather than by an accident of the filter.
        //
        // Each of the Effective* properties below reads one of the stored properties above and substitutes a
        // default when it is unset. They have nothing of their own to copy — and, more to the point, if Clone()
        // dropped the property one of them reads, the reflection check on that stored property is what catches
        // it. Excluding a stored property here would be the dangerous edit; excluding these is not.
        string[] computed =
        [
            nameof(SweepSettings.EffectiveBalanceThresholdSats),
            nameof(SweepSettings.EffectiveCrossChainChain),
            nameof(SweepSettings.EffectiveCrossChainAsset),
            nameof(SweepSettings.EffectiveCrossChainSlippageBps),
            nameof(SweepSettings.EffectiveCrossChainMinimumStableUnits),
            nameof(SweepSettings.EffectiveMinimumSweepSats)
        ];

        var all = typeof(SweepSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .ToList();

        var properties = all.Where(p => !computed.Contains(p.Name)).ToList();

        Assert.NotEmpty(properties);
        Assert.All(properties, p => Assert.True(
            p.CanWrite,
            $"SweepSettings.{p.Name} has no setter this test can vary. Either give it one, or add it to the "
            + "`computed` list above with a reason — silently skipping it would let Clone() drop it unnoticed."));

        var source = new SweepSettings();
        foreach (var property in properties)
            property.SetValue(source, DistinctValueFor(property));

        var clone = source.Clone();

        foreach (var property in properties)
        {
            Assert.Equal(
                property.GetValue(source),
                property.GetValue(clone));
        }
    }

    [Fact]
    public void Clone_is_independent_of_its_source()
    {
        // The reason Clone() exists at all: the service's settings cache hands these objects out by reference, so
        // an aliased sweep configuration would make an edit to a new one silently edit the old — including the copy
        // a failed provisioning attempt is supposed to roll back to.
        var source = new SweepSettings { BalanceThresholdSats = 500_000, StaticAddress = "bcrt1qoriginal" };
        var clone = source.Clone();

        clone.BalanceThresholdSats = 1;
        clone.StaticAddress = "bcrt1qedited";

        Assert.Equal(500_000, source.BalanceThresholdSats);
        Assert.Equal("bcrt1qoriginal", source.StaticAddress);
    }

    [Fact]
    public void Sweeping_is_off_by_default()
    {
        Assert.False(new SweepSettings().Enabled);
    }

    [Fact]
    public void The_defaults_form_a_configuration_that_can_actually_sweep()
    {
        // Not a tautology over the constants: this asserts a relationship between three of them that has to hold or
        // the shipped defaults never sweep anything. The threshold minus the reserve must clear the minimum, and the
        // fee ceiling must clear the worst tier fee actually observed — otherwise a merchant who turns sweeping on
        // and changes nothing gets a store that refuses every pass.
        var settings = new SweepSettings();

        var sweepableAtThreshold = settings.BalanceThresholdSats - settings.ReserveSats;
        Assert.True(
            sweepableAtThreshold >= settings.MinimumSweepSats,
            $"the default threshold makes {sweepableAtThreshold} sweepable, below the {settings.MinimumSweepSats} "
            + "default minimum");

        var allowedFeeAtFloor = settings.MinimumSweepSats * settings.MaxFeePercent / 100d;
        Assert.True(
            allowedFeeAtFloor > Constants.IndicativeCoopExitFeeSats,
            $"at the default minimum sweep the fee ceiling is {allowedFeeAtFloor} sat, which would refuse the "
            + $"{Constants.IndicativeCoopExitFeeSats} sat fee measured on regtest");
    }

    [Fact]
    public void The_default_fee_guard_is_a_percentage_and_not_a_flat_limit()
    {
        // Coop-exit fees are flat and amount-independent — the funded regtest run measured the same
        // 1,950-2,430 sat total from 294 sats swept to 99,901 — so a flat default would either never bite or
        // would refuse every sweep the first time mainnet broadcast fees rose past it.
        var settings = new SweepSettings();

        Assert.Null(settings.MaxFeeFlatSats);
        Assert.True(settings.MaxFeePercent > 0);
    }

    [Fact]
    public void Draining_is_on_by_default_because_the_default_reserve_is_zero()
    {
        // With the fee charged on top, the reserve is what pays it — and the default reserve is nothing.
        var settings = new SweepSettings();

        Assert.Equal(0, settings.ReserveSats);
        Assert.True(settings.DrainWhenSweeping);
    }

    [Fact]
    public void The_default_destination_is_the_stores_own_wallet_with_no_address_configured()
    {
        var settings = new SweepSettings();

        Assert.Equal(SweepDestinationMode.StoreWallet, settings.DestinationMode);
        Assert.Null(settings.StaticAddress);
    }

    [Fact]
    public void The_default_confirmation_speed_is_not_the_most_expensive_tier()
    {
        // The SDK's own enum is ordered Fast = 0, which is why this plugin declares its own: a merchant who never
        // touches the setting must not be buying the most expensive tier because zero happened to mean "fast".
        Assert.Equal(SweepConfirmationSpeed.Medium, new SweepSettings().ConfirmationSpeed);
        Assert.NotEqual(SweepConfirmationSpeed.Fast, default(SweepConfirmationSpeed));
    }

    /// <summary>
    /// A value distinguishable from the property's default, so a missed copy in <c>Clone</c> shows up.
    /// </summary>
    private static object DistinctValueFor(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(bool))
            // Inverted rather than set true: DrainWhenSweeping already defaults to true, and a clone that dropped
            // it would still compare equal against a hard-coded true.
            return !(bool)(property.GetValue(new SweepSettings()) ?? false);
        if (type == typeof(long))
            return 1_234_567L;
        if (type == typeof(double))
            return 7.25d;
        if (type == typeof(uint))
            // Inside the 10–500 range the cross-chain slippage field accepts and different from its default,
            // so the value is one Clone() could plausibly be asked to carry rather than an obvious sentinel.
            return 137u;
        if (type == typeof(string))
            return "distinct-value";
        if (type.IsEnum)
        {
            // The last member, so it differs from every default (all of which are the first or second).
            var values = Enum.GetValues(type);
            return values.GetValue(values.Length - 1)!;
        }

        throw new NotSupportedException(
            $"SweepSettings.{property.Name} is a {type.Name}, which this test does not know how to vary. Add a "
            + "case above so Clone() stays covered.");
    }
}
