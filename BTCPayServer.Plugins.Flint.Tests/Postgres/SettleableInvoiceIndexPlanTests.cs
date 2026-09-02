using BTCPayServer.Plugins.Flint.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests.Postgres;

/// <summary>
/// The planner property the whole expiry-order reorder exists for: the reconciliation walk must actually
/// be planned as a seek into <c>IX_InvoiceRecords_StoreId_ExpiresAt_Settleable</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is pinned as a plan rather than as a result set.</b> Every other store test passes whether or
/// not the planner uses the index — they assert rows, and the walk returns the same rows with a seq scan and a
/// sort. The reorder of <c>ListForReconciliationAsync</c> to <c>ORDER BY ExpiresAt, PaymentHash</c> changed
/// nothing observable about the output and everything about the plan: measured against a real table, the
/// planner refused the partial index while the walk ordered by <c>CreatedAt</c>, and takes it once the ORDER BY
/// rides the index key. That is a planner property, so it is pinned with <c>EXPLAIN</c> — the only statement
/// that reports it. If a future change widens the predicate, renames the index, or drops the ORDER BY columns,
/// the walk silently regresses to walking the whole discarded history and only this test notices.
/// </para>
/// <para>
/// <b>Seeded shape.</b> The table is seeded the way the store discards history: 500 expired-and-abandoned
/// rows created oldest-first with their expiries far past the walk's 14-day grace floor (the rows the old
/// <c>CreatedAt</c> walk walked pointlessly), plus a handful of genuinely settleable rows ahead of the floor.
/// 500 is the measured floor, not a load test: against a live Postgres, 100 seeded rows leave the planner on
/// a seq scan plus a top-N sort — the heap is simply too small — and from 500 the index seek wins. The
/// original A/B measured the refusal at 500k rows and the switch at this same query shape. The query is
/// issued with literals rather than bind parameters, because <c>EXPLAIN</c> costed against unknown
/// parameter values would not be the plan production gets for a real store id. This seed is the minimal
/// witness of the property, not a load test.
/// </para>
/// <para>
/// Skipped unless <c>SPARK_POSTGRES_TESTS</c> holds a connection string, like every other test against the
/// shared Postgres fixture.
/// </para>
/// </remarks>
[Trait("Category", "Postgres")]
[Collection(PostgresTestDatabase.CollectionName)]
public sealed class SettleableInvoiceIndexPlanTests
{
    private const string StoreId = "plan-test-store";
    private const string IndexName = "IX_InvoiceRecords_StoreId_ExpiresAt_Settleable";

    /// <summary>Discarded-history rows seeded a day apart, oldest-created first, all past the walk's
    /// 14-day grace floor — and enough of them that a seq scan stops being the cheaper plan.</summary>
    private const int AbandonedRows = 500;

    private readonly PostgresTestDatabase _database;

    public SettleableInvoiceIndexPlanTests(PostgresTestDatabase database) => _database = database;

    [Fact]
    public async Task The_reconciliation_walk_plans_as_a_seek_into_the_settleable_partial_index()
    {
        var factory = await _database.CreateFactoryAsync();
        await using var context = factory.CreateContext();

        // Same raw-seed shape as the A/B harness: status 0 (Unpaid) rows whose expiry sits a day apart,
        // oldest-created first, all older than the 14-day settleable floor — abandoned history. Then a few
        // rows whose expiry is ahead of the floor, which is what the walk should reach directly.
        await context.Database.ExecuteSqlRawAsync(
            $"""
             INSERT INTO "{Constants.DatabaseSchema}"."InvoiceRecords"
                     ("PaymentHash", "StoreId", "Bolt11", "AmountMsat", "CreatedAt", "ExpiresAt", "Status")
             SELECT 'plan-ab-' || g, '{StoreId}', 'lnbcrt1000n1mock', 1000000,
                    now() - (({AbandonedRows} - g) * interval '1 day') - interval '15 days',
                    now() - (({AbandonedRows} - g) * interval '1 day') - interval '15 days' + interval '1 hour',
                    0
             FROM generate_series(1, {AbandonedRows}) g;
             INSERT INTO "{Constants.DatabaseSchema}"."InvoiceRecords"
                     ("PaymentHash", "StoreId", "Bolt11", "AmountMsat", "CreatedAt", "ExpiresAt", "Status")
             SELECT 'plan-ok-' || g, '{StoreId}', 'lnbcrt1000n1mock', 1000000,
                    now() - (g * interval '3 minutes'),
                    now() + interval '30 minutes' + (g % 5) * interval '1 hour',
                    0
             FROM generate_series(1, 5) g;
             ANALYZE "{Constants.DatabaseSchema}"."InvoiceRecords";
             """);

        // The production query shape of EfInvoiceRecordStore.ListForReconciliationAsync (no cursor): store
        // equality, not-yet-paid, the expiry floor, the walk's own ordering under its page limit — and a
        // whole-row select, because the walk materialises entities rather than a projection.
        var plan = new List<string>();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                 EXPLAIN (ANALYZE, COSTS OFF)
                 SELECT r.*
                 FROM "{Constants.DatabaseSchema}"."InvoiceRecords" r
                  WHERE r."StoreId" = '{StoreId}' AND r."Status" <> 1
                    AND r."ExpiresAt" > now() - interval '14 days'
                  ORDER BY r."ExpiresAt", r."PaymentHash"
                  LIMIT 100;
                 """;
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                plan.Add(reader.GetString(0));
        }

        var text = string.Join('\n', plan);
        Assert.Contains(IndexName, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan", text, StringComparison.Ordinal);
    }
}
