using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The Advanced page's sweep-tuning form: it owns exactly two fields, and cannot touch the rest.
/// </summary>
/// <remarks>
/// The reserve and the fee policy moved off the sweep page because their defaults are right for almost every
/// store. What makes the move safe is the merge contract tested here: <c>AdvancedSweep</c> reads the stored
/// configuration, overrides only the two fields it displays, and pushes the whole object through the same
/// validation path the sweep form and the API use. A regression in that merge is a form silently rewriting a
/// destination or a threshold it never showed the merchant.
/// </remarks>
public class SparkAdvancedPageTests
{
    private const string Store = SparkSurfaceHarness.AttackerStore;

    /// <summary>A valid regtest address the fake wallet does not hand out.</summary>
    private const string OwnAddress = "bcrt1qt8hufshrz62z5vj4q40uqx6c6ytlujy5s03gwm";

    [Fact]
    public async Task Saving_the_advanced_form_changes_only_the_reserve_and_the_fee_policy()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        h.Settings.Settings[Store]!.Sweep = new SweepSettings
        {
            Enabled = true,
            BalanceThresholdSats = 400_000,
            MinimumSweepSats = 150_000,
            ConfirmationSpeed = SweepConfirmationSpeed.Fast,
            MaxFeePercent = 2.5,
            DestinationMode = SweepDestinationMode.StaticAddress,
            StaticAddress = OwnAddress
        };

        var result = await h.Mvc.AdvancedSweep(
            Store,
            new SparkAdvancedViewModel
            {
                Settings = new SweepSettingsInput { ReserveSats = 50_000, DrainWhenSweeping = false }
            },
            CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);

        var sweep = h.Settings.Settings[Store]!.Sweep;
        Assert.Equal(50_000, sweep.ReserveSats);
        Assert.False(sweep.DrainWhenSweeping);

        // Everything the form never displayed is exactly as it was stored.
        Assert.True(sweep.Enabled);
        Assert.Equal(400_000, sweep.BalanceThresholdSats);
        Assert.Equal(150_000, sweep.MinimumSweepSats);
        Assert.Equal(SweepConfirmationSpeed.Fast, sweep.ConfirmationSpeed);
        Assert.Equal(2.5, sweep.MaxFeePercent);
        Assert.Equal(SweepDestinationMode.StaticAddress, sweep.DestinationMode);
        Assert.Equal(OwnAddress, sweep.StaticAddress);
    }

    [Fact]
    public async Task An_invalid_combination_is_refused_and_nothing_is_written()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        h.Settings.Settings[Store]!.Sweep = new SweepSettings
        {
            Enabled = true,
            DestinationMode = SweepDestinationMode.StaticAddress,
            StaticAddress = OwnAddress
        };

        // Fee-on-top with no reserve to pay the fee from: the cross-field rule the merge must still hit,
        // because the Enabled flag it depends on comes from storage rather than from this form.
        var result = await h.Mvc.AdvancedSweep(
            Store,
            new SparkAdvancedViewModel
            {
                Settings = new SweepSettingsInput { ReserveSats = 0, DrainWhenSweeping = false }
            },
            CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.False(h.Mvc.ModelState.IsValid);
        Assert.Empty(h.Settings.Writes);
    }

    [Fact]
    public async Task The_advanced_page_shows_the_stored_configuration()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        h.Settings.Settings[Store]!.Sweep = new SweepSettings
        {
            ReserveSats = 21_000,
            DrainWhenSweeping = false
        };

        var result = await h.Mvc.Advanced(Store, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SparkAdvancedViewModel>(view.Model);
        Assert.Equal(Store, model.StoreId);
        Assert.Equal(21_000, model.Settings.ReserveSats);
        Assert.False(model.Settings.DrainWhenSweeping);
    }
}
