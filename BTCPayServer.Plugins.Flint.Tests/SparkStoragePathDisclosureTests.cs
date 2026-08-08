using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The host's filesystem layout is not part of a store's status.
/// </summary>
/// <remarks>
/// <para>
/// Both status surfaces are reachable with <c>CanViewStoreSettings</c>, which every store role above the
/// lowest holds, and both used to render the absolute path of the store's SDK storage directory. That says
/// where the BTCPay data directory lives and, by inference, how the server is deployed — a fact about the
/// host, on a page that is otherwise entirely about the store, shown to people who are store managers and
/// not server operators.
/// </para>
/// <para>
/// Both surfaces are checked, and so is the negative: an admin still gets the path, because a redaction that
/// removed it from everybody would be a different change and would take the operator's diagnostic with it.
/// </para>
/// </remarks>
public class SparkStoragePathDisclosureTests
{
    private const string Store = SparkSurfaceHarness.AttackerStore;

    [Fact]
    public async Task The_status_page_does_not_show_a_store_manager_the_storage_path()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, serverAdmin: false);

        var view = Assert.IsType<ViewResult>(await h.Mvc.Status(Store, CancellationToken.None));
        var model = Assert.IsType<SparkStatusViewModel>(view.Model);

        Assert.Null(model.StorageDirectory);
    }

    [Fact]
    public async Task The_status_page_shows_a_server_admin_the_storage_path()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, serverAdmin: true);

        var view = Assert.IsType<ViewResult>(await h.Mvc.Status(Store, CancellationToken.None));
        var model = Assert.IsType<SparkStatusViewModel>(view.Model);

        Assert.Equal(h.Runtime.GetStorageDirectory(Store), model.StorageDirectory);
    }

    /// <summary>
    /// The remove page projects the same record, so it has to be checked separately.
    /// </summary>
    /// <remarks>
    /// It is a second view over one view model, and it names the path in prose rather than in a table row —
    /// exactly the shape that gets missed when a redaction is applied to "the status page".
    /// </remarks>
    [Fact]
    public async Task The_remove_page_does_not_show_a_store_manager_the_storage_path()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, serverAdmin: false);

        var view = Assert.IsType<ViewResult>(await h.Mvc.Remove(Store, CancellationToken.None));
        var model = Assert.IsType<SparkStatusViewModel>(view.Model);

        Assert.Null(model.StorageDirectory);
    }

    [Fact]
    public async Task The_remove_page_shows_a_server_admin_the_storage_path()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, serverAdmin: true);

        var view = Assert.IsType<ViewResult>(await h.Mvc.Remove(Store, CancellationToken.None));
        var model = Assert.IsType<SparkStatusViewModel>(view.Model);

        Assert.Equal(h.Runtime.GetStorageDirectory(Store), model.StorageDirectory);
    }

    [Fact]
    public async Task The_status_endpoint_does_not_return_a_store_manager_the_storage_path()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, serverAdmin: false);

        var data = ApiStatus(await h.Api.GetStatus(Store, CancellationToken.None));

        Assert.Null(data.StorageDirectory);

        // Belt and braces: the path must not survive anywhere else in the serialised body either.
        Assert.DoesNotContain(
            h.Runtime.GetStorageDirectory(Store),
            Newtonsoft.Json.JsonConvert.SerializeObject(data, ApiJson.Settings));
    }

    [Fact]
    public async Task The_status_endpoint_returns_a_server_admin_the_storage_path()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, serverAdmin: true);

        var data = ApiStatus(await h.Api.GetStatus(Store, CancellationToken.None));

        Assert.Equal(h.Runtime.GetStorageDirectory(Store), data.StorageDirectory);
    }

    private static SparkStatusData ApiStatus(IActionResult result) =>
        Assert.IsType<SparkStatusData>(Assert.IsType<OkObjectResult>(result).Value);
}
