using System.Reflection;
using BTCPayServer.Plugins.Flint.Models;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Pins which setup-form properties model binding may and may not touch.
/// </summary>
/// <remarks>
/// An attribute attaches to the declaration that follows it, comments notwithstanding, so a property moved
/// without its attribute leaves the attribute on whatever now comes next. That exact slip put
/// <c>[BindNever]</c> on <see cref="SparkSetupViewModel.EnableSweeping"/>: the setup page's sweeping opt-in
/// posted true, bound to nothing, and the merchant who asked for auto-sweep did not get it — while
/// <see cref="SparkSetupViewModel.AlreadyConfigured"/>, the property the attribute was written for, became
/// overpostable. Reflection is the cheapest honest test: it reads the compiled attribute placement, which is
/// what the model binder reads.
/// </remarks>
public class SparkSetupViewModelBindingTests
{
    [Fact]
    public void The_sweeping_opt_in_is_bindable()
    {
        var property = typeof(SparkSetupViewModel).GetProperty(nameof(SparkSetupViewModel.EnableSweeping))!;
        Assert.Null(property.GetCustomAttribute<BindNeverAttribute>());
    }

    [Fact]
    public void The_already_configured_flag_is_not_bindable()
    {
        var property = typeof(SparkSetupViewModel).GetProperty(nameof(SparkSetupViewModel.AlreadyConfigured))!;
        Assert.NotNull(property.GetCustomAttribute<BindNeverAttribute>());
    }
}
