using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The plugin's display name, and the invariants the nav icon's markup depends on.
/// </summary>
/// <remarks>
/// <para>
/// The plugin is called <b>Flint</b>: it is Seth For Privacy' work (see <c>LICENSE</c>), and a plugin calling
/// itself plain "Spark" implies it comes from Spark or from Breez. In 0.3.0 the identifier followed the
/// display name to <c>BTCPayServer.Plugins.Flint</c>, because a third party had already claimed the old
/// one on the plugin registry. <see cref="TheDisplayNameIsFlint"/> and
/// <see cref="TheIdentifierIsTheFlintOne"/> pin each end, and
/// <see cref="TheDataKeyingConstantsAreTheFlintOnes"/> pins the constants that key existing data —
/// that last one is the important one, and its own remarks explain why.
/// </para>
/// <para>
/// The icon tests exist because the nav icon is hand-written inline SVG rather than
/// <c>&lt;vc:icon symbol="…" /&gt;</c>, and that is not a stylistic choice: core's Icon component renders
/// <c>&lt;use href="~/img/icon-sprite.svg#symbol"&gt;</c>, so it can only ever address a symbol in BTCPay's
/// own sprite and a plugin has no way to add one. Someone tidying the markup back into a <c>&lt;vc:icon&gt;</c>
/// would get a blank space, which no other test in the suite would notice — nothing here renders a view.
/// </para>
/// <para>
/// Likewise the fill: BTCPay ships light and dark themes and the nav link changes colour on hover and when
/// active, so the icon has to inherit <c>currentColor</c>. The Flint mark's own colours — the slate tile, the
/// off-white letter, the amber spark — live in <c>assets/logo.svg</c> and belong only there: that file is a
/// standalone raster that inherits nothing, and this icon is the opposite case. Pasting any of those hexes
/// into the markup makes the icon wrong in some theme or hover state, and the amber spark is the tempting
/// one — the nav rendering of the mark is deliberately monochrome.
/// <see cref="TheNavIconInheritsCurrentColor"/> is that guard, and a copy-paste from the logo SVG is the
/// regression it exists to catch.
/// </para>
/// <para>
/// What is deliberately <b>not</b> here: any assertion about whether the icon looks good. Legibility at nav
/// size was settled by rasterising the candidates at 16px, 24px and 32px on light and dark backgrounds and
/// looking at them; a test cannot repeat that, and one pretending to would only encode a preference.
/// </para>
/// </remarks>
public class SparkBrandingTests
{
    private const string DisplayName = "Flint";

    /// <summary>The mark's own colours from assets/logo.svg, none of which belongs in themed markup.</summary>
    private static readonly string[] BrandFills = ["#232B36", "#EFF2F6", "#FFB020"];

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(ThisFile(), "..", ".."));

    private static string ThisFile([CallerFilePath] string path = "") => path;

    private static string NavViewPath =>
        Path.Combine(RepositoryRoot, "BTCPayServer.Plugins.Flint", "Views", "Shared", "Spark", "SparkNav.cshtml");

    private static string NavMarkup => File.ReadAllText(NavViewPath);

    /// <summary>
    /// BTCPay reflects <c>&lt;Product&gt;</c> off the built assembly for the name it shows on the plugin's
    /// registry card, and <c>BaseBTCPayServerPlugin</c> falls back to the assembly name when it is absent —
    /// so deleting the property does not fail the build, it just makes the plugin introduce itself as
    /// "BTCPayServer.Plugins.Flint".
    /// </summary>
    [Fact]
    public void TheDisplayNameIsFlint()
    {
        var product = typeof(SparkPlugin).Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;

        Assert.Equal(DisplayName, product);
    }

    /// <summary>
    /// The identifier, and that the assembly carries the same string — BTCPay resolves a plugin's assembly by
    /// the identifier it was installed under, so the csproj's <c>AssemblyName</c> and
    /// <see cref="Constants.PluginIdentifier"/> cannot be allowed to drift apart.
    /// </summary>
    /// <remarks>
    /// It became <c>BTCPayServer.Plugins.Flint</c> in 0.3.0 because a third party had already registered
    /// <c>BTCPayServer.Plugins.Flint</c> on the official plugin registry, and BTCPay joins an installed plugin
    /// to a registry entry on identifier alone — so their repository was credited as the author of this one,
    /// and their build would have been offered as an update to it. Reverting this string re-opens both.
    /// </remarks>
    [Fact]
    public void TheIdentifierIsTheFlintOne()
    {
        Assert.Equal("BTCPayServer.Plugins.Flint", Constants.PluginIdentifier);
        Assert.Equal(Constants.PluginIdentifier, new SparkPlugin().Identifier);
        Assert.Equal(Constants.PluginIdentifier, typeof(SparkPlugin).Assembly.GetName().Name);
        Assert.NotEqual(DisplayName, Constants.PluginIdentifier);
    }

    /// <summary>
    /// The constants that key existing data, pinned to their current values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These used to assert that none of them had followed the identifier rename, because at that
    /// point a change to any of them would have been an accident. They were then changed on purpose,
    /// all together, when the plugin became Flint — a deliberate break with a documented re-import
    /// procedure, taken while the only affected wallets were two the maintainer controlled.
    /// </para>
    /// <para>
    /// The test did not become pointless when that happened, it inverted: it now pins the new values
    /// so the <em>next</em> change is the accident. <see cref="Constants.DataProtectionPurpose"/> is
    /// still the one that matters most — it is the purpose string every stored mnemonic is encrypted
    /// under, and moving it again without a decrypt-and-re-encrypt migration makes every merchant's
    /// recovery phrase permanently undecryptable. The others orphan the Postgres schema, the per-store
    /// settings, the SDK storage directory, the wired Lightning payment method, and the obfuscated
    /// Breez key's XOR mask.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDataKeyingConstantsAreTheFlintOnes()
    {
        Assert.Equal("BTCPayServer.Plugins.Flint", Constants.DatabaseSchema);
        Assert.Equal("BTCPayServer.Plugins.Flint.Mnemonic", Constants.DataProtectionPurpose);
        Assert.Equal("Flint", Constants.StoreSettingsKey);
        Assert.Equal("Flint", Constants.WorkDirName);
        Assert.Equal("flint", Constants.ConnectionStringType);

        // No fragment of the previous identity may survive in a data-keying position: a half-applied
        // rename is worse than either whole one, because it decrypts some stores and not others. These
        // literals name the strings these constants used to hold, so they must not be swept up by a
        // future rebrand pass -- one already rewrote this list once, and this assertion is what caught it.
        foreach (var stale in new[] { "Spark", "breez" })
        {
            Assert.DoesNotContain(stale, Constants.DatabaseSchema, StringComparison.Ordinal);
            Assert.DoesNotContain(stale, Constants.DataProtectionPurpose, StringComparison.Ordinal);
        }

        // The XOR mask is private, so it is checked by its effect: the key still deobfuscates to
        // printable ASCII. A mask left behind by a partial rename yields mojibake, not an exception.
        Assert.Matches("^[\\x20-\\x7E]+$", Constants.BreezApiKey);
    }

    [Fact]
    public void TheNavEntryIsLabelledWithTheDisplayName()
    {
        Assert.Contains($"<span>{DisplayName}</span>", NavMarkup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The icon is inline SVG, not <c>&lt;vc:icon&gt;</c>, because that component can only reach symbols in
    /// core's sprite.
    /// </summary>
    [Fact]
    public void TheNavIconIsInlineSvgRatherThanTheIconComponent()
    {
        Assert.DoesNotContain("<vc:icon", NavMarkup, StringComparison.Ordinal);
        Assert.Contains("<svg class=\"icon\"", NavMarkup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every painted attribute is <c>currentColor</c> (or <c>none</c>), and no brand fill appears in the
    /// icon.
    /// </summary>
    /// <remarks>
    /// The brand-fill sweep is over the icon element only, not the whole view: the Razor comment above the
    /// icon quotes <c>rgb(49,41,56)</c> to explain why it must not be used, and a check that could not tell
    /// the explanation from the mistake would push the explanation out of the file.
    /// </remarks>
    [Fact]
    public void TheNavIconInheritsCurrentColor()
    {
        var icon = NavIcon();

        var paints = icon.DescendantsAndSelf()
            .SelectMany(element => new[] { element.Attribute("fill"), element.Attribute("stroke") })
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Value)
            .ToList();

        Assert.NotEmpty(paints);
        Assert.All(paints, paint => Assert.True(
            paint is "currentColor" or "none",
            $"The nav icon paints with '{paint}'. It must inherit currentColor: BTCPay has light and dark "
            + "themes, and the nav link recolours on hover and when active, so a fixed colour is invisible "
            + "in some of the states this renders in."));

        var markup = icon.ToString();
        foreach (var fill in BrandFills)
        {
            Assert.DoesNotContain(fill, markup, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The icon is authored in core's 24-unit icon box, which is what <c>.icon</c> sizes it against
    /// (<c>width/height: var(--icon-size)</c>; <c>#mainMenu</c> sets that to <c>1.5rem</c>).
    /// </summary>
    [Fact]
    public void TheNavIconIsAuthoredInCoresIconBox()
    {
        Assert.Equal("0 0 24 24", NavIcon().Attribute("viewBox")?.Value);
    }

    /// <summary>
    /// The inline <c>&lt;svg&gt;</c> from the nav view, parsed. Razor emits it verbatim, so an ill-formed
    /// element would reach the browser as-is; parsing it here is also what lets the tests above inspect
    /// attributes rather than grep for substrings.
    /// </summary>
    private static XElement NavIcon()
    {
        var markup = NavMarkup;
        var start = markup.IndexOf("<svg", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No inline <svg> in {NavViewPath}.");
        var end = markup.IndexOf("</svg>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Unterminated inline <svg> in {NavViewPath}.");

        return XElement.Parse(markup[start..(end + "</svg>".Length)]);
    }
}
