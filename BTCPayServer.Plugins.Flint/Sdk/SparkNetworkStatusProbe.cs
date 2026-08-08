using System;
using System.Threading;
using System.Threading.Tasks;
using Breez.Sdk.Spark;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// The real <see cref="ISparkNetworkStatusProbe"/>, over the SDK's static <c>GetSparkStatus</c>.
/// </summary>
/// <remarks>
/// <para>
/// Bounded by <see cref="SparkDeadline"/> and never allowed to throw. This runs on a request thread rendering
/// a status page, the call is a network round trip to a third party, and no SDK call can be cancelled — so a
/// slow or unreachable status endpoint must degrade to "unknown" rather than hang the page or 500 it.
/// </para>
/// <para>
/// Loading the native library is a side effect of the first SDK call in the process (~450 ms). In practice
/// <c>SparkService.StartAsync</c> has already paid that cost by initialising logging, so this is not the
/// first call; the deadline covers the case where it is.
/// </para>
/// </remarks>
public sealed class SparkNetworkStatusProbe : ISparkNetworkStatusProbe
{
    /// <summary>
    /// Shorter than <see cref="Constants.SdkCallDeadline"/>: that budget is sized for background loops, and a
    /// page a merchant is waiting on cannot spend 30 s on an optional detail.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private readonly ILogger<SparkNetworkStatusProbe> _logger;

    public SparkNetworkStatusProbe(ILogger<SparkNetworkStatusProbe> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SparkNetworkStatus?> TryGetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await SparkDeadline
                .OrNullAsync(
                    BreezSdkSparkMethods.GetSparkStatus(),
                    Deadline,
                    () => _logger.LogDebug(
                        "Reading the Spark network status exceeded {Seconds}s", Deadline.TotalSeconds),
                    cancellationToken)
                .ConfigureAwait(false);

            if (status is null)
                return null;

            return new SparkNetworkStatus(
                status.status.ToString(),
                // Unix seconds, as a u64. Clamped rather than trusted: a malformed value from a third-party
                // endpoint must not throw out of a status page.
                ToTimestamp(status.lastUpdated));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the Spark network status");
            return null;
        }
    }

    private static DateTimeOffset ToTimestamp(ulong unixSeconds)
    {
        const long maxUnixSeconds = 253_402_300_799; // 9999-12-31T23:59:59Z, the limit DateTimeOffset accepts.
        var seconds = unixSeconds > maxUnixSeconds ? maxUnixSeconds : (long)unixSeconds;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }
}
