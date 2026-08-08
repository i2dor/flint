using BTCPayServer.Abstractions.Contracts;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IStoreRepository"/> that stores what BTCPay's real one stores: JSON.
/// </summary>
/// <remarks>
/// <para>
/// The round trip is the point, not a convenience. <c>StoreRepository</c> persists a serialised blob and
/// deserialises a <em>fresh</em> object on every read, so nothing a caller does to a value it read can reach
/// the database. A fake that handed the same instance back would quietly make the persistence layer share
/// aliasing that the real one does not have — and the settings-cache aliasing this plugin actually had lives
/// one layer above, in <c>SparkService</c>'s own cache. Modelling the repository faithfully is what keeps that
/// distinction testable.
/// </para>
/// <para>
/// <see cref="FailNextUpdateWith"/> throws <em>before</em> anything is stored, which is how a failed
/// <c>SaveChangesAsync</c> behaves: the write did not happen. A fake that stored first and threw afterwards
/// would make "the persisted state rolled back" untestable, and the in-memory cache's divergence from it
/// unobservable.
/// </para>
/// </remarks>
public sealed class FakeStoreRepository : IStoreRepository
{
    private readonly Dictionary<string, Dictionary<string, string>> _settings = [];

    /// <summary>Thrown by the next <see cref="UpdateSetting{T}"/>, before it stores anything.</summary>
    public Exception? FailNextUpdateWith { get; set; }

    /// <summary>Every write that was actually persisted, in order.</summary>
    public List<(string StoreId, string Name)> Writes { get; } = [];

    /// <summary>Seeds a stored setting the way a previous run would have left it.</summary>
    public FakeStoreRepository Seed<T>(string storeId, string name, T value) where T : class
    {
        Bucket(name)[storeId] = JsonConvert.SerializeObject(value);
        return this;
    }

    /// <summary>What is actually persisted, deserialised fresh. Null when nothing is.</summary>
    public T? Stored<T>(string storeId, string name) where T : class =>
        Bucket(name).TryGetValue(storeId, out var json) ? JsonConvert.DeserializeObject<T>(json) : null;

    public Task<T?> GetSettingAsync<T>(string storeId, string name) where T : class =>
        Task.FromResult(Stored<T>(storeId, name));

    public Task<Dictionary<string, T?>> GetSettingsAsync<T>(string name) where T : class =>
        Task.FromResult(Bucket(name).ToDictionary(
            pair => pair.Key,
            pair => JsonConvert.DeserializeObject<T>(pair.Value)));

    public Task UpdateSetting<T>(string storeId, string name, T obj) where T : class
    {
        if (FailNextUpdateWith is { } failure)
        {
            FailNextUpdateWith = null;
            throw failure;
        }

        // The interface says non-null; the real implementation deletes the row for a null, and the plugin
        // removes a store's configuration that way.
        if (obj is null)
            Bucket(name).Remove(storeId);
        else
            Bucket(name)[storeId] = JsonConvert.SerializeObject(obj);

        Writes.Add((storeId, name));
        return Task.CompletedTask;
    }

    private Dictionary<string, string> Bucket(string name)
    {
        if (!_settings.TryGetValue(name, out var bucket))
            _settings[name] = bucket = [];
        return bucket;
    }
}
