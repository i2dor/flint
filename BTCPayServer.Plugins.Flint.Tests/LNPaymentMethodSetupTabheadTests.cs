using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Data;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using BTCPayServer.Security;
using Ganss.Xss;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.ViewFeatures.Buffers;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The setup-tabhead extension point renders a store's connection string only for the store the request
/// was authorised for — never for the one the bound model names.
/// </summary>
/// <remarks>
/// <para>
/// <c>LightningNodeViewModel.StoreId</c> is an ordinary form-bound settable property, and core's POST of
/// <c>SetupLightningNode</c> re-renders the page through six paths without ever reassigning it from the
/// route or the authorised store. So the model reaching the plugin's partial on a POST can name any store
/// on the server, and before the authorised-store guard the partial resolved the connection string — a
/// bearer spend credential — straight off <c>Model.StoreId</c> and embedded it in page JS.
/// </para>
/// <para>
/// These are the suite's first tests that <em>render</em> a view, and they render the real thing: the
/// compiled <c>.cshtml</c> is discovered as the <see cref="RazorCompiledItemMetadataAttribute"/> the plugin
/// assembly carries for it, executed against a real <see cref="SparkService"/> over
/// <see cref="SparkServiceHarness"/> fakes, with the authorisation read through BTCPay's own
/// <c>SetStoreData</c>/<c>GetStoreDataOrNull</c> channel. Assertions are on the rendered output only. A
/// regression that dropped the guard would call <c>GetConnectionString</c> for the wrong store and put
/// that store's key in the output, which is exactly what the mismatch case asserts against; the match
/// case holds the guard honest the other way, so the fix cannot pass by simply never rendering anything.
/// </para>
/// <para>
/// The stand-ins are plumbing around the view rather than in it: the <c>@inject</c>ed
/// <see cref="Safe"/> is built over a single-method <see cref="IJsonHelper"/> and an
/// <see cref="IHtmlHelper"/> dispatch proxy that do only what the production helpers do for this one
/// call — serialise with <see cref="JsonSerializer"/>, wrap the result verbatim as HTML — and throw on
/// anything else, so no other helper surface can be quietly relied on. The one tag helper the rendered
/// branch does match, BTCPay's CSP nonce helper on the inline <c>&lt;script&gt;</c>, runs for real over
/// the production factory; the link- and permission-helpers live only in branches no case reaches, so
/// no link-generation services need modelling.
/// </para>
/// </remarks>
public class LNPaymentMethodSetupTabheadTests
{
    /// <summary>The store whose connection string exists and would be rendered if consulted.</summary>
    private const string WalletStore = "store-with-wallet";

    /// <summary>A different, wallet-less store, used as the authorised one in the mismatch case.</summary>
    private const string AuthorisedStore = "store-authorised";

    /// <summary>A payment key shaped like a generated one, distinctive so its presence in output is unmistakable.</summary>
    private const string PaymentKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string TabheadItemPath = "/Views/Shared/Spark/LNPaymentMethodSetupTabhead.cshtml";

    [Fact]
    public async Task A_model_naming_another_store_than_the_authorised_one_renders_no_connection_string()
    {
        using var harness = SparkServiceHarness.Create();
        harness.SeedStore(WalletStore, SparkServiceHarness.MnemonicFor(1), PaymentKey);
        StartService(harness);

        // The attack: authorisation succeeded for AuthorisedStore, the form-bound model names WalletStore.
        var html = await RenderTabheadAsync(harness.Service, authorisedStoreId: AuthorisedStore, WalletStore);

        Assert.DoesNotContain("type=flint", html);
        Assert.DoesNotContain(PaymentKey, html);
        // Fail-closed means the partial renders nothing at all — not even the pill it would otherwise emit.
        Assert.Equal(string.Empty, html.Trim());
    }

    [Fact]
    public async Task A_request_carrying_no_authorised_store_renders_nothing()
    {
        using var harness = SparkServiceHarness.Create();
        harness.SeedStore(WalletStore, SparkServiceHarness.MnemonicFor(2), PaymentKey);
        StartService(harness);

        // Nothing was authorised onto the request: the guard must fail closed rather than trust the form.
        var html = await RenderTabheadAsync(harness.Service, authorisedStoreId: null, WalletStore);

        Assert.DoesNotContain("type=flint", html);
        Assert.DoesNotContain(PaymentKey, html);
        Assert.Equal(string.Empty, html.Trim());
    }

    [Fact]
    public async Task The_authorised_store_still_gets_its_connection_string_rendered()
    {
        using var harness = SparkServiceHarness.Create();
        harness.SeedStore(WalletStore, SparkServiceHarness.MnemonicFor(3), PaymentKey);
        StartService(harness);

        // The legitimate flow — authorised store and model agree — must keep working unchanged.
        var html = await RenderTabheadAsync(harness.Service, authorisedStoreId: WalletStore, WalletStore);

        Assert.Contains($"type=flint;store-id={WalletStore};key={PaymentKey}", html);
        // And it is the real pill: the script that fills core's connection-string field is present.
        Assert.Contains("<script", html);
    }

    /// <summary>
    /// Runs <c>StartAsync</c> so the service's startup gate opens, with a timeout so a regression that hangs
    /// startup fails the test instead of the test run.
    /// </summary>
    /// <remarks>
    /// Needed even by the suppressed cases: if the guard were gone, the partial would reach
    /// <c>GetConnectionString</c>, which awaits the gate — the test must distinguish "refused" from "still
    /// waiting on a service nobody started".
    /// </remarks>
    private static void StartService(SparkServiceHarness harness)
    {
        if (!harness.Service.StartAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(30)))
        {
            Assert.Fail(
                "SparkService.StartAsync did not complete within 30s over the harness's fake SDK; the render "
                + "under test must be able to await the plugin's startup gate.");
        }
    }

    /// <summary>
    /// Executes the plugin's compiled <c>LNPaymentMethodSetupTabhead</c> page over the given authorisation
    /// and model, and returns everything it wrote.
    /// </summary>
    private static async Task<string> RenderTabheadAsync(
        SparkService service,
        string? authorisedStoreId,
        string modelStoreId)
    {
        // .NET 10's Razor compiler tags each compiled page class with a key/value metadata attribute;
        // the "Identifier" key carries the .cshtml path it was compiled from.
        var pageType = typeof(SparkPlugin).Assembly
            .GetTypes()
            .Single(t => t.GetCustomAttributes<RazorCompiledItemMetadataAttribute>()
                .Any(m => m.Key == "Identifier" && m.Value == TabheadItemPath));
        var page = (RazorPage<LightningNodeViewModel>)Activator.CreateInstance(pageType)!;

        var viewData = new ViewDataDictionary<LightningNodeViewModel>(
            new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = new LightningNodeViewModel { CryptoCode = "BTC", StoreId = modelStoreId },
        };

        var httpContext = new DefaultHttpContext();
        if (authorisedStoreId is not null)
        {
            // The same channel SetContextFilter writes on a request core authorised for a store.
            httpContext.SetStoreData(new StoreData { Id = authorisedStoreId });
        }

        // The rendered branch contains an inline <script>, and the plugin's @addTagHelper lines bind
        // BTCPay's CSPInlineScriptTagHelper to every script element — the only tag helper any rendered
        // branch matches, since the MVC helpers all require asp-* attributes this markup does not carry
        // and the permission/link helpers only match the anchor branch, which no case reaches. The
        // factory and activator below are the production ones (internals, instantiated the way the DI
        // registration does); the services added are the CSP policy bag the script helper stamps its
        // nonce into and the production output buffer the tag-helper pipeline writes through.
        httpContext.RequestServices = new ServiceCollection()
            .AddSingleton<ITagHelperFactory>(CreateDefaultTagHelperFactory())
            .AddSingleton<IViewBufferScope>(CreateViewBufferScope())
            .AddSingleton(new ContentSecurityPolicies())
            .BuildServiceProvider();

        var writer = new StringWriter(CultureInfo.InvariantCulture);
        page.ViewContext = new ViewContext
        {
            HttpContext = httpContext,
            RouteData = new RouteData(),
            ViewData = viewData,
            Writer = writer,
        };
        page.ViewData = viewData;
        // RazorPage<TModel>.Model is read off ViewData.Model; setting the view data is what feeds the view.

        SetInjectProperty(page, nameof(SparkService), service);
        SetInjectProperty(page, "Safe", new Safe(
            DispatchProxy.Create<IHtmlHelper, RawOnlyHtmlHelper>(),
            new PlainJsonHelper(),
            new HtmlSanitizer()));

        // RazorPageBase.HtmlEncoder is a [RazorInject] the view engine fills from DI (HtmlEncoder.Default
        // is what MVC registers); the tag-helper output pipeline encodes through it.
        SetInjectProperty(page, "HtmlEncoder", System.Text.Encodings.Web.HtmlEncoder.Default);

        await page.ExecuteAsync();
        return writer.ToString();
    }

    /// <summary>Sets one of the compiled page's <c>@inject</c> properties, failing loudly if it is not there.</summary>
    private static void SetInjectProperty(object page, string name, object value)
    {
        var property = page.GetType().GetProperty(
            name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"The compiled page {page.GetType().Name} has no '{name}' inject property to populate.");

        if (!property.CanWrite)
        {
            throw new InvalidOperationException(
                $"The compiled page's '{name}' inject property is not writable.");
        }

        property.SetValue(page, value);
    }

    /// <summary>
    /// The production <see cref="ITagHelperFactory"/> over the production activator, both internal and
    /// therefore reflection-instantiated rather than reimplemented — the point is that the partial gets
    /// exactly the tag helpers a real request would build for it.
    /// </summary>
    private static ITagHelperFactory CreateDefaultTagHelperFactory()
    {
        var assembly = typeof(ITagHelperFactory).Assembly;
        var activatorType = assembly.GetType(
            "Microsoft.AspNetCore.Mvc.Razor.Infrastructure.DefaultTagHelperActivator", throwOnError: true)!;
        var factoryType = assembly.GetType(
            "Microsoft.AspNetCore.Mvc.Razor.DefaultTagHelperFactory", throwOnError: true)!;
        return (ITagHelperFactory)Activator.CreateInstance(factoryType, Activator.CreateInstance(activatorType))!;
    }

    /// <summary>
    /// The production <see cref="IViewBufferScope"/> over the shared array pools — the instance MVC's own
    /// DI registers — needed because the tag-helper pipeline buffers the script element's content.
    /// </summary>
    private static IViewBufferScope CreateViewBufferScope()
    {
        var type = typeof(IViewBufferScope).Assembly.GetType(
            "Microsoft.AspNetCore.Mvc.ViewFeatures.Buffers.MemoryPoolViewBufferScope", throwOnError: true)!;
        return (IViewBufferScope)Activator.CreateInstance(
            type, ArrayPool<ViewBufferValue>.Shared, ArrayPool<char>.Shared)!;
    }

    /// <summary>Serialises JSON exactly as the page needs; anything else about the helper is unused by this view.</summary>
    private sealed class PlainJsonHelper : IJsonHelper
    {
        public IHtmlContent Serialize(object? model) =>
            new HtmlString(JsonSerializer.Serialize(model));
    }
}

/// <summary>
/// An <see cref="IHtmlHelper"/> that does only what the production helper does for <c>Raw</c> — wrap the
/// value verbatim as HTML — because that is all <c>Safe.Json</c> calls. Every other member throws, so a
/// view that starts reaching for more cannot pass on a helper it was never given. Public because that is
/// what <see cref="DispatchProxy"/> requires of the type it derives its proxy from.
/// </summary>
public class RawOnlyHtmlHelper : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is { Name: "Raw" } && targetMethod.GetParameters().Length == 1)
        {
            return new HtmlString(System.Convert.ToString(args![0], CultureInfo.InvariantCulture) ?? string.Empty);
        }

        throw new NotSupportedException(
            $"The rendered setup partial must not call IHtmlHelper.{targetMethod?.Name}; "
            + "extend the test double deliberately if it ever does.");
    }
}
