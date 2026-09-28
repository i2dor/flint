using System.Text;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Options;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The real <see cref="FileExitStateBackupStore"/>, over a real temp directory: what it stores is a
/// multi-megabyte secret that has to survive being written, and both halves of that sentence are
/// filesystem behaviour a fake cannot stand in for.
/// </summary>
/// <remarks>
/// No feature gate is involved — the store is storage, not the feature; whether anything is written to
/// it is decided above it, and covered in <c>SparkExitStateAutoBackupTests</c>.
/// </remarks>
public class FileExitStateBackupStoreTests
{
    private const string Store = "store-1";

    private const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Fact]
    public async Task A_store_with_no_file_reads_as_absent_rather_than_failing()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        Assert.Null(await store.ReadAsync(Store));
        Assert.Null(await store.TakenAtAsync(Store));
        Assert.False(await store.DeleteAsync(Store));
    }

    [Fact]
    public async Task What_is_written_comes_back_and_the_write_leaves_no_temporary_behind()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        await store.WriteAsync(Store, "the-stored-backup-blob", null);

        Assert.Equal("the-stored-backup-blob", await store.ReadAsync(Store));

        // The atomic write lands via a `.tmp` sibling renamed over the target. A leftover temp is a
        // half-written backup sitting in the same directory as the real one — an operator reading
        // this directory (and they are the only audience that should) should never find one.
        Assert.Empty(Directory.GetFiles(store.StorageDirectory(), "*.tmp"));

        Assert.NotNull(await store.TakenAtAsync(Store));
    }

    [Fact]
    public async Task A_second_write_replaces_the_first_rather_than_failing_on_the_existing_file()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        await store.WriteAsync(Store, "first-backup", null);
        await store.WriteAsync(Store, "second-backup", null);

        Assert.Equal("second-backup", await store.ReadAsync(Store));
    }

    [Fact]
    public async Task Taken_before_the_first_write_is_null_and_a_deletion_makes_it_null_again()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        Assert.Null(await store.TakenAtAsync(Store));

        await store.WriteAsync(Store, "the-stored-backup-blob", null);
        var written = await store.TakenAtAsync(Store);
        Assert.NotNull(written);

        // UTC-offset zero, because a caller compares it against its own UTC-sourced clock and an
        // unspecified offset would compare two different zones as if they were one.
        Assert.Equal(TimeSpan.Zero, written!.Value.Offset);

        Assert.True(await store.DeleteAsync(Store));
        Assert.Null(await store.ReadAsync(Store));
        Assert.Null(await store.TakenAtAsync(Store));
    }

    [Fact]
    public async Task The_directory_holds_the_backup_and_nothing_of_the_backup_appears_in_the_log()
    {
        using var dir = new TempDirectory();
        var log = new CapturingLogger<FileExitStateBackupStore>();
        var store = Create(dir, log);
        var secret = "blob-that-must-not-be-logged-9f3a";

        await store.WriteAsync(Store, secret, null);
        Assert.Equal(secret, await store.ReadAsync(Store));
        await store.DeleteAsync(Store);

        Assert.DoesNotContain(secret, log.AllText);
    }

    [Fact]
    public async Task The_backups_directory_is_owner_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");

        using var dir = new TempDirectory();
        var store = Create(dir);

        await store.WriteAsync(Store, "the-stored-backup-blob", null);

        // The same hardening the SDK's per-store directory and the log directory get — this one
        // holds, per store, the wallet's whole exit state as one readable string.
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(store.StorageDirectory()));
    }

    [Fact(Timeout = 60_000)]
    public async Task The_backup_file_itself_is_owner_only()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix file modes.");

        using var dir = new TempDirectory();
        var store = Create(dir);

        await store.WriteAsync(Store, "the-stored-backup-blob", null);

        // The 0700 directory keeps other accounts out; this is the second line of defence for the
        // one that is already in — the mode is set on the temporary before the rename carries it to
        // the target, so the secret is never world-readable under either name, and a reader that
        // reaches past the directory still meets a file it cannot open.
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(store.PathFor(Store)));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_stream_with_nothing_stored_answers_null_and_an_open_one_answers_the_stored_bytes()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        // Null, not an empty stream — the download answers "there is no backup" only to a null, and a
        // zero-byte file would have read as a backup that imports as nothing.
        await using (var absent = await store.OpenReadAsync(Store))
            Assert.Null(absent);

        await store.WriteAsync(Store, "the-stored-backup-blob", null);

        // The bytes a download hands over, read back off an open handle: this seam never interprets
        // the content, and the streaming read is the same rule — what comes out is what went in.
        await using var stream = await store.OpenReadAsync(Store)
            ?? throw new InvalidOperationException("the stored backup did not open");
        using var reader = new StreamReader(stream);
        Assert.Equal("the-stored-backup-blob", await reader.ReadToEndAsync());
    }

    [Fact(Timeout = 60_000)]
    public async Task An_open_download_is_neither_blocked_by_the_next_write_nor_cut_short_by_it()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);
        await store.WriteAsync(Store, "first-backup", null);

        // The FileShare promise, as a fact about a filesystem rather than a comment: a pass that
        // refreshes the backup halfway through a download renames a new file over the one being
        // served, and the handle already open reads to a clean end of the bytes it opened — the
        // response is one whole backup or another, never an error and never a mixture.
        await using var serving = await store.OpenReadAsync(Store)
            ?? throw new InvalidOperationException("the stored backup did not open");
        await store.WriteAsync(Store, "second-backup-longer", null);

        using var reader = new StreamReader(serving);
        Assert.Equal("first-backup", await reader.ReadToEndAsync());
    }

    [Fact(Timeout = 60_000)]
    public async Task Concurrent_writers_publish_one_whole_backup_and_leave_no_temporary_behind()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        // Big enough that the writes overlap in time, distinct enough that a mixture is detectable: with
        // one shared temporary name, two writers wrote into the same file and the rename could publish
        // the front of one and the back of the other.
        var candidates = Enumerable.Range(0, 8)
            .Select(i => new string((char)('a' + i), 512 * 1024))
            .ToArray();

        await Task.WhenAll(candidates.Select(content => Task.Run(() => store.WriteAsync(Store, content, null))));

        var stored = await store.ReadAsync(Store);
        Assert.Contains(stored, candidates);
        Assert.Empty(Directory.GetFiles(store.StorageDirectory(), "*.tmp"));
    }

    [Fact(Timeout = 60_000)]
    public async Task The_tracked_store_s_belief_matches_the_file_after_concurrent_writes()
    {
        using var dir = new TempDirectory();
        var scheduler = new ExitStateBackupScheduler();
        var tracked = new TrackedExitStateBackupStore(Create(dir), scheduler);

        var candidates = Enumerable.Range(0, 8)
            .Select(i => new string((char)('a' + i), 256 * 1024))
            .ToArray();

        await Task.WhenAll(candidates.Select(content => Task.Run(() => tracked.WriteAsync(Store, content, null))));

        // The note and the write are one step: whichever write landed last is the one the scheduler
        // believes, so the next pass compares against the file that is really there.
        var onDisk = await tracked.ReadAsync(Store);
        Assert.NotNull(onDisk);
        Assert.True(scheduler.ContentUnchanged(Store, onDisk));
    }

    [Fact]
    public async Task A_failed_write_makes_the_tracked_belief_unknown_rather_than_stale()
    {
        var scheduler = new ExitStateBackupScheduler();
        var inner = new FakeExitStateBackupStore();
        var tracked = new TrackedExitStateBackupStore(inner, scheduler);

        await tracked.WriteAsync(Store, "first", null);
        Assert.True(scheduler.ContentUnchanged(Store, "first"));

        inner.FailWriteWith = new IOException("disk full");
        await Assert.ThrowsAsync<IOException>(() => tracked.WriteAsync(Store, "second", null));

        // Unknown, so the next pass re-seeds from the file instead of trusting a belief a failed write may
        // have falsified.
        Assert.False(scheduler.KnowsStoredContent(Store));
    }

    [Fact]
    public async Task Reads_of_a_store_whose_directory_does_not_exist_yet_answer_absent()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        // No write has ever created the directory: "absent", not a DirectoryNotFoundException out of a
        // download or a connect.
        Assert.False(Directory.Exists(store.StorageDirectory()));
        Assert.Null(await store.ReadAsync(Store));
        await using (var stream = await store.OpenReadAsync(Store))
            Assert.Null(stream);
        Assert.Null(await store.TakenAtAsync(Store));
    }

    [Fact]
    public async Task A_backup_an_earlier_build_left_readable_by_others_is_restricted_on_first_read()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix file modes.");

        using var dir = new TempDirectory();
        var store = Create(dir);

        // What a file written before the owner-only temporary looks like: the secret at 0644 inside the
        // 0700 directory, and it stays that way until its next changed write — never, on an idle wallet.
        Directory.CreateDirectory(store.StorageDirectory());
        await File.WriteAllTextAsync(store.PathFor(Store), "old-build-backup");
        File.SetUnixFileMode(store.PathFor(Store),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Assert.Equal("old-build-backup", await store.ReadAsync(Store));

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(store.PathFor(Store)));
    }

    [Fact]
    public async Task A_temporary_abandoned_by_a_crashed_write_is_cleaned_up_by_a_later_write()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);
        await store.WriteAsync(Store, "first-backup", null);

        // A process killed mid-write leaves its uniquely named temporary behind, and nothing would ever
        // look for it again. An hour old is old enough that no live writer can still own it.
        var abandoned = store.PathFor(Store) + ".0123456789abcdef0123456789abcdef.tmp";
        await File.WriteAllTextAsync(abandoned, "half-written");
        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow - TimeSpan.FromHours(2));

        await store.WriteAsync(Store, "second-backup", null);

        Assert.False(File.Exists(abandoned));
        Assert.Equal("second-backup", await store.ReadAsync(Store));
    }

    [Fact]
    public async Task A_write_stamps_the_backup_with_its_digest_and_the_wallet_that_wrote_it()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        Assert.Null(await store.ReadStampAsync(Store));

        await store.WriteAsync(Store, "wallet-export", "02aa");

        var stamp = await store.ReadStampAsync(Store);
        Assert.NotNull(stamp);
        Assert.True(stamp.Describes("wallet-export"));
        Assert.False(stamp.Describes("something-else"));
        Assert.Equal("02aa", stamp.WalletIdentity);

        // And the stamp goes with the backup it describes.
        await store.DeleteAsync(Store);
        Assert.Null(await store.ReadStampAsync(Store));
    }

    [Fact]
    public async Task A_stamp_is_written_only_for_the_content_the_file_actually_holds()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);
        await store.WriteAsync(Store, "on-disk", null);

        // A caller that imported bytes the file no longer holds must not stamp the file with them.
        Assert.False(await store.StampAsync(Store, "imported-earlier", "02aa"));
        Assert.True((await store.ReadStampAsync(Store))!.Describes("on-disk"));

        Assert.True(await store.StampAsync(Store, "on-disk", "02aa"));
        Assert.Equal("02aa", (await store.ReadStampAsync(Store))!.WalletIdentity);
    }

    [Fact]
    public async Task A_different_wallet_s_first_write_keeps_the_previous_wallet_s_backup_aside()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        await store.WriteAsync(Store, "old-wallet-exit-data", "02old");
        await store.WriteAsync(Store, "new-wallet-exit-data", "02new");

        // A re-provision onto a new seed: the old wallet's backup is its only device-proof exit data, and the
        // new wallet's first pass must not be what destroys it.
        Assert.Equal("new-wallet-exit-data", await store.ReadAsync(Store));
        var aside = Assert.Single(Directory.GetFiles(store.StorageDirectory(), Store + ".other-wallet-*.txt"));
        Assert.Equal("old-wallet-exit-data", await File.ReadAllTextAsync(aside));

        // The same wallet, or one whose identity is unknown, replaces as it always did.
        await store.WriteAsync(Store, "newer", "02new");
        await store.WriteAsync(Store, "newest", null);
        Assert.Single(Directory.GetFiles(store.StorageDirectory(), Store + ".other-wallet-*.txt"));
        Assert.Equal("newest", await store.ReadAsync(Store));
    }

    [Fact]
    public async Task Queued_backups_are_kept_apart_from_the_automatic_one_deduplicated_and_listed_oldest_first()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);
        await store.WriteAsync(Store, "automatic", "02aa");

        var first = await store.AddPendingAsync(Store, "pasted-one");
        await Task.Delay(5);
        var second = await store.AddPendingAsync(Store, "pasted-two");
        var again = await store.AddPendingAsync(Store, "pasted-one");

        Assert.Equal(first, again);
        var queued = await store.ListPendingAsync(Store);
        Assert.Equal([first, second], queued.Select(entry => entry.Id));
        Assert.Equal("pasted-one", await store.ReadPendingAsync(Store, first));
        Assert.Equal(Encoding.UTF8.GetByteCount("pasted-two"), queued[1].Length);

        // An automatic write never reaches the queue.
        await store.WriteAsync(Store, "automatic-next", "02aa");
        Assert.Equal(2, (await store.ListPendingAsync(Store)).Count);

        Assert.True(await store.DeletePendingAsync(Store, first));
        Assert.Null(await store.ReadPendingAsync(Store, first));
        Assert.Single(await store.ListPendingAsync(Store));

        var aside = await store.SetAsidePendingAsync(Store, second, ExitStateBackupSetAside.Foreign);
        Assert.NotNull(aside);
        Assert.StartsWith(Store + ".foreign-", aside);
        Assert.Empty(await store.ListPendingAsync(Store));
        Assert.True(File.Exists(Path.Combine(store.StorageDirectory(), aside)));
    }

    [Fact]
    public async Task A_pending_id_that_is_not_one_the_store_generated_is_refused()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        // Ids come back from a form; anything but the generated shape could name another file.
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadPendingAsync(Store, "../../etc/passwd"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadPendingAsync(Store, "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeletePendingAsync(Store, ""));
    }

    [Fact]
    public async Task Setting_the_automatic_backup_aside_moves_it_and_its_stamp_out_of_the_live_names()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);
        Assert.Null(await store.SetAsideAsync(Store, ExitStateBackupSetAside.Removed));

        await store.WriteAsync(Store, "exit-data", "02aa");
        var aside = await store.SetAsideAsync(Store, ExitStateBackupSetAside.Removed);

        Assert.NotNull(aside);
        Assert.StartsWith(Store + ".removed-", aside);
        Assert.Null(await store.ReadAsync(Store));
        Assert.Null(await store.ReadStampAsync(Store));
        Assert.Equal("exit-data", await File.ReadAllTextAsync(Path.Combine(store.StorageDirectory(), aside)));

        var kept = await store.KeepAsideAsync(Store, "legacy-copy", ExitStateBackupSetAside.OtherWallet);
        Assert.StartsWith(Store + ".other-wallet-", kept);
        Assert.Equal("legacy-copy", await File.ReadAllTextAsync(Path.Combine(store.StorageDirectory(), kept)));
    }

    [Fact]
    public void A_store_id_that_could_escape_the_owner_only_directory_is_refused()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        // The id is BTCPay's own, not attacker-controlled — but a separator in it would place a
        // backup outside the directory whose permissions are the whole protection, so it is a
        // refusal rather than a judgment call.
        _ = Assert.Throws<ArgumentException>(() => store.PathFor("../elsewhere"));
        _ = Assert.Throws<ArgumentException>(() => store.PathFor(""));
    }

    private static FileExitStateBackupStore Create(
        TempDirectory dir, CapturingLogger<FileExitStateBackupStore>? log = null) =>
        new(Options.Create(new DataDirectories { DataDir = dir.Path }),
            log ?? new CapturingLogger<FileExitStateBackupStore>());

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "spark-backup-store-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
