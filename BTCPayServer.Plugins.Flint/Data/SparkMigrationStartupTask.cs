using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// Applies the plugin's pending EF migrations at startup.
/// </summary>
/// <remarks>
/// BTCPay does not migrate plugin schemas for you: each plugin owns its schema and migrates it from
/// an <see cref="IStartupTask"/>. Startup tasks run before the web host serves requests, so
/// <see cref="SparkService"/> can assume the tables exist.
/// </remarks>
public class SparkMigrationStartupTask : IStartupTask
{
    private readonly SparkPluginDbContextFactory _contextFactory;
    private readonly ILogger<SparkMigrationStartupTask> _logger;

    public SparkMigrationStartupTask(
        SparkPluginDbContextFactory contextFactory,
        ILogger<SparkMigrationStartupTask> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var context = _contextFactory.CreateContext();
        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
            return;

        _logger.LogInformation("Applying {Count} Spark plugin migration(s)", pending.Count);
        await context.Database.MigrateAsync(cancellationToken);
    }
}
