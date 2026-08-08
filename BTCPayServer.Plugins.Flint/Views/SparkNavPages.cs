namespace BTCPayServer.Plugins.Flint.Views;

/// <summary>
/// Menu-item identifiers for the plugin's pages.
/// </summary>
/// <remarks>
/// <para>
/// These are the values passed to <c>ViewData.SetLayoutModel</c> and matched by the
/// <c>layout-menu-item</c> tag helper in <c>SparkNav.cshtml</c>, which is how a page highlights its own
/// entry in the store's Plugins navigation. Strings rather than an enum because the tag helper compares
/// them as strings.
/// </para>
/// <para>
/// One constant per <em>nav entry</em>, not per page: the tag helper also emits the value as an element id
/// (<c>menu-item-{value}</c>), so two entries sharing a constant would produce a duplicate id and light up
/// together. Pages that are reached from a section rather than from the nav — the sweep confirmation, the
/// deposit and removal pages — borrow the constant of the entry they sit under, which is what keeps that
/// entry highlighted while the merchant is on them. The values are prefixed because the id lands in a
/// document shared with core and every other installed plugin.
/// </para>
/// </remarks>
public static class SparkNavPages
{
    /// <summary>The top-level entry: the status page, or setup while the store has no Spark wallet.</summary>
    public const string Spark = "Spark";

    /// <summary>Sweep configuration and history, and the confirmation page a manual sweep goes through.</summary>
    public const string Sweeps = "SparkSweeps";

    /// <summary>Stable Balance settings. Rendered only where Stable Balance can work — mainnet.</summary>
    public const string StableBalance = "SparkStableBalance";
}
