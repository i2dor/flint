using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Outcome of resolving a connection string to a live client. Exactly one of the two is set.
/// </summary>
public sealed record SparkClientResolution(ILightningClient? Client, string? Error)
{
    public static SparkClientResolution Failed(string error) => new(null, error);
    public static SparkClientResolution Resolved(ILightningClient client) => new(client, null);
}

/// <summary>
/// Resolves a store id plus payment key to that store's live Lightning client.
/// </summary>
/// <remarks>
/// A seam over <c>SparkService</c> so <see cref="SparkConnectionStringHandler"/> can be tested without
/// standing up the SDK, a store repository and a database.
/// </remarks>
public interface ISparkClientResolver
{
    SparkClientResolution Resolve(string storeId, string paymentKey, Network network);
}
