using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// What a migration does to rows that already exist.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are otherwise unexercised by this suite: the Postgres store tests build a schema from scratch, so
/// they never observe a backfill. That is fine for column shapes and wrong for <em>defaults</em>, which only
/// matter to rows written before the migration ran — the rows on a real merchant's server.
/// </para>
/// <para>
/// Inspecting the operations through EF's own API rather than scanning the file, so a hand edit that changed
/// the value would be caught but a reformat would not.
/// </para>
/// </remarks>
public class SparkMigrationTests
{
    /// <summary>
    /// Existing sweep rows are backfilled as carrying a usable idempotency key.
    /// </summary>
    /// <remarks>
    /// <b>The scaffolder generates <c>false</c> here, and <c>false</c> would be wrong in a way that costs
    /// money.</b> Every row written before Wave 7 is a cooperative exit whose idempotency key the SDK adopted as
    /// its own payment id — that is what makes a crashed sweep resolvable. Backfilling <c>false</c> would tell
    /// the engine's recovery walk that those rows must instead be matched by a provider quote id, a column that
    /// is null on all of them; any sweep in flight across the upgrade would then be written off as unresolvable
    /// after the grace period, for a send that very likely succeeded and could have been looked up.
    /// </remarks>
    [Fact]
    public void The_cross_chain_migration_treats_existing_rows_as_having_a_usable_idempotency_key()
    {
        var operation = Assert.Single(
            new CrossChainSweepFields().UpOperations
                .OfType<AddColumnOperation>()
                .Where(o => o.Name == nameof(SweepRecord.IdempotencyKeyAccepted)));

        Assert.Equal("SweepRecords", operation.Table);
        Assert.False(operation.IsNullable);
        Assert.Equal(true, operation.DefaultValue);
    }

    /// <summary>
    /// Existing rows are backfilled as cooperative exits, which is what they are.
    /// </summary>
    /// <remarks>
    /// True by the enum's zero value rather than by an explicit default, which is exactly why it is worth
    /// pinning: reordering <see cref="Services.SweepDestinationKind"/> so that <c>EvmAddress</c> came first
    /// would silently reclassify every historical sweep as cross-chain, and nothing else would notice.
    /// </remarks>
    [Fact]
    public void The_cross_chain_migration_treats_existing_rows_as_cooperative_exits()
    {
        var operation = Assert.Single(
            new CrossChainSweepFields().UpOperations
                .OfType<AddColumnOperation>()
                .Where(o => o.Name == nameof(SweepRecord.DestinationKind)));

        Assert.Equal((int)Services.SweepDestinationKind.BitcoinAddress, operation.DefaultValue);
    }
}
