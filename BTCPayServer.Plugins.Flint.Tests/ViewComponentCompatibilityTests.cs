using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Every view component the plugin's views invoke exists in the BTCPay Server release the plugin is pinned to.
/// </summary>
/// <remarks>
/// <para>
/// These exist because of a bug that reached a real deployment while the whole suite was green. Five views use
/// <c>&lt;vc:title-header /&gt;</c>, and <c>TitleHeader</c> was only added in BTCPay v2.4.1 (upstream
/// <c>7d206d935</c>, "Rename &lt;vc:breadcrumb&gt;") — but <see cref="Constants.MinBTCPayServerVersion"/> had been
/// lowered to 2.4.0 on the strength of the plugin compiling clean and the suite passing against the v2.4.0 tag.
/// </para>
/// <para>
/// Neither of those could have caught it. A <c>&lt;vc:…&gt;</c> tag compiles into a call to
/// <c>IViewComponentHelper.InvokeAsync(<em>string</em>)</c>, so the component's identity survives compilation only
/// as a name looked up in a dictionary built at startup from the <em>host's</em> assemblies; and no test in the
/// suite renders a view, so nothing ever performed that lookup. On a 2.4.0 host the first request to any Spark
/// page threw <c>InvalidOperationException: A view component named 'TitleHeader' could not be found</c>, BTCPay
/// auto-disabled the plugin and restarted, and every plugin route — MVC and Greenfield — 404'd until an operator
/// re-enabled it.
/// </para>
/// <para>
/// So this performs that lookup at test time: it scans the plugin's <c>.cshtml</c> for component invocations and
/// resolves each against the set of components ASP.NET Core would actually discover in the pinned submodule's
/// <c>BTCPayServer</c> assembly plus the plugin's own. It is a guard on the <em>pin</em>, and therefore also the
/// mechanical half of the check required before <see cref="Constants.MinBTCPayServerVersion"/> may be lowered:
/// point the submodule at the candidate floor's tag, and this test says whether the views can render there.
/// (Verified: on a v2.4.0 checkout the plugin still compiles clean, and this fails naming all five views.)
/// </para>
/// <para>
/// <see cref="EveryPartialAndLayoutThePluginNamesExists"/> extends the same idea to the plugin's other
/// string-addressed views — partials, the layout, and the partials registered against core's UI extension points.
/// Tag helpers are <b>not</b> covered; see the note on that test for why, and what remains uncovered.
/// </para>
/// </remarks>
public class ViewComponentCompatibilityTests
{
    /// <summary>
    /// The plugin's views, i.e. what the guard protects.
    /// </summary>
    private static string PluginViewsDirectory => Path.Combine(RepositoryRoot, "BTCPayServer.Plugins.Flint", "Views");

    /// <summary>
    /// BTCPay's own views, used only to prove this test's tag-name arithmetic against a corpus that is known to
    /// render — see <see cref="BTCPaysOwnViewsAllResolve"/>.
    /// </summary>
    private static string CoreViewsDirectory => Path.Combine(RepositoryRoot, "btcpayserver", "BTCPayServer");

    /// <summary>
    /// Repository root, derived from this file's compile-time path rather than from the test assembly's location:
    /// the output directory's depth below the project is an MSBuild detail, and <c>dotnet test</c> may run the
    /// assembly from somewhere else entirely.
    /// </summary>
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(ThisFile(), "..", ".."));

    private static string ThisFile([CallerFilePath] string path = "") => path;

    /// <summary>
    /// The BTCPay release actually compiled into <c>BTCPayServer.dll</c> here, from its assembly version.
    /// </summary>
    /// <remarks>
    /// Read from the assembly rather than from <see cref="Constants.BuiltAgainstBTCPayServerVersion"/> because the
    /// interesting moment for these tests is precisely when the two disagree: re-pointing the submodule at a
    /// candidate support floor and re-running is how the check below is meant to be used, and a message quoting the
    /// constant would then name the wrong release. btcpayserver's <c>Build/Version.csproj</c> carries the release
    /// number, so the built assembly's version is the tag.
    /// </remarks>
    private static string BuiltBTCPayServerVersion =>
        typeof(Hosting.BTCPayServerServices).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "unknown";

    /// <summary>
    /// The submodule checkout and <see cref="Constants.BuiltAgainstBTCPayServerVersion"/> agree.
    /// </summary>
    /// <remarks>
    /// The constant is documentation of the pin, and the update workflow claims to move the two together. Nothing
    /// enforced that, so a hand-edited submodule pointer could leave the constant — and the docs/building.md paragraph and PR
    /// body that quote it — describing a release nobody is building against. It also keeps the honest reading of
    /// the guard below: what it proves is a statement about whichever BTCPay is checked out.
    /// </remarks>
    [Fact]
    public void ThePinnedSubmoduleIsTheVersionTheConstantClaims()
    {
        Assert.Equal(Constants.BuiltAgainstBTCPayServerVersion, BuiltBTCPayServerVersion);
    }

    [Fact]
    public void EveryViewComponentThePluginInvokesExistsInThePinnedBTCPay()
    {
        var catalogue = AvailableComponents();
        Assert.True(Directory.Exists(PluginViewsDirectory), $"Plugin views directory not found: {PluginViewsDirectory}");

        var references = ScanForComponentReferences(PluginViewsDirectory).ToList();

        // A scan that finds nothing would pass vacuously forever. The views do use components; if they ever stop,
        // deleting this guard should be the deliberate decision rather than an unnoticed side effect.
        Assert.NotEmpty(references);

        var unresolved = references.Where(reference => !catalogue.Resolves(reference.Name)).ToList();
        if (unresolved.Count == 0)
            return;

        var message = new StringBuilder()
            .AppendLine($"{unresolved.Count} view component reference(s) in the plugin's views do not exist in the")
            .AppendLine($"BTCPay Server actually built here (v{BuiltBTCPayServerVersion}), nor in the plugin itself:")
            .AppendLine();
        foreach (var reference in unresolved)
        {
            message.AppendLine($"  {reference.File}:{reference.Line}: {reference.Syntax} -> view component '{reference.Name}'");
        }

        message
            .AppendLine()
            .AppendLine("A Razor view component is resolved BY NAME AT RENDER TIME, so this compiles and every other")
            .AppendLine("test passes; the failure only appears when a browser first hits the page, as")
            .AppendLine("InvalidOperationException: A view component named '…' could not be found. BTCPay treats that")
            .AppendLine("as a faulty plugin: it auto-disables it and restarts, and every plugin route 404s until an")
            .AppendLine("operator re-enables it by hand.")
            .AppendLine()
            .AppendLine($"Either the view is wrong, or Constants.MinBTCPayServerVersion (currently {Constants.MinBTCPayServerVersion})")
            .AppendLine("is wrong: the component may have been added, renamed or removed in a release newer than the")
            .AppendLine("declared floor. Compiling against an older BTCPay proves nothing here — only a render does.")
            .AppendLine()
            .AppendLine($"Components that do exist ({catalogue.TagNames.Count}), as their <vc:…> spelling:")
            .AppendLine($"  {string.Join(", ", catalogue.TagNames)}");

        Assert.Fail(message.ToString());
    }

    /// <summary>
    /// The same resolution, applied to BTCPay's own views.
    /// </summary>
    /// <remarks>
    /// This is a self-test of the test. Resolving <c>&lt;vc:title-header /&gt;</c> to the class <c>TitleHeader</c>
    /// means reproducing the kebab-casing Razor applies to a component's short name (see <see cref="ToHtmlCase"/>),
    /// and a subtly wrong reproduction would make the guard above quietly accept anything. BTCPay's own views are a
    /// corpus of several hundred invocations that provably resolve on a running BTCPay, so if the discovery or the
    /// casing were wrong, this would fail.
    /// </remarks>
    [Fact]
    public void BTCPaysOwnViewsAllResolve()
    {
        var catalogue = AvailableComponents();
        Assert.True(Directory.Exists(CoreViewsDirectory), $"Submodule not checked out: {CoreViewsDirectory}");

        var references = ScanForComponentReferences(CoreViewsDirectory).ToList();
        Assert.NotEmpty(references);

        var unresolved = references.Where(reference => !catalogue.Resolves(reference.Name)).ToList();
        Assert.True(
            unresolved.Count == 0,
            "This test's view-component discovery or its kebab-case arithmetic is wrong: BTCPay's own views " +
            "reference components it cannot resolve, and those views demonstrably render. Unresolved: " +
            string.Join(", ", unresolved.Select(reference => $"{reference.File}:{reference.Line} '{reference.Name}'")));
    }

    /// <summary>
    /// Every partial and layout the plugin names as a string exists in the pinned BTCPay or in the plugin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same failure class as the view components above — a name resolved by the view engine at render time,
    /// invisible to the compiler and to a suite that never renders — applied to the plugin's other string-addressed
    /// views: two of core's partials (<c>_StatusMessage</c>, <c>_ValidationScriptsPartial</c>), core's
    /// <c>_Layout.cshtml</c>, and the five plugin partials registered against core's UI extension points, whose
    /// names live in <c>SparkPlugin.cs</c> rather than in any view.
    /// </para>
    /// <para>
    /// <b>A necessary condition, not the view engine.</b> Resolution here models Razor's two cases — a name ending
    /// in <c>.cshtml</c> is a path relative to the referencing view, anything else is looked for at
    /// <c>Views/Shared/{name}.cshtml</c> and <c>Views/{referencing view's folder}/{name}.cshtml</c> — over a
    /// composite of the plugin's and core's <c>Views</c> trees, which is how BTCPay composes them for a plugin. It
    /// does not run core's view location expanders, so a file that exists but is not reachable would pass. Names
    /// built from a Razor expression are skipped outright, since there is nothing static to check; the plugin has
    /// none today.
    /// </para>
    /// <para>
    /// <b>Tag helpers are the remaining gap, deliberately left open.</b> They are a real hazard of the same family —
    /// a compiled view instantiates the tag helper <em>type</em>, so one that a host's older BTCPay does not have is
    /// a <c>TypeLoadException</c> at render — but deciding which helper an element binds to means reimplementing
    /// Razor's binding: <c>[HtmlTargetElement]</c> patterns, required attributes and parent constraints, across
    /// every registered assembly. A half-right version would report elements that bind to nothing as failures and
    /// miss real ones, which is worse than no check. As it stands the exposure is nil: the plugin's views use only
    /// <c>asp-*</c> helpers from <c>Microsoft.AspNetCore.Mvc.TagHelpers</c>, part of the framework rather than of
    /// BTCPay, and none of BTCPay's own (whose element targets are <c>srv-model</c>, <c>use</c>, and attribute
    /// conditions on ordinary elements). Adding a BTCPay-specific tag helper to a view puts that back on the table.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryPartialAndLayoutThePluginNamesExists()
    {
        var references = ScanForViewNameReferences().ToList();
        Assert.NotEmpty(references);

        var missing = references.Where(reference => !ViewFileExists(reference)).ToList();
        Assert.True(
            missing.Count == 0,
            $"{missing.Count} view(s) the plugin names as a string could not be found in the BTCPay actually built "
            + $"here (v{BuiltBTCPayServerVersion}) nor in the plugin. A partial or layout name is resolved at "
            + "render time, so this compiles and every other test passes — the page just throws when a browser "
            + "reaches it, and BTCPay auto-disables the plugin. Either the reference is wrong, or "
            + $"Constants.MinBTCPayServerVersion (currently {Constants.MinBTCPayServerVersion}) is:\n  "
            + string.Join("\n  ", missing.Select(reference => $"{reference.File}:{reference.Line}: '{reference.Name}'")));
    }

    /// <summary>
    /// <c>&lt;partial name="…" /&gt;</c>, <c>Html.Partial…("…")</c>, <c>Layout = "…"</c> in the plugin's views, plus
    /// the partial names <c>SparkPlugin</c> registers against core's UI extension points.
    /// </summary>
    private static IEnumerable<ViewNameReference> ScanForViewNameReferences()
    {
        foreach (var file in Directory.EnumerateFiles(PluginViewsDirectory, "*.cshtml", SearchOption.AllDirectories)
                     .Concat([Path.Combine(RepositoryRoot, "BTCPayServer.Plugins.Flint", "SparkPlugin.cs")])
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(RepositoryRoot, file);
            // Extension points name a partial from C#, and are resolved from core's UiExtensionPoint component, so
            // the referencing folder is core's Views/Shared rather than anything in this file.
            var searchFolder = file.EndsWith(".cshtml", StringComparison.Ordinal)
                ? Path.GetDirectoryName(file)!
                : Path.Combine(PluginViewsDirectory, "Shared");
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (var name in ScanLineForViewNames(lines[index]))
                    yield return new ViewNameReference(relative, index + 1, name, searchFolder);
            }
        }
    }

    /// <summary>The statically resolvable view names on one line.</summary>
    private static IEnumerable<string> ScanLineForViewNames(string line) =>
        ViewNameReferenceSyntax.Matches(line)
            .Select(match => match.Groups["name"].Value)
            // Built at render time from a model value; nothing static to resolve.
            .Where(name => !name.Contains('@', StringComparison.Ordinal));

    /// <summary>
    /// The view-name scanner picks up every spelling, including the ones the plugin does not use today.
    /// </summary>
    [Theory]
    [InlineData("<partial name=\"_StatusMessage\" />", "_StatusMessage")]
    [InlineData("    <partial name=\"_ValidationScriptsPartial\" />", "_ValidationScriptsPartial")]
    [InlineData("@await Html.PartialAsync(\"_StatusMessage\")", "_StatusMessage")]
    [InlineData("@Html.Partial(\"_StatusMessage\")", "_StatusMessage")]
    [InlineData("@{ await Html.RenderPartialAsync(\"_StatusMessage\"); }", "_StatusMessage")]
    [InlineData("    Layout = \"../Shared/_Layout.cshtml\";", "../Shared/_Layout.cshtml")]
    [InlineData("services.AddUIExtension(\"store-integrations-nav\", \"Spark/SparkNav\");", "Spark/SparkNav")]
    public void ViewNameScannerFindsEverySyntax(string line, string expected)
    {
        Assert.Equal([expected], ScanLineForViewNames(line));
    }

    /// <summary>A name assembled at render time is skipped rather than reported as missing.</summary>
    [Fact]
    public void ViewNameScannerSkipsDynamicNames()
    {
        Assert.Empty(ScanLineForViewNames("<partial name=\"@partial\" model=\"@Model.Model\" />"));
    }

    /// <param name="SearchFolder">Absolute folder the reference is resolved relative to.</param>
    private sealed record ViewNameReference(string File, int Line, string Name, string SearchFolder);

    private static readonly Regex ViewNameReferenceSyntax =
        new("""
            <partial\s+name="(?<name>[^"]+)"
            |Html\.(?:Render)?Partial(?:Async)?\s*\(\s*"(?<name>[^"]+)"
            |Layout\s*=\s*"(?<name>[^"]+)"
            |AddUIExtension\s*\(\s*"[^"]*"\s*,\s*"(?<name>[^"]+)"
            """,
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace);

    private static bool ViewFileExists(ViewNameReference reference)
    {
        // A path: relative to the referencing view, in whichever of the two Views trees the composite provider
        // would satisfy it from.
        if (reference.Name.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = Path.GetFullPath(Path.Combine(reference.SearchFolder, reference.Name));
            return File.Exists(resolved) || File.Exists(InCoreViews(resolved));
        }

        // A name: Views/Shared/{name}.cshtml, or the referencing view's own folder.
        var shared = Path.Combine(PluginViewsDirectory, "Shared", reference.Name + ".cshtml");
        var alongside = Path.Combine(reference.SearchFolder, reference.Name + ".cshtml");
        return File.Exists(shared) || File.Exists(InCoreViews(shared))
            || File.Exists(alongside) || File.Exists(InCoreViews(alongside));
    }

    /// <summary>The same virtual view path, taken from core's <c>Views</c> tree instead of the plugin's.</summary>
    private static string InCoreViews(string pluginViewPath) =>
        Path.Combine(CoreViewsDirectory, "Views", Path.GetRelativePath(PluginViewsDirectory, pluginViewPath));

    /// <summary>
    /// The set of view component names available to a Spark view at render time, as ASP.NET Core itself computes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a source scan and not a hand-written re-implementation of the discovery rules. MVC's own
    /// <see cref="ViewComponentFeatureProvider"/> decides what is a view component (public, concrete, non-generic
    /// class that either ends in <c>ViewComponent</c> or carries <c>[ViewComponent]</c> — which the abstract
    /// <c>ViewComponent</c> base class does, inheritably, so deriving from it is enough — minus anything marked
    /// <c>[NonViewComponent]</c>), and <see cref="DefaultViewComponentDescriptorProvider"/> decides what each one is
    /// called (honouring <c>[ViewComponent(Name = "…")]</c>). Driving those two types over the real assemblies means
    /// the rules cannot drift from the runtime's, because they are the runtime's.
    /// </para>
    /// <para>
    /// Reflecting over the built <c>BTCPayServer</c> assembly is also the honest model of the failure: BTCPay
    /// resolves components from the assemblies loaded in the host process, and the pinned submodule's build output is
    /// this repository's stand-in for those. Scanning the submodule's source instead would have to guess at
    /// conditional compilation, generated types and the discovery conventions above.
    /// </para>
    /// <para>
    /// Only two assemblies are considered — BTCPay's and the plugin's. A real host may have more components from
    /// other installed plugins, but a view that depends on one of those is depending on something the operator may
    /// not have installed, so the narrower set is the correct one to check against.
    /// </para>
    /// </remarks>
    private static ComponentCatalogue AvailableComponents()
    {
        var partManager = new ApplicationPartManager();
        foreach (var assembly in ComponentSourceAssemblies())
            partManager.ApplicationParts.Add(new AssemblyPart(assembly));
        partManager.FeatureProviders.Add(new ViewComponentFeatureProvider());

        var descriptors = new DefaultViewComponentDescriptorProvider(partManager).GetViewComponents();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tagNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            // ShortName is what <vc:…> and Component.InvokeAsync("…") resolve against; FullName is accepted by
            // Component.InvokeAsync too, and is how an ambiguity between two same-named components is disambiguated.
            names.Add(descriptor.ShortName);
            names.Add(descriptor.FullName);
            // The tag-helper spelling: Razor derives the element name from the short name, so this is the form a
            // reference scanned out of a <vc:…> tag will be compared against.
            var tagName = ToHtmlCase(descriptor.ShortName);
            names.Add(tagName);
            tagNames.Add(tagName);
            // Component.InvokeAsync<T>() and typeof(T) reference the CLR type, which for a component named by
            // [ViewComponent(Name = "…")] is not the short name.
            names.Add(descriptor.TypeInfo.Name);
            if (descriptor.TypeInfo.FullName is { } clrFullName)
                names.Add(clrFullName);
        }

        return new ComponentCatalogue(names, tagNames.ToList());
    }

    /// <summary>
    /// Every spelling by which a discovered component can be referenced (<see cref="Resolves"/>), plus just their
    /// <c>&lt;vc:…&gt;</c> tag names, which is the readable list to put in a failure message.
    /// </summary>
    private sealed record ComponentCatalogue(HashSet<string> Names, IReadOnlyList<string> TagNames)
    {
        public bool Resolves(string reference) => Names.Contains(reference);
    }

    /// <summary>
    /// BTCPay's assembly (built from the pinned submodule) and the plugin's own.
    /// </summary>
    /// <remarks>
    /// Anchored on types rather than assembly names so a rename or a retargeted project reference is a compile
    /// error here rather than a silently empty component set. <c>BTCPayServerServices</c> is BTCPay's composition
    /// root and is not going anywhere; deliberately not a type from <c>Components/</c>, since the whole point is
    /// that components come and go between releases.
    /// </remarks>
    private static IEnumerable<Assembly> ComponentSourceAssemblies()
    {
        yield return typeof(Hosting.BTCPayServerServices).Assembly;
        yield return typeof(SparkPlugin).Assembly;
    }

    /// <summary>A single view-component invocation found in a view.</summary>
    private sealed record ComponentReference(string File, int Line, string Syntax, string Name);

    /// <summary>
    /// <c>&lt;vc:some-name …&gt;</c>. Razor reserves the <c>vc:</c> element prefix for view components, so any
    /// element with it is an invocation — there is no false-positive to filter out.
    /// </summary>
    private static readonly Regex TagHelperInvocation =
        new(@"<vc:(?<name>[a-zA-Z0-9-]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>Component.InvokeAsync("Name")</c> and <c>Component.InvokeAsync&lt;Type&gt;()</c>, the two non-tag
    /// spellings. The generic form is matched on its type argument's final segment, which is the CLR type name
    /// <see cref="AvailableComponents"/> also records.
    /// </summary>
    private static readonly Regex HelperInvocation =
        new("""Component\.InvokeAsync\s*(?:<\s*(?<type>[A-Za-z0-9_.]+)\s*>\s*\(|\(\s*(?:typeof\s*\(\s*(?<type>[A-Za-z0-9_.]+)\s*\)|"(?<name>[^"]+)"))""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IEnumerable<ComponentReference> ScanForComponentReferences(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cshtml", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(RepositoryRoot, file);
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (var (syntax, name) in ScanLine(lines[index]))
                    yield return new ComponentReference(relative, index + 1, syntax, name);
            }
        }
    }

    /// <summary>
    /// The invocations on one line of a view, as (syntax as written, component name referenced) pairs.
    /// </summary>
    /// <remarks>
    /// Line-at-a-time is enough: a <c>&lt;vc:…&gt;</c> element may wrap over several lines, but its name is always
    /// on the line the tag opens on. Split out from the file walk so <see cref="ScannerFindsEverySyntax"/> can
    /// exercise the spellings the plugin's views do not currently use.
    /// </remarks>
    private static IEnumerable<(string Syntax, string Name)> ScanLine(string line)
    {
        foreach (Match match in TagHelperInvocation.Matches(line))
            yield return ($"<vc:{match.Groups["name"].Value}>", match.Groups["name"].Value);

        foreach (Match match in HelperInvocation.Matches(line))
        {
            // A generic or typeof argument may be namespace-qualified; the descriptor set holds both the bare type
            // name and the full name, so the reference is taken as written.
            var name = match.Groups["type"].Success ? match.Groups["type"].Value : match.Groups["name"].Value;
            yield return (match.Value.Trim(), name);
        }
    }

    /// <summary>
    /// The scanner picks up every spelling of an invocation, including the ones no view uses today.
    /// </summary>
    /// <remarks>
    /// A guard that silently matches nothing is worse than no guard, and the plugin's views only use the
    /// <c>&lt;vc:…&gt;</c> form — so without this, three of the four branches would be dead code that nobody would
    /// notice was broken until a view started using them.
    /// </remarks>
    [Theory]
    [InlineData("    <vc:title-header />", "title-header")]
    [InlineData("<vc:truncate-center text=\"@x\" classes=\"c\" />", "truncate-center")]
    [InlineData("@await Component.InvokeAsync(\"UiExtensionPoint\", new { location = \"x\" })", "UiExtensionPoint")]
    [InlineData("@await Component.InvokeAsync (  \"TitleHeader\" )", "TitleHeader")]
    [InlineData("@await Component.InvokeAsync<TitleHeader>()", "TitleHeader")]
    [InlineData("@await Component.InvokeAsync< BTCPayServer.Components.Breadcrumb.TitleHeader >()", "BTCPayServer.Components.Breadcrumb.TitleHeader")]
    [InlineData("@await Component.InvokeAsync(typeof(TitleHeader))", "TitleHeader")]
    public void ScannerFindsEverySyntax(string line, string expected)
    {
        Assert.Equal([expected], ScanLine(line).Select(reference => reference.Name));
    }

    /// <summary>
    /// Lines that merely look like an invocation are not treated as one.
    /// </summary>
    [Theory]
    [InlineData("<div class=\"vc:not-a-component\">")]
    [InlineData("@await Html.PartialAsync(\"_StatusMessage\")")]
    [InlineData("// Component.InvokeAsyncSomethingElse(\"Nope\")")]
    public void ScannerIgnoresNonInvocations(string line)
    {
        Assert.Empty(ScanLine(line));
    }

    /// <summary>
    /// A view component's short name in the casing Razor gives its <c>&lt;vc:…&gt;</c> element.
    /// </summary>
    /// <remarks>
    /// This is the one piece of the runtime's behaviour that has to be reproduced rather than called: the conversion
    /// lives in Razor's compile-time tooling, not in a shipped runtime API. The rule (from Razor's
    /// <c>ViewComponentTagHelperDescriptorFactory</c>) inserts a hyphen before an upper-case letter that starts a new
    /// word and lower-cases the result, which is why <c>TitleHeader</c> is <c>title-header</c> and the
    /// acronym-leading <c>UiExtensionPoint</c> is <c>ui-extension-point</c> rather than <c>u-i-extension-point</c>.
    /// <see cref="BTCPaysOwnViewsAllResolve"/> is the check that this reproduction is faithful.
    /// </remarks>
    private static string ToHtmlCase(string name) => HtmlCase.Replace(name, "-$1$2").ToLowerInvariant();

    private static readonly Regex HtmlCase =
        new("(?<!^)((?<=[a-zA-Z0-9])[A-Z][a-z])|((?<=[a-z])[A-Z])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
