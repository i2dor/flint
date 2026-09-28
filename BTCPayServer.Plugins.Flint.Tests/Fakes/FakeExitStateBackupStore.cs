using System.IO;
using System.Text;
using BTCPayServer.Plugins.Flint.Services;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IExitStateBackupStore"/> for tests whose subject is a <em>caller</em> of the
/// store — what it writes, what it refuses, and what it leaves untouched on failure.
/// </summary>
/// <remarks>
/// Deliberately not used by anything that tests the store's own behaviour (the layout, the atomic
/// replace, the permissions): those tests run the real <see cref="FileExitStateBackupStore"/> over a
/// temp directory, because a fake would be asserting the fake. This one exists so the exit-service
/// tests can watch the calls and script failures without a filesystem in the way.
/// </remarks>
public sealed class FakeExitStateBackupStore : IExitStateBackupStore
{
    private readonly Dictionary<string, string> _files = [];
    private readonly Dictionary<string, ExitStateBackupStamp> _stamps = [];
    private readonly Dictionary<string, List<(string Id, string Content)>> _pending = [];
    private int _nextPending;

    /// <summary>Every store id the method was called with, in call order.</summary>
    public List<string> ReadCalls { get; } = [];

    /// <summary>Every store id the method was called with, in call order.</summary>
    public List<string> WriteCalls { get; } = [];

    /// <summary>Every store id the method was called with, in call order.</summary>
    public List<string> DeleteCalls { get; } = [];

    /// <summary>Every store id a backup was queued for, in call order — including a deduplicated one.</summary>
    public List<string> PendingAddCalls { get; } = [];

    /// <summary>What was set aside, as (store, reason) — the fake keeps no content for these.</summary>
    public List<(string StoreId, ExitStateBackupSetAside Reason)> SetAsides { get; } = [];

    /// <summary>Thrown by every read while set.</summary>
    public Exception? FailReadWith { get; set; }

    /// <summary>Thrown by every write while set — the automatic slot and the queue alike.</summary>
    public Exception? FailWriteWith { get; set; }

    /// <summary>The write time every stored file reports; the fake keeps one stamp for all of them.</summary>
    public DateTimeOffset? TakenAt { get; set; }

    /// <summary>What is stored for a store, or null when nothing is. Reads the subject's own writes.</summary>
    public string? Stored(string storeId) => _files.GetValueOrDefault(storeId);

    /// <summary>The contents queued for a store, oldest first.</summary>
    public IReadOnlyList<string> Pending(string storeId) =>
        _pending.TryGetValue(storeId, out var queue) ? queue.Select(entry => entry.Content).ToList() : [];

    public Task<string?> ReadAsync(string storeId, CancellationToken cancellationToken = default)
    {
        ReadCalls.Add(storeId);
        return FailReadWith is { } failure
            ? Task.FromException<string?>(failure)
            : Task.FromResult(Stored(storeId));
    }

    // A fresh stream per call, as a file would give: the caller owns and disposes what it opens. The
    // bytes are the UTF-8 encoding the file store's own read would produce, so a controller served by
    // this fake sees the same payload a controller served by the real store would.
    public Task<Stream?> OpenReadAsync(string storeId, CancellationToken cancellationToken = default)
    {
        if (FailReadWith is { } failure)
            return Task.FromException<Stream?>(failure);

        return Task.FromResult(AsStream(Stored(storeId)));
    }

    public Task<DateTimeOffset?> TakenAtAsync(string storeId, CancellationToken cancellationToken = default) =>
        FailReadWith is { } failure
            ? Task.FromException<DateTimeOffset?>(failure)
            : Task.FromResult(Stored(storeId) is null ? null : TakenAt);

    public Task WriteAsync(
        string storeId, string backup, string? walletIdentity, CancellationToken cancellationToken = default)
    {
        WriteCalls.Add(storeId);
        if (FailWriteWith is { } failure)
            return Task.FromException(failure);

        _files[storeId] = backup;
        _stamps[storeId] = new ExitStateBackupStamp(ExitStateBackupStamp.HashOf(backup), walletIdentity);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string storeId, CancellationToken cancellationToken = default)
    {
        DeleteCalls.Add(storeId);
        _stamps.Remove(storeId);
        return Task.FromResult(_files.Remove(storeId));
    }

    public Task<ExitStateBackupStamp?> ReadStampAsync(string storeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_stamps.GetValueOrDefault(storeId));

    public Task<bool> StampAsync(
        string storeId, string backup, string? walletIdentity, CancellationToken cancellationToken = default)
    {
        if (Stored(storeId) != backup)
            return Task.FromResult(false);

        _stamps[storeId] = new ExitStateBackupStamp(ExitStateBackupStamp.HashOf(backup), walletIdentity);
        return Task.FromResult(true);
    }

    public Task<string?> SetAsideAsync(
        string storeId, ExitStateBackupSetAside reason, CancellationToken cancellationToken = default)
    {
        if (!_files.Remove(storeId))
            return Task.FromResult<string?>(null);

        _stamps.Remove(storeId);
        SetAsides.Add((storeId, reason));
        return Task.FromResult<string?>($"{storeId}.{reason}.txt");
    }

    public Task<string> KeepAsideAsync(
        string storeId, string backup, ExitStateBackupSetAside reason, CancellationToken cancellationToken = default)
    {
        if (FailWriteWith is { } failure)
            return Task.FromException<string>(failure);

        SetAsides.Add((storeId, reason));
        return Task.FromResult($"{storeId}.{reason}.txt");
    }

    public Task<IReadOnlyList<PendingExitStateBackup>> ListPendingAsync(
        string storeId, CancellationToken cancellationToken = default)
    {
        if (FailReadWith is { } failure)
            return Task.FromException<IReadOnlyList<PendingExitStateBackup>>(failure);

        IReadOnlyList<PendingExitStateBackup> entries = _pending.TryGetValue(storeId, out var queue)
            ? queue.Select(entry => new PendingExitStateBackup(
                    entry.Id, TakenAt ?? DateTimeOffset.UnixEpoch, Encoding.UTF8.GetByteCount(entry.Content)))
                .ToList()
            : [];
        return Task.FromResult(entries);
    }

    public Task<string> AddPendingAsync(string storeId, string backup, CancellationToken cancellationToken = default)
    {
        PendingAddCalls.Add(storeId);
        if (FailWriteWith is { } failure)
            return Task.FromException<string>(failure);

        if (!_pending.TryGetValue(storeId, out var queue))
            _pending[storeId] = queue = [];

        if (queue.FirstOrDefault(entry => entry.Content == backup) is { Id: { } existing })
            return Task.FromResult(existing);

        var id = $"20260916T120000000Z-{_nextPending++:x8}";
        queue.Add((id, backup));
        return Task.FromResult(id);
    }

    public Task<string?> ReadPendingAsync(string storeId, string id, CancellationToken cancellationToken = default) =>
        FailReadWith is { } failure
            ? Task.FromException<string?>(failure)
            : Task.FromResult(FindPending(storeId, id));

    public Task<Stream?> OpenReadPendingAsync(string storeId, string id, CancellationToken cancellationToken = default) =>
        FailReadWith is { } failure
            ? Task.FromException<Stream?>(failure)
            : Task.FromResult(AsStream(FindPending(storeId, id)));

    public Task<bool> DeletePendingAsync(string storeId, string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_pending.TryGetValue(storeId, out var queue) && queue.RemoveAll(entry => entry.Id == id) > 0);

    public Task<string?> SetAsidePendingAsync(
        string storeId, string id, ExitStateBackupSetAside reason, CancellationToken cancellationToken = default)
    {
        if (!_pending.TryGetValue(storeId, out var queue) || queue.RemoveAll(entry => entry.Id == id) == 0)
            return Task.FromResult<string?>(null);

        SetAsides.Add((storeId, reason));
        return Task.FromResult<string?>($"{storeId}.{reason}.txt");
    }

    private string? FindPending(string storeId, string id) =>
        _pending.TryGetValue(storeId, out var queue)
            ? queue.FirstOrDefault(entry => entry.Id == id).Content
            : null;

    private static Stream? AsStream(string? content) =>
        content is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(content));
}
