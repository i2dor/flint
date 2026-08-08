using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// Builds <see cref="SparkPluginDbContext"/> instances against BTCPay's configured database.
/// </summary>
/// <remarks>
/// Deriving from <see cref="BaseDbContextFactory{T}"/> is what gets the plugin BTCPay's Npgsql
/// conventions for free: retry-on-failure, the pinned Postgres version, the C-locale database
/// template, and a migrations-history table named after our schema rather than sharing BTCPay's
/// <c>__EFMigrationsHistory</c>.
/// </remarks>
public class SparkPluginDbContextFactory : BaseDbContextFactory<SparkPluginDbContext>
{
    private readonly ILoggerFactory _loggerFactory;

    public SparkPluginDbContextFactory(IOptions<DatabaseOptions> options, ILoggerFactory loggerFactory)
        : base(options, Constants.DatabaseSchema)
    {
        _loggerFactory = loggerFactory;
    }

    public override SparkPluginDbContext CreateContext(
        Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<SparkPluginDbContext>();
        builder.UseLoggerFactory(_loggerFactory);
        builder.AddInterceptors(MigrationInterceptor.Instance);
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new SparkPluginDbContext(builder.Options);
    }
}
