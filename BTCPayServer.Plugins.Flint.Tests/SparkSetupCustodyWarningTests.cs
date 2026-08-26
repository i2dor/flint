using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The setup page tells a tenant whose store is not on their own server that the Spark seed is readable by the
/// operator, at the point of seed creation or import.
/// </summary>
/// <remarks>
/// <para>
/// The multi-tenant finding this pins: on a shared or public-registration BTCPay instance the store's Spark
/// seed is stored on the server, encrypted with keys that live in the same data directory, so whoever operates
/// the server can decrypt it and spend the Lightning funds — a custody transfer the old copy framed as a
/// recovery fact ("unreadable without this server's data-protection keys") rather than a confidentiality one.
/// The two are different claims, and only one of them is true.
/// </para>
/// <para>
/// The warning mirrors BTCPay core's own wallet-generation form, which shows a non-admin the same warning for
/// an on-chain seed: it is shown to everyone except a <c>ServerAdmin</c>, the one caller operating the host the
/// seed is stored on. The read of the view source is deliberate — like the nav tests, this pins the copy that
/// actually renders, not a model field that might not reach the page; render-time behaviour is covered by
/// <see cref="SparkSurfaceHarness"/>'s admin/non-admin identities.
/// </para>
/// </remarks>
public class SparkSetupCustodyWarningTests
{
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(ThisFile(), "..", ".."));

    private static string ThisFile([CallerFilePath] string path = "") => path;

    private static string SetupView => Path.Combine(
        RepositoryRoot, "BTCPayServer.Plugins.Flint", "Views", "Spark", "Setup.cshtml");

    [Fact]
    public void The_setup_page_names_the_server_operator_as_a_custodian()
    {
        var view = File.ReadAllText(SetupView);

        // The plain-language disclosure the finding asked for: whoever operates the server can decrypt the
        // stored seed and spend the funds, and a tenant who does not control the server should not put a seed
        // here. Both the shared framing and the actionable instruction are pinned.
        Assert.Contains("whoever operates this server can decrypt it", view);
        Assert.Contains("can decrypt it and spend this wallet's funds", view);
        Assert.Contains("do not put a Spark seed here", view);
    }

    [Fact]
    public void The_custody_warning_is_only_for_non_admin_store_managers()
    {
        var view = File.ReadAllText(SetupView);

        // The same conditional core's own wallet-generation form uses. A server admin operating their own
        // instance is the self-hosted case and needs no reminder that they can read their own seed; the
        // recipient of the warning is precisely the store manager who is not the operator.
        Assert.Contains("!User.IsInRole(Roles.ServerAdmin)", view);
    }

    [Fact]
    public void The_setup_page_does_not_claim_the_stored_seed_is_unreadable_on_the_server()
    {
        var view = File.ReadAllText(SetupView);

        // The old recovery framing ("unreadable without this server's data-protection keys") implied the
        // operator could not read the copy, which is false on a host the operator controls. The sentence is
        // gone rather than merely supplemented, because two conflicting sentences side by side are how the
        // weaker one wins with a skimming tenant.
        Assert.DoesNotContain("unreadable without this server's data-protection keys", view);
    }
}
