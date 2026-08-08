using BTCPayServer.Components.MainNav;
using BTCPayServer.Models.StoreViewModels;

namespace BTCPayServer.Plugins.Flint.Views;

/// <summary>
/// The store a navigation render is for, read out of whatever model core handed the extension point.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <c>SparkNav.cshtml</c> rather than left inline because it is the one piece of that file that
/// can throw, and the blast radius of a throw there is the whole server: the navigation renders inside the
/// layout, so an exception takes out every page of every store, not just Spark's. Nothing in the suite renders
/// a Razor view (see <c>ViewComponentCompatibilityTests</c> for why that is), so inline the behaviour would be
/// untested; here it is an ordinary method with an ordinary test.
/// </para>
/// <para>
/// <see cref="MainNavViewModel"/> is what core actually passes at <c>store-integrations-nav</c>, and the store
/// on it is authoritative — <c>HttpContext.GetStoreData</c> deliberately returns null while the navigation is
/// rendering, so the caller's <c>GetCurrentStoreId</c> fallback is a second choice, not the first. The other
/// two shapes cost a line each and cover the extension points a future core release might route through here.
/// Anything else yields null so the caller falls back rather than guessing.
/// </para>
/// </remarks>
public static class SparkNavStoreId
{
    /// <summary>The store id carried by <paramref name="model"/>, or null if it carries none.</summary>
    public static string? From(object? model) => model switch
    {
        MainNavViewModel mainNav => mainNav.Store?.Id,
        string storeId => storeId,
        StoreDashboardViewModel dashboard => dashboard.StoreId,
        _ => null
    };
}
