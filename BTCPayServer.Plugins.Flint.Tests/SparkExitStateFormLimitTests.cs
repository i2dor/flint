using System.Net;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Filters;
using BTCPayServer.Plugins.Flint.Controllers;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// A multi-megabyte exit-state paste reaches the save action through a real Kestrel and MVC pipeline, with
/// BTCPay's own antiforgery filter in front of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What failed, and why only a pipeline test can see it.</b> ASP.NET caps a form value at 4 MiB by
/// default, and the SDK documents real exports as several megabytes. The antiforgery check reads the whole
/// form <em>before</em> the action runs, so a larger paste died there as a bare 400 — no action, no message,
/// nothing a unit test calling the action method could ever observe. The fix is attributes on the action,
/// and attributes only act inside the pipeline: this runs the real controller behind real Kestrel, real
/// routing, authentication and authorisation, MVC's own filters, BTCPay's global
/// <see cref="UIControllerAntiforgeryTokenAttribute"/> and the controller's own
/// <see cref="AutoValidateAntiforgeryTokenAttribute"/>, with a real token pair.
/// </para>
/// <para>
/// Six megabytes of JSON-shaped text, posted both ways the action can be reached: multipart, as the page
/// posts it now, and url-encoded — how the page posted it before, and the case that failed: every brace,
/// quote and comma triples, and the 4 MiB value limit is only applied to url-encoded forms. With the
/// action's limits removed, the url-encoded test fails with exactly the 400 operators saw. Only the
/// controller's collaborators are the surface harness's fakes.
/// </para>
/// </remarks>
public class SparkExitStateFormLimitTests
{
    private const string Store = SparkSurfaceHarness.AttackerStore;

    /// <summary>Past the 4 MiB default by half again, and shaped like the SDK's JSON so url-encoding inflates it.</summary>
    private static readonly string SixMegabyteBackup = BuildBackup(6 * 1024 * 1024);

    [Fact(Timeout = 120_000)]
    public async Task A_six_megabyte_multipart_paste_reaches_the_action_intact()
    {
        await using var site = await Site.StartAsync();

        using var form = new MultipartFormDataContent
        {
            { new StringContent(await site.TokenAsync()), "__RequestVerificationToken" },
            { new StringContent(SixMegabyteBackup, Encoding.UTF8), nameof(Models.SparkAdvancedViewModel.ExitStateBackup) }
        };

        var response = await site.Client.PostAsync($"/plugins/{Store}/spark/advanced/exit-state", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(SixMegabyteBackup, site.Exit.LastExitState);
    }

    [Fact(Timeout = 120_000)]
    public async Task A_six_megabyte_url_encoded_paste_reaches_the_action_intact()
    {
        await using var site = await Site.StartAsync();

        using var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", await site.TokenAsync()),
            new KeyValuePair<string, string>(nameof(Models.SparkAdvancedViewModel.ExitStateBackup), SixMegabyteBackup)
        ]);

        var response = await site.Client.PostAsync($"/plugins/{Store}/spark/advanced/exit-state", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(SixMegabyteBackup, site.Exit.LastExitState);
    }

    [Fact(Timeout = 120_000)]
    public async Task The_same_backup_uploaded_as_a_file_reaches_the_action_intact()
    {
        await using var site = await Site.StartAsync();

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(SixMegabyteBackup));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(await site.TokenAsync()), "__RequestVerificationToken" },
            { file, nameof(Models.SparkAdvancedViewModel.ExitStateBackupFile), "exit-state-backup.txt" }
        };

        var response = await site.Client.PostAsync($"/plugins/{Store}/spark/advanced/exit-state", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(SixMegabyteBackup, site.Exit.LastExitState);
    }

    [Fact(Timeout = 120_000)]
    public async Task A_post_without_an_antiforgery_token_is_still_refused()
    {
        // The limits widen what the form read accepts; they must not have moved the check out of the way.
        await using var site = await Site.StartAsync();

        using var form = new MultipartFormDataContent
        {
            { new StringContent("small"), nameof(Models.SparkAdvancedViewModel.ExitStateBackup) }
        };

        var response = await site.Client.PostAsync($"/plugins/{Store}/spark/advanced/exit-state", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(site.Exit.LastExitState);
    }

    private static string BuildBackup(int length)
    {
        const string chunk = "{\"leaf\":\"0a1b2c3d\",\"tx\":\"02000000\",\"vout\":1},";
        var builder = new StringBuilder(length);
        while (builder.Length < length)
            builder.Append(chunk);
        builder.Length = length;
        return builder.ToString();
    }

    /// <summary>
    /// A real web host on a loopback port, serving the real <see cref="SparkController"/> built by the surface
    /// harness, with BTCPay's antiforgery filter registered globally the way BTCPay registers it.
    /// </summary>
    private sealed class Site : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Site(WebApplication app, HttpClient client, RecordingExitService exit)
        {
            _app = app;
            Client = client;
            Exit = exit;
        }

        public HttpClient Client { get; }

        public RecordingExitService Exit { get; }

        public static async Task<Site> StartAsync()
        {
            var exit = new RecordingExitService();
            var harness = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");

            builder.Services
                .AddControllersWithViews(options => options.Filters.Add(new UIControllerAntiforgeryTokenAttribute()))
                .ConfigureApplicationPartManager(parts =>
                {
                    // The plugin's controllers only: the test assembly's references would otherwise bring in
                    // every BTCPay controller, whose dependencies this host does not have.
                    parts.ApplicationParts.Clear();
                    parts.ApplicationParts.Add(new AssemblyPart(typeof(SparkController).Assembly));
                });
            builder.Services.AddSingleton<IControllerActivator>(new HarnessActivator(harness.Mvc));
            builder.Services.AddAntiforgery();
            builder.Services
                .AddAuthentication(AuthenticationSchemes.Cookie)
                .AddScheme<AuthenticationSchemeOptions, SignedInOperator>(AuthenticationSchemes.Cookie, _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy(Policies.CanViewStoreSettings, policy => policy.RequireAuthenticatedUser());
                options.AddPolicy(Policies.CanModifyStoreSettings, policy => policy.RequireAuthenticatedUser());
            });

            var app = builder.Build();

            // What BTCPay's store authorisation leaves on the request: the store this request may touch.
            app.Use((context, next) =>
            {
                context.SetStoreData(new StoreData { Id = Store });
                return next(context);
            });
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet("/token", (IAntiforgery antiforgery, HttpContext context) =>
                antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty);
            app.MapControllers();

            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var client = new HttpClient(new HttpClientHandler { UseCookies = true, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(60)
            };

            return new Site(app, client, exit);
        }

        /// <summary>A request token, with its cookie now in the client's jar.</summary>
        public async Task<string> TokenAsync() => await Client.GetStringAsync("/token");

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>Hands MVC the controller the harness built, so its collaborators are the harness's fakes.</summary>
    private sealed class HarnessActivator : IControllerActivator
    {
        private readonly SparkController _controller;

        public HarnessActivator(SparkController controller) => _controller = controller;

        public object Create(ControllerContext context) => _controller;

        public void Release(ControllerContext context, object controller)
        {
        }
    }

    /// <summary>Every request is the same signed-in operator — authorisation is not what is under test.</summary>
    private sealed class SignedInOperator : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public SignedInOperator(
            IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "operator"), new Claim(ClaimTypes.Name, "operator")],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    /// <summary>
    /// Records the one value under test and answers every save with success; everything else is the page
    /// tests' stub.
    /// </summary>
    private sealed class RecordingExitService : Services.ISparkUnilateralExitService
    {
        private readonly SparkExitPageTests.StubExitService _inner = new();

        public string? LastExitState { get; private set; }

        public Task<Services.UnilateralExitOpResult> SetExitStateBackupAsync(
            string storeId, string? exitState, CancellationToken cancellationToken = default)
        {
            LastExitState = exitState;
            return Task.FromResult(new Services.UnilateralExitOpResult(true, null, null));
        }

        public Task<Services.UnilateralExitPageData> ReadAsync(string storeId, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(storeId, cancellationToken);

        public Task<Services.UnilateralExitOpResult> AcknowledgeDisclosureAsync(
            string storeId, CancellationToken cancellationToken = default) =>
            _inner.AcknowledgeDisclosureAsync(storeId, cancellationToken);

        public Task<Services.UnilateralExitOpResult> SetExplorerUrlAsync(
            string storeId, string? esploraApiUrl, CancellationToken cancellationToken = default) =>
            _inner.SetExplorerUrlAsync(storeId, esploraApiUrl, cancellationToken);

        public Task<Services.UnilateralExitOpResult> CheckAsync(
            string storeId, string recordId, CancellationToken cancellationToken = default) =>
            _inner.CheckAsync(storeId, recordId, cancellationToken);

        public Task<Services.UnilateralExitOpResult> QuoteAsync(
            string storeId, long feeRateSatPerVbyte, string destinationAddress,
            CancellationToken cancellationToken = default) =>
            _inner.QuoteAsync(storeId, feeRateSatPerVbyte, destinationAddress, cancellationToken);

        public Task<Services.UnilateralExitOpResult> BuildAsync(
            string storeId, string recordId, long? feeRateSatPerVbyte, CancellationToken cancellationToken = default) =>
            _inner.BuildAsync(storeId, recordId, feeRateSatPerVbyte, cancellationToken);

        public Task<Services.UnilateralExitOpResult> MarkCompletedAsync(
            string storeId, string recordId, bool confirmedWithoutVerdict, CancellationToken cancellationToken = default) =>
            _inner.MarkCompletedAsync(storeId, recordId, confirmedWithoutVerdict, cancellationToken);

        public Task<Services.UnilateralExitOpResult> AbandonAsync(
            string storeId, string recordId, CancellationToken cancellationToken = default) =>
            _inner.AbandonAsync(storeId, recordId, cancellationToken);
    }
}
