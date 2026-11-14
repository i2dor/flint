using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BTCPayServer.Plugins.Flint.Data;

public sealed class EfSparkLedgerStore : ISparkLedgerStore
{
    private readonly SparkPluginDbContextFactory _factory;

    public EfSparkLedgerStore(SparkPluginDbContextFactory factory)
    {
        _factory = factory;
    }

    public async Task AppendAsync(SparkLedgerEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = _factory.CreateContext();
            context.SparkLedger.Add(entry);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Two concurrent callers both passed the ExistsFor... guard before either committed.
            // The second insert hits the UNIQUE index; the entry is already recorded, so this is success.
        }
    }

    public async Task<bool> ExistsForSdkPaymentAsync(
        string storeId,
        string sdkPaymentId,
        CancellationToken cancellationToken = default)
    {
        await using var context = _factory.CreateContext();
        return await context.SparkLedger
            .Where(e => e.StoreId == storeId && e.SdkPaymentId == sdkPaymentId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ExistsForPaymentHashAsync(
        string storeId,
        string paymentHash,
        CancellationToken cancellationToken = default)
    {
        await using var context = _factory.CreateContext();
        return await context.SparkLedger
            .Where(e => e.StoreId == storeId && e.PaymentHash == paymentHash)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SparkLedgerEntry>> ListAsync(
        string storeId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        await using var context = _factory.CreateContext();
        return await context.SparkLedger
            .Where(e => e.StoreId == storeId)
            .OrderByDescending(e => e.OccurredAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<long> CountAsync(string storeId, CancellationToken cancellationToken = default)
    {
        await using var context = _factory.CreateContext();
        return await context.SparkLedger
            .Where(e => e.StoreId == storeId)
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
