using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

public sealed record BackfillCount(int Skipped, int Created, int Failed)
{
    public static BackfillCount Zero => new(0, 0, 0);

    public BackfillCount Add(BackfillCount other) =>
        new(Skipped + other.Skipped, Created + other.Created, Failed + other.Failed);
}

public sealed record BackfillReport(
    bool DryRun,
    string StoreId,
    BackfillCount LightningReceives,
    BackfillCount StablecoinReceives,
    BackfillCount Sends,
    BackfillCount Sweeps,
    BackfillCount UnilateralExits)
{
    public BackfillCount Total =>
        LightningReceives
            .Add(StablecoinReceives)
            .Add(Sends)
            .Add(Sweeps)
            .Add(UnilateralExits);
}

public sealed class SparkLedgerBackfillService(
    SparkPluginDbContextFactory dbFactory,
    ISparkLedgerStore ledger,
    ILogger<SparkLedgerBackfillService> logger)
{
    public async Task<BackfillReport> RunAsync(
        string storeId,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Store {StoreId}: ledger backfill starting (dry={DryRun})",
            storeId, dryRun);

        var lightning = await BackfillLightningAsync(storeId, dryRun, cancellationToken)
            .ConfigureAwait(false);
        var stablecoin = await BackfillStablecoinAsync(storeId, dryRun, cancellationToken)
            .ConfigureAwait(false);
        var sends = await BackfillSendsAsync(storeId, dryRun, cancellationToken)
            .ConfigureAwait(false);
        var sweeps = await BackfillSweepsAsync(storeId, dryRun, cancellationToken)
            .ConfigureAwait(false);
        var exits = await BackfillUnilateralExitsAsync(storeId, dryRun, cancellationToken)
            .ConfigureAwait(false);

        var report = new BackfillReport(dryRun, storeId, lightning, stablecoin, sends, sweeps, exits);
        logger.LogInformation(
            "Store {StoreId}: ledger backfill done (dry={DryRun}) lightning={L} stablecoin={S} sends={Se} sweeps={Sw} exits={E}",
            storeId, dryRun,
            $"{lightning.Created}c/{lightning.Skipped}s/{lightning.Failed}f",
            $"{stablecoin.Created}c/{stablecoin.Skipped}s/{stablecoin.Failed}f",
            $"{sends.Created}c/{sends.Skipped}s/{sends.Failed}f",
            $"{sweeps.Created}c/{sweeps.Skipped}s/{sweeps.Failed}f",
            $"{exits.Created}c/{exits.Skipped}s/{exits.Failed}f");
        return report;
    }

    private async Task<BackfillCount> BackfillLightningAsync(
        string storeId, bool dryRun, CancellationToken cancellationToken)
    {
        var skipped = 0;
        var created = 0;
        var failed = 0;

        await using var context = dbFactory.CreateContext();
        var records = await context.InvoiceRecords
            .AsNoTracking()
            .Where(r => r.StoreId == storeId && r.CreditedAt != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var record in records)
        {
            try
            {
                var existsByHash = record.PaymentHash != null
                    && await ledger.ExistsForPaymentHashAsync(storeId, record.PaymentHash, cancellationToken)
                        .ConfigureAwait(false);
                var existsBySdk = record.SdkPaymentId != null
                    && await ledger.ExistsForSdkPaymentAsync(storeId, record.SdkPaymentId, cancellationToken)
                        .ConfigureAwait(false);
                if (existsByHash || existsBySdk)
                {
                    skipped++;
                    continue;
                }

                if (!dryRun)
                {
                    await ledger.AppendAsync(new SparkLedgerEntry
                    {
                        Id = Guid.NewGuid().ToString(),
                        StoreId = storeId,
                        EntryType = SparkLedgerEntryType.LightningReceive,
                        AmountMsat = record.AmountReceivedMsat ?? 0,
                        FeeMsat = 0,
                        OccurredAt = record.SettledAt ?? record.CreditedAt!.Value,
                        RecordedAt = record.CreditedAt!.Value,
                        SdkPaymentId = record.SdkPaymentId,
                        PaymentHash = record.PaymentHash,
                        IsBackfill = true,
                    }, cancellationToken).ConfigureAwait(false);
                }
                created++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex,
                    "Store {StoreId}: lightning backfill failed for hash {PaymentHash}",
                    storeId, record.PaymentHash);
            }
        }

        return new BackfillCount(skipped, created, failed);
    }

    private async Task<BackfillCount> BackfillStablecoinAsync(
        string storeId, bool dryRun, CancellationToken cancellationToken)
    {
        var skipped = 0;
        var created = 0;
        var failed = 0;

        await using var context = dbFactory.CreateContext();
        var quotes = await context.StablecoinQuotes
            .AsNoTracking()
            .Where(q => q.StoreId == storeId && q.CreditedAt != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var quote in quotes)
        {
            try
            {
                var exists = quote.SdkPaymentId != null
                    && await ledger.ExistsForSdkPaymentAsync(storeId, quote.SdkPaymentId, cancellationToken)
                        .ConfigureAwait(false);
                if (exists)
                {
                    skipped++;
                    continue;
                }

                if (!dryRun)
                {
                    await ledger.AppendAsync(new SparkLedgerEntry
                    {
                        Id = Guid.NewGuid().ToString(),
                        StoreId = storeId,
                        EntryType = SparkLedgerEntryType.StablecoinReceive,
                        AmountMsat = 0,
                        FeeMsat = 0,
                        OccurredAt = quote.SettledAt ?? quote.CreditedAt!.Value,
                        RecordedAt = quote.CreditedAt!.Value,
                        SdkPaymentId = quote.SdkPaymentId,
                        BtcPayInvoiceId = quote.InvoiceId,
                        StablecoinQuoteId = quote.Id,
                        StablecoinChain = quote.Chain,
                        StablecoinAsset = quote.Asset,
                        StablecoinPaidBaseUnits = quote.PaidBaseUnits,
                        StablecoinExternalTxHash = quote.ExternalTxHash,
                        IsBackfill = true,
                    }, cancellationToken).ConfigureAwait(false);
                }
                created++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex,
                    "Store {StoreId}: stablecoin backfill failed for quote {QuoteId}",
                    storeId, quote.Id);
            }
        }

        return new BackfillCount(skipped, created, failed);
    }

    // OutgoingPaymentRecord does not persist AmountSats or FeeSats; recovering those from the SDK
    // would require live SDK queries and is out of scope for the initial backfill. Sends made after
    // this ledger was added are written at the SparkSendPaymentService.SendAsync write point.
    private static Task<BackfillCount> BackfillSendsAsync(
        string storeId, bool dryRun, CancellationToken cancellationToken) =>
        Task.FromResult(BackfillCount.Zero);

    private async Task<BackfillCount> BackfillSweepsAsync(
        string storeId, bool dryRun, CancellationToken cancellationToken)
    {
        var skipped = 0;
        var created = 0;
        var failed = 0;

        await using var context = dbFactory.CreateContext();
        var records = await context.SweepRecords
            .AsNoTracking()
            .Where(r => r.StoreId == storeId
                && (r.Status == SweepRecordStatus.Sent || r.Status == SweepRecordStatus.Confirmed))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var record in records)
        {
            try
            {
                var exists = await ledger.ExistsForSdkPaymentAsync(storeId, record.IdempotencyKey, cancellationToken)
                    .ConfigureAwait(false);
                if (exists)
                {
                    skipped++;
                    continue;
                }

                if (!dryRun)
                {
                    await ledger.AppendAsync(new SparkLedgerEntry
                    {
                        Id = Guid.NewGuid().ToString(),
                        StoreId = storeId,
                        EntryType = SparkLedgerEntryType.Sweep,
                        AmountMsat = record.AmountSats * 1000L,
                        FeeMsat = (record.FeeSats ?? 0) * 1000L,
                        OccurredAt = record.CompletedAt ?? record.CreatedAt,
                        RecordedAt = record.CompletedAt ?? record.CreatedAt,
                        SdkPaymentId = record.IdempotencyKey,
                        IsBackfill = true,
                    }, cancellationToken).ConfigureAwait(false);
                }
                created++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex,
                    "Store {StoreId}: sweep backfill failed for {IdempotencyKey}",
                    storeId, record.IdempotencyKey);
            }
        }

        return new BackfillCount(skipped, created, failed);
    }

    private async Task<BackfillCount> BackfillUnilateralExitsAsync(
        string storeId, bool dryRun, CancellationToken cancellationToken)
    {
        var skipped = 0;
        var created = 0;
        var failed = 0;

        await using var context = dbFactory.CreateContext();
        var records = await context.UnilateralExitRecords
            .AsNoTracking()
            .Where(r => r.StoreId == storeId && r.Status == UnilateralExitStatus.Completed)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // No SdkPaymentId for unilateral exits; check by time + amount to avoid duplicates.
        var existingExits = await context.SparkLedger
            .AsNoTracking()
            .Where(e => e.StoreId == storeId && e.EntryType == SparkLedgerEntryType.UnilateralExit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var record in records)
        {
            try
            {
                var exists = existingExits.Exists(e =>
                    e.OccurredAt == record.UpdatedUtc
                    && e.AmountMsat == record.RecoverableValueSat * 1000L);
                if (exists)
                {
                    skipped++;
                    continue;
                }

                if (!dryRun)
                {
                    await ledger.AppendAsync(new SparkLedgerEntry
                    {
                        Id = Guid.NewGuid().ToString(),
                        StoreId = storeId,
                        EntryType = SparkLedgerEntryType.UnilateralExit,
                        AmountMsat = record.RecoverableValueSat * 1000L,
                        FeeMsat = record.TotalFeeSat * 1000L,
                        OccurredAt = record.UpdatedUtc,
                        RecordedAt = record.UpdatedUtc,
                        IsBackfill = true,
                    }, cancellationToken).ConfigureAwait(false);
                }
                created++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex,
                    "Store {StoreId}: unilateral exit backfill failed for record {RecordId}",
                    storeId, record.Id);
            }
        }

        return new BackfillCount(skipped, created, failed);
    }
}
