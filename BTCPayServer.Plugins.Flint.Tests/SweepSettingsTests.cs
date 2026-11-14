using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The sweep settings' defaults and their <see cref="SweepSettings.Clone"/> contract.
/// </summary>
/// <remarks>
/// That <c>Clone</c> carries every property is checked by reflection in
/// <see cref="SparkSettingsCacheTests.Clone_copies_every_property"/>, which recurses into the sweep section.
/// </remarks>
public class SweepSettingsTests
{
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
}
