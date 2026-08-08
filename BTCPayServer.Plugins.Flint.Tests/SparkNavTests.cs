using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BTCPayServer.Components.MainNav;
using BTCPayServer.Data;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Plugins.Flint.Controllers;
using BTCPayServer.Plugins.Flint.Views;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The store's Plugins navigation: what <c>SparkNav.cshtml</c> offers, and that every page agrees with it.
/// </summary>
/// <remarks>
/// <para>
/// Same failure class as <see cref="ViewComponentCompatibilityTests"/> and the same reason for existing: a
/// navigation entry is a bundle of strings resolved at render time — the action name by MVC's link generator,
/// the highlight by a string comparison against whatever the page put in ViewData — and nothing in the suite
/// renders a view, so the compiler and the other 1000 tests see none of it. A nav entry pointing at an action
/// that no longer exists renders as a dead <c>href="/"</c>, and a page whose menu-item id no entry uses simply
/// never highlights. Both are silent.
/// </para>
/// <para>
/// The one behavioural test here is <see cref="NoModelShapeMakesTheNavThrow"/>, which is the reason
/// <see cref="SparkNavStoreId"/> is a class rather than a switch inside the view. The navigation renders inside
/// the layout: an exception there is not a broken Spark page, it is a broken server.
/// </para>
/// </remarks>
public class SparkNavTests
{
    private static string PluginDirectory => Path.Combine(RepositoryRoot, "BTCPayServer.Plugins.Flint");

    private static string NavView => Path.Combine(PluginDirectory, "Views", "Shared", "Spark", "SparkNav.cshtml");

    private static string PageViewsDirectory => Path.Combine(PluginDirectory, "Views", "Spark");

    /// <summary>
    /// Repository root, derived from this file's compile-time path — see the note on the same member in
    /// <see cref="ViewComponentCompatibilityTests"/>.
    /// </summary>
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(ThisFile(), "..", ".."));

    private static string ThisFile([CallerFilePath] string path = "") => path;

    /// <summary>
    /// No model core could hand the extension point makes the store-id resolution throw.
    /// </summary>
    /// <remarks>
    /// The shapes below are, in order: what core actually passes; the same before a store is resolved (core
    /// guards this itself today, but the guard is core's to remove); the two shapes tolerated in case a future
    /// release routes another extension point through here; and four that model "core passed something this
    /// plugin has never seen", which is the case that must degrade to the caller's fallback rather than to a
    /// 500 on every page of the server.
    /// </remarks>
    [Fact]
    public void NoModelShapeMakesTheNavThrow()
    {
        Assert.Equal("store-from-nav", SparkNavStoreId.From(new MainNavViewModel { Store = new StoreData { Id = "store-from-nav" } }));
        Assert.Null(SparkNavStoreId.From(new MainNavViewModel { Store = null }));
        Assert.Equal("store-as-string", SparkNavStoreId.From("store-as-string"));
        Assert.Equal("store-from-dashboard", SparkNavStoreId.From(new StoreDashboardViewModel { StoreId = "store-from-dashboard" }));

        Assert.Null(SparkNavStoreId.From(null));
        Assert.Null(SparkNavStoreId.From(new object()));
        Assert.Null(SparkNavStoreId.From(42));
        Assert.Null(SparkNavStoreId.From(new StoreDashboardViewModel { StoreId = null }));
    }

    /// <summary>
    /// An empty string reaches the caller unchanged rather than being smoothed into null.
    /// </summary>
    /// <remarks>
    /// Pinned because the view's guard is <c>string.IsNullOrEmpty</c> on the resolved id and not on the raw
    /// model: a resolver that quietly turned "" into null would send the view to <c>GetCurrentStoreId</c>, i.e.
    /// render a Spark entry for whichever store the URL happens to name, on a page whose model said it had no
    /// store. The empty string is a caller's answer of "no store", and stays one.
    /// </remarks>
    [Fact]
    public void AnEmptyStoreIdIsNotTreatedAsAMissingOne()
    {
        Assert.Equal(string.Empty, SparkNavStoreId.From(string.Empty));
    }

    /// <summary>
    /// Every destination the nav links to is a GET action on <see cref="SparkController"/>.
    /// </summary>
    /// <remarks>
    /// <c>asp-action</c> is a string the link generator resolves at render time. Name an action that does not
    /// exist — renamed, or made POST-only — and the tag helper does not fail: it emits <c>href="/"</c>, so the
    /// nav entry renders, looks right, and takes the merchant to the home page.
    /// </remarks>
    [Fact]
    public void EveryNavDestinationIsAGetActionOnTheController()
    {
        var actions = NavActions(File.ReadAllText(NavView)).ToList();

        // Four entries' worth today (Status/Setup on the top entry, Sweep, StableBalance). A regex that
        // silently stopped matching would otherwise pass this test forever.
        Assert.NotEmpty(actions);

        var gets = typeof(SparkController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpGetAttribute>().Any())
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = actions.Where(action => !gets.Contains(action)).ToList();
        Assert.True(
            missing.Count == 0,
            $"SparkNav.cshtml links to {string.Join(", ", missing)}, which {(missing.Count == 1 ? "is not a GET action" : "are not GET actions")} "
            + $"on SparkController. The link generator does not fail on an unknown action, it emits href=\"/\" — the entry "
            + $"still renders and still looks right. GET actions available: {string.Join(", ", gets.Order())}");
    }

    /// <summary>
    /// Every page sets a menu-item id, and every id it sets is one the nav renders.
    /// </summary>
    /// <remarks>
    /// The highlight is a string comparison in core's <c>layout-menu-item</c> tag helper against the value the
    /// page put in ViewData, so a page naming an id no entry uses just never highlights — and a page that sets
    /// none highlights nothing while claiming a title. Pages without an entry of their own (deposits, removal,
    /// the sweep confirmation) deliberately borrow the id of the entry they sit under, which is why this checks
    /// membership rather than a one-to-one mapping.
    /// </remarks>
    [Fact]
    public void EveryPageHighlightsAMenuItemTheNavRenders()
    {
        var rendered = MenuItemIdsRenderedByTheNav();

        var pages = Directory.EnumerateFiles(PageViewsDirectory, "*.cshtml")
            .Where(file => !Path.GetFileName(file).StartsWith('_'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(pages);

        foreach (var page in pages)
        {
            var name = Path.GetFileName(page);
            var declared = LayoutModelMenuItem.Matches(File.ReadAllText(page))
                .Select(match => match.Groups["constant"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.True(declared.Count == 1, $"{name} should set exactly one SparkNavPages constant via SetLayoutModel, not {declared.Count}.");
            Assert.True(
                rendered.Contains(declared[0]),
                $"{name} highlights SparkNavPages.{declared[0]}, which SparkNav.cshtml never renders — so the page "
                + $"highlights nothing. Rendered: {string.Join(", ", rendered.Order())}");
        }
    }

    /// <summary>
    /// No identifier on <see cref="SparkNavPages"/> is dead, and none is used by two entries.
    /// </summary>
    /// <remarks>
    /// The tag helper emits the value as an element id (<c>menu-item-{value}</c>) in a document core and every
    /// other installed plugin also write into, so a duplicate is invalid HTML and lights two entries at once. A
    /// constant no entry uses is the other half: a page can name it, and then highlight nothing.
    /// </remarks>
    [Fact]
    public void EveryMenuItemIdentifierIsRenderedByExactlyOneNavEntry()
    {
        var declared = typeof(SparkNavPages)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => field.Name)
            .ToList();
        Assert.NotEmpty(declared);

        var rendered = MenuItemUsesInTheNav();
        Assert.Equal(declared.Order(), rendered.Select(use => use.Key).Order());

        var duplicated = rendered.Where(use => use.Value > 1).Select(use => use.Key).ToList();
        Assert.True(
            duplicated.Count == 0,
            $"SparkNav.cshtml uses {string.Join(", ", duplicated)} on more than one entry. The layout-menu-item tag "
            + "helper turns the value into an element id, so two entries sharing one produce a duplicate id and "
            + "highlight together.");
    }

    /// <summary>The constant names the nav passes to <c>layout-menu-item</c>, with how often each is used.</summary>
    private static Dictionary<string, int> MenuItemUsesInTheNav() =>
        MenuItemAttribute.Matches(File.ReadAllText(NavView))
            .GroupBy(match => match.Groups["constant"].Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static HashSet<string> MenuItemIdsRenderedByTheNav() =>
        MenuItemUsesInTheNav().Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary><c>layout-menu-item="@SparkNavPages.Something"</c>.</summary>
    private static readonly Regex MenuItemAttribute =
        new(@"layout-menu-item=""@SparkNavPages\.(?<constant>\w+)""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary><c>SetLayoutModel(new LayoutModel(SparkNavPages.Something, …))</c>.</summary>
    private static readonly Regex LayoutModelMenuItem =
        new(@"SetLayoutModel\s*\(\s*new\s+LayoutModel\s*\(\s*SparkNavPages\.(?<constant>\w+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The action names an <c>asp-action</c> in the nav can resolve to.
    /// </summary>
    /// <remarks>
    /// Two spellings, because the top entry's destination is state-dependent and written inline: a bare literal
    /// (<c>asp-action="Sweep"</c>), and a Razor expression whose branches are literals
    /// (<c>asp-action="@(configured ? "Status" : "Setup")"</c>). Every literal in the expression is a
    /// destination the entry can actually take, so all of them are collected.
    /// </remarks>
    private static IEnumerable<string> NavActions(string view)
    {
        foreach (Match match in AspAction.Matches(view))
        {
            if (match.Groups["literal"].Success)
            {
                yield return match.Groups["literal"].Value;
                continue;
            }

            foreach (Match branch in QuotedIdentifier.Matches(match.Groups["expression"].Value))
                yield return branch.Groups["name"].Value;
        }
    }

    private static readonly Regex AspAction =
        new(@"asp-action=""(?:(?<expression>@\([^)]*\))|(?<literal>[A-Za-z]\w*))""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedIdentifier =
        new(@"""(?<name>[A-Za-z]\w*)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The action scanner reads both spellings, including the ones the nav does not use today.
    /// </summary>
    /// <remarks>
    /// A scanner that matched nothing would make <see cref="EveryNavDestinationIsAGetActionOnTheController"/>
    /// pass vacuously, which is worse than not having it.
    /// </remarks>
    [Theory]
    [InlineData(@"<a asp-action=""Sweep"" asp-route-storeId=""@storeId"">", "Sweep")]
    [InlineData(@"asp-action=""@(configured ? ""Status"" : ""Setup"")""", "Status,Setup")]
    [InlineData(@"asp-action=""StableBalance""", "StableBalance")]
    public void ActionScannerReadsBothSpellings(string line, string expected)
    {
        Assert.Equal(expected.Split(','), NavActions(line));
    }

    /// <summary>Attributes that are not an action reference are not read as one.</summary>
    [Theory]
    [InlineData(@"<a asp-controller=""Spark"" asp-route-storeId=""@storeId"">")]
    [InlineData(@"<li class=""nav-item"" permission=""@Policies.CanViewStoreSettings"">")]
    public void ActionScannerIgnoresOtherAttributes(string line)
    {
        Assert.Empty(NavActions(line));
    }
}
