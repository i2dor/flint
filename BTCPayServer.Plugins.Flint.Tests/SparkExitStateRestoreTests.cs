using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The exit-state backups a wallet has not been shown to hold — pasted ones, and stored ones whose import
/// failed — and when the stored automatic backup is imported at all. The real service, the real file store
/// and a fake SDK, as <see cref="SparkExitStateAutoBackupTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// The property every test here protects: <b>a backup that has not been imported is never destroyed by an
/// automatic pass or an export.</b> Before the import queue there was one file, the next due pass — within
/// two minutes of a receive, and at once after a restart — replaced it with the wallet's own export, and for
/// a wallet restored from its seed while the operators were down that export was of almost nothing.
/// </para>
/// <para>
/// And the other half, which is the reason not to import on every connect: the SDK documents that importing
/// an out-of-date export makes leaves spent since spendable again until the next refresh, so the wallet's
/// own last export is imported back only when its storage was empty.
/// </para>
/// </remarks>
public class SparkExitStateRestoreTests
{
    private const string StoreId = "store-restore";

    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------------------------
    // A pasted backup
    // ------------------------------------------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task A_pasted_backup_is_imported_into_the_running_wallet_at_once_and_leaves_the_queue()
    {
        using var h = await StartedAsync(new StubTimeProvider(Base));
        var wallet = h.Sdk.Clients[StoreId];
        wallet.ExitStateImportResult = new SparkExitStateImport(3, 0, 0, 1);

        await h.ExitStateBackups.AddPendingAsync(StoreId, "pasted-backup", Ct);
        var report = await h.Service.ImportPendingExitStateAsync(StoreId, Ct);

        // Imported now — not left for a restart the next pass would beat to the file.
        Assert.Equal(ExitStateImportOutcome.Imported, report.Outcome);
        Assert.Equal(3u, report.RestoredLeaves);
        Assert.Contains("pasted-backup", wallet.ExitImportCalls);
        Assert.Empty(await h.ExitStateBackups.ListPendingAsync(StoreId, Ct));

        // And the wallet changed, so a fresh automatic backup is owed.
        Assert.NotNull(h.BackupScheduler.PendingSince(StoreId));
        Assert.DoesNotContain("pasted-backup", h.Log.AllText);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_pasted_backup_that_fails_to_import_survives_every_automatic_pass()
    {
        var clock = new StubTimeProvider(Base);
        using var h = await StartedAsync(clock);
        var wallet = h.Sdk.Clients[StoreId];
        wallet.FailImportWith = new InvalidOperationException("refused");

        await h.ExitStateBackups.AddPendingAsync(StoreId, "pasted-backup", Ct);
        var report = await h.Service.ImportPendingExitStateAsync(StoreId, Ct);
        Assert.Equal(ExitStateImportOutcome.Failed, report.Outcome);

        // The pass that used to replace the paste with the wallet's own export: it writes the automatic
        // slot, and the queue is not touched — on this pass or on the hourly one after it.
        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        clock.Advance(TimeSpan.FromHours(1));
        h.Sdk.Clients[StoreId].ExitStateToExport = "export-after-an-hour";
        await h.Service.TakeDueExitStateBackupsAsync(Ct);

        Assert.Equal("export-after-an-hour", await h.ExitStateBackups.ReadAsync(StoreId, Ct));
        var queued = Assert.Single(await h.ExitStateBackups.ListPendingAsync(StoreId, Ct));
        Assert.Equal("pasted-backup", await h.ExitStateBackups.ReadPendingAsync(StoreId, queued.Id, Ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task The_page_s_export_never_touches_a_backup_waiting_to_be_imported()
    {
        using var h = await StartedAsync(new StubTimeProvider(Base));
        h.Sdk.Clients[StoreId].FailImportWith = new InvalidOperationException("refused");
        await h.ExitStateBackups.AddPendingAsync(StoreId, "pasted-backup", Ct);
        await h.Service.ImportPendingExitStateAsync(StoreId, Ct);

        var export = await h.Service.ExportExitStateAsync(StoreId, Ct);

        Assert.True(export.Stored);
        Assert.Equal("exit-state-blob", export.ExitState);
        Assert.Single(await h.ExitStateBackups.ListPendingAsync(StoreId, Ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task A_backup_queued_while_the_wallet_was_down_is_imported_at_its_next_connect()
    {
        var first = await StartedAsync(new StubTimeProvider(Base));
        SparkServiceHarness? h = null;
        try
        {
            await first.ExitStateBackups.AddPendingAsync(StoreId, "pasted-while-down", Ct);

            h = first.Restart();
            await h.Service.StartAsync(Ct);
            await h.Service.WhenExitStateRestoredAsync(StoreId).WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.Contains("pasted-while-down", h.Sdk.Clients[StoreId].ExitImportCalls);
            Assert.Empty(await h.ExitStateBackups.ListPendingAsync(StoreId, Ct));
        }
        finally
        {
            h?.Dispose();
            first.Dispose();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task A_backup_whose_every_leaf_is_another_wallet_s_is_kept_aside_rather_than_deleted()
    {
        using var h = await StartedAsync(new StubTimeProvider(Base));
        h.Sdk.Clients[StoreId].ExitStateImportResult = new SparkExitStateImport(0, 12, 0, 0);

        await h.ExitStateBackups.AddPendingAsync(StoreId, "someone-else-s-backup", Ct);
        var report = await h.Service.ImportPendingExitStateAsync(StoreId, Ct);

        Assert.Equal(ExitStateImportOutcome.Imported, report.Outcome);
        Assert.Equal(12u, report.ForeignLeaves);
        Assert.Empty(await h.ExitStateBackups.ListPendingAsync(StoreId, Ct));
        Assert.Single(Directory.GetFiles(BackupDirectory(h), StoreId + ".foreign-*.txt"));
    }

    // ------------------------------------------------------------------------------------------------
    // The startup import and the automatic pass
    // ------------------------------------------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task The_automatic_pass_waits_for_the_wallet_s_startup_import()
    {
        using var h = SparkServiceHarness.Create(timeProvider: new StubTimeProvider(Base));
        h.SeedStore(StoreId, SparkServiceHarness.MnemonicFor(1));
        await h.ExitStateBackups.WriteAsync(StoreId, "stored-before-the-restart", null, Ct);

        // An import that does not return until the test says so: the restore is still working with the
        // stored file, and a pass that ran now would replace it underneath.
        var importing = new TaskCompletionSource<SparkExitStateImport>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sdk.OnConnect = (_, client) => client.ImportOverride = _ => importing.Task;
        await h.Service.StartAsync(Ct);
        await WaitFor(() => h.Sdk.Clients[StoreId].ExitImportCalls.Count > 0, "the startup import to begin");

        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        Assert.Empty(h.Sdk.Clients[StoreId].ExitExportCalls);
        Assert.Equal("stored-before-the-restart", await h.ExitStateBackups.ReadAsync(StoreId, Ct));

        importing.SetResult(new SparkExitStateImport(1, 0, 0, 0));
        await h.Service.WhenExitStateRestoredAsync(StoreId).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        Assert.Single(h.Sdk.Clients[StoreId].ExitExportCalls);
    }

    [Fact(Timeout = 120_000)]
    public async Task A_stored_backup_the_connect_could_not_import_is_queued_before_the_pass_replaces_it()
    {
        var first = await StartedAsync(new StubTimeProvider(Base));
        SparkServiceHarness? h = null;
        try
        {
            // The case the backup exists for: the stored copy is the only exit data there is (the wallet's
            // storage is gone, so nothing says it holds it), and this SDK refuses the import.
            await first.ExitStateBackups.WriteAsync(StoreId, "the-only-copy", "02aafff7", Ct);

            h = first.Restart((_, client) => client.FailImportWith = new InvalidOperationException("refused"));
            await h.Service.StartAsync(Ct);
            await h.Service.WhenExitStateRestoredAsync(StoreId).WaitAsync(TimeSpan.FromSeconds(10), Ct);

            // Every store is due at once after a restart. The pass writes the wallet's own export — of almost
            // nothing, for a wallet in this state — and the refused copy is in the queue, not overwritten.
            h.Sdk.Clients[StoreId].ExitStateToExport = "an-export-of-almost-nothing";
            await h.Service.TakeDueExitStateBackupsAsync(Ct);

            Assert.Equal("an-export-of-almost-nothing", await h.ExitStateBackups.ReadAsync(StoreId, Ct));
            var queued = Assert.Single(await h.ExitStateBackups.ListPendingAsync(StoreId, Ct));
            Assert.Equal("the-only-copy", await h.ExitStateBackups.ReadPendingAsync(StoreId, queued.Id, Ct));
            Assert.DoesNotContain("the-only-copy", h.Log.AllText);
        }
        finally
        {
            h?.Dispose();
            first.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // When the automatic backup is imported at all
    // ------------------------------------------------------------------------------------------------

    [Fact(Timeout = 120_000)]
    public async Task The_wallet_s_own_backup_is_not_imported_back_while_its_storage_is_intact()
    {
        var first = await StartedAsync(new StubTimeProvider(Base));
        SparkServiceHarness? h = null;
        try
        {
            await first.Service.TakeDueExitStateBackupsAsync(Ct);
            Assert.Equal("exit-state-blob", await first.ExitStateBackups.ReadAsync(StoreId, Ct));

            // The SDK's own storage, as a real connect leaves it.
            CreateSdkStorage(first);

            h = first.Restart();
            await h.Service.StartAsync(Ct);
            await h.Service.WhenExitStateRestoredAsync(StoreId).WaitAsync(TimeSpan.FromSeconds(10), Ct);

            // Its own export is older than its storage: the only thing importing it could add is leaves
            // spent since, made spendable again until the next refresh — on every connect, which includes
            // every settings save.
            Assert.Empty(h.Sdk.Clients[StoreId].ExitImportCalls);
        }
        finally
        {
            h?.Dispose();
            first.Dispose();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task The_wallet_s_own_backup_is_imported_when_its_storage_was_lost()
    {
        var first = await StartedAsync(new StubTimeProvider(Base));
        SparkServiceHarness? h = null;
        try
        {
            await first.Service.TakeDueExitStateBackupsAsync(Ct);

            // No SDK storage at the restart: deleted, lost, or a server restored without it. The backup is
            // now the only copy of the exit data, which is the case it exists for.
            h = first.Restart();
            await h.Service.StartAsync(Ct);
            await h.Service.WhenExitStateRestoredAsync(StoreId).WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.Equal(["exit-state-blob"], h.Sdk.Clients[StoreId].ExitImportCalls);
        }
        finally
        {
            h?.Dispose();
            first.Dispose();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task A_backup_no_stamp_describes_is_imported_once_and_then_recognised()
    {
        var first = await StartedAsync(new StubTimeProvider(Base));
        SparkServiceHarness? second = null;
        SparkServiceHarness? third = null;
        try
        {
            // What an earlier build left: a backup with no stamp beside it, next to intact SDK storage.
            await WriteUnstampedBackupAsync(first, "written-by-an-earlier-build");
            CreateSdkStorage(first);

            second = first.Restart();
            await second.Service.StartAsync(Ct);
            await second.Service.WhenExitStateRestoredAsync(StoreId).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(["written-by-an-earlier-build"], second.Sdk.Clients[StoreId].ExitImportCalls);

            // The import stamped it, so the next connect knows the wallet holds it.
            third = second.Restart();
            await third.Service.StartAsync(Ct);
            await third.Service.WhenExitStateRestoredAsync(StoreId).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Empty(third.Sdk.Clients[StoreId].ExitImportCalls);
        }
        finally
        {
            third?.Dispose();
            second?.Dispose();
            first.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // The export deadline
    // ------------------------------------------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task A_hung_export_is_abandoned_and_not_started_again_while_it_still_runs()
    {
        var clock = new StubTimeProvider(Base);
        using var h = SparkServiceHarness.Create(
            timeProvider: clock, exitStateCallDeadline: TimeSpan.FromMilliseconds(200));
        h.SeedStore(StoreId, SparkServiceHarness.MnemonicFor(1));
        await h.Service.StartAsync(Ct);
        await h.Service.WhenExitStateRestoredAsync(StoreId).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var wallet = h.Sdk.Clients[StoreId];
        var hung = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        wallet.ExportOverride = () => hung.Task;

        // The pass returns — it runs on one of BTCPay's few shared periodic loops, and a hung SDK call must
        // not hold that loop — and stores nothing.
        await h.Service.TakeDueExitStateBackupsAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Single(wallet.ExitExportCalls);
        Assert.Null(await h.ExitStateBackups.ReadAsync(StoreId, Ct));
        Assert.Contains("exceeded", h.Log.AllText);

        // Still running: the next pass does not stack a second call on the stuck one.
        clock.Advance(TimeSpan.FromMinutes(1));
        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        Assert.Single(wallet.ExitExportCalls);

        // Once it finishes, the store is asked again.
        hung.SetResult("late");
        wallet.ExportOverride = null;
        clock.Advance(TimeSpan.FromMinutes(1));
        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        Assert.Equal(2, wallet.ExitExportCalls.Count);
        Assert.Equal("exit-state-blob", await h.ExitStateBackups.ReadAsync(StoreId, Ct));
    }

    // ------------------------------------------------------------------------------------------------
    // Removal and re-provisioning
    // ------------------------------------------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task Removing_Flint_keeps_the_store_s_backups_aside_under_names_that_say_so()
    {
        using var h = await StartedAsync(new StubTimeProvider(Base));
        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        h.Sdk.Clients[StoreId].FailImportWith = new InvalidOperationException("refused");
        await h.ExitStateBackups.AddPendingAsync(StoreId, "still-waiting", Ct);
        await h.Service.ImportPendingExitStateAsync(StoreId, Ct);

        await h.Service.Set(StoreId, null);

        // Not deleted — removing Flint is not evidence the funds were swept — and not left under the live
        // names, where a later re-provision of this store would take them for its own.
        Assert.Null(await h.ExitStateBackups.ReadAsync(StoreId, Ct));
        Assert.Empty(await h.ExitStateBackups.ListPendingAsync(StoreId, Ct));
        Assert.Equal(2, Directory.GetFiles(BackupDirectory(h), StoreId + ".removed-*.txt").Length);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_settings_write_cannot_bring_back_a_deprecated_backup_that_was_cleared()
    {
        using var h = await StartedAsync(new StubTimeProvider(Base));

        // A whole-settings write built from a copy read before a clear: the deprecated slot is only ever
        // cleared now, so a value the stored configuration no longer has is stale and must not be persisted.
        var stale = (await h.Service.Get(StoreId))!;
        stale.UnilateralExit = new UnilateralExitSettings { ExitStateBackup = "cleared-a-moment-ago" };

        await h.Service.Set(StoreId, stale);

        Assert.Null(h.Stores.Stored<SparkSettings>(StoreId, Constants.StoreSettingsKey)!.UnilateralExit!.ExitStateBackup);
        Assert.Null((await h.Service.Get(StoreId))!.UnilateralExit!.ExitStateBackup);
    }

    // ------------------------------------------------------------------------------------------------
    // Wiring
    // ------------------------------------------------------------------------------------------------

    private static async Task<SparkServiceHarness> StartedAsync(TimeProvider clock)
    {
        var h = SparkServiceHarness.Create(timeProvider: clock);
        try
        {
            h.SeedStore(StoreId, SparkServiceHarness.MnemonicFor(1));
            await h.Service.StartAsync(CancellationToken.None);
            await h.Service.WhenExitStateRestoredAsync(StoreId).WaitAsync(TimeSpan.FromSeconds(10));
            return h;
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    /// <summary>What a real connect leaves in the store's SDK directory: the SDK's own database, nested.</summary>
    private static void CreateSdkStorage(SparkServiceHarness h)
    {
        var nested = Path.Combine(h.StorageDirFor(StoreId), "regtest", "0a1b2c3d");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "storage.sql"), "sqlite");
    }

    private static string BackupDirectory(SparkServiceHarness h) =>
        RawStore(h).StorageDirectory();

    private static FileExitStateBackupStore RawStore(SparkServiceHarness h) =>
        new(Options.Create(new DataDirectories { DataDir = h.DataDir }), NullLogger<FileExitStateBackupStore>.Instance);

    /// <summary>Writes the automatic backup with no stamp beside it — the shape an earlier build left.</summary>
    private static async Task WriteUnstampedBackupAsync(SparkServiceHarness h, string content)
    {
        var raw = RawStore(h);
        await raw.WriteAsync(StoreId, content, null);
        File.Delete(raw.StampPathFor(StoreId));
    }

    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for {because}");
            await Task.Delay(20, CancellationToken.None);
        }
    }

}
