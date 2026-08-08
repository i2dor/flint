using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// Health of the Spark network itself, as reported by the service operators.
/// </summary>
/// <param name="Status">
/// The SDK's <c>ServiceStatus</c>: <c>Operational</c>, <c>Degraded</c>, <c>Partial</c>, <c>Major</c> or
/// <c>Unknown</c>.
/// </param>
/// <param name="LastUpdated">When the operators last published that status.</param>
public sealed record SparkNetworkStatus(string Status, DateTimeOffset LastUpdated)
{
    /// <summary>True only for an explicitly operational network. <c>Unknown</c> is not treated as healthy.</summary>
    public bool IsOperational => string.Equals(Status, "Operational", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the Spark network's published status. Needs no wallet and no connection.
/// </summary>
/// <remarks>
/// Surfaced on the status page because every Lightning receive this plugin handles rides the Lightspark
/// service provider: when receives start failing, "is Spark up" is the first question, and
/// the answer is free. Breez's own "moving to production" checklist asks for it.
/// </remarks>
public interface ISparkNetworkStatusProbe
{
    /// <summary>
    /// The current status, or null when it could not be read. A null is normal enough to render as
    /// "unknown" rather than as an error: this is a third-party status endpoint.
    /// </summary>
    Task<SparkNetworkStatus?> TryGetAsync(CancellationToken cancellationToken = default);
}
