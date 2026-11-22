using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Flint.Services;

public sealed class SparkLedgerQueryService
{
    // Open-quote window: quotes expire and then the SDK probes for 24h past expiry.
    private static readonly TimeSpan PendingMatchWindow = TimeSpan.FromDays(1);

    private static readonly IReadOnlyDictionary<string, string> ExplorerBase =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ethereum"] = "https://etherscan.io/tx/",
            ["base"]     = "https://basescan.org/tx/",
            ["polygon"]  = "https://polygonscan.com/tx/",
            ["bsc"]      = "https://bscscan.com/tx/",
            ["tron"]     = "https://tronscan.org/#/transaction/",
            ["solana"]   = "https://solscan.io/tx/",
            ["bitcoin"]  = "https://mempool.space/tx/",
        };

    private static readonly Regex EvmHexHash    = new(@"^(0x)?[0-9a-fA-F]{64}$", RegexOptions.Compiled);
    private static readonly Regex SolanaBase58   = new(@"^[1-9A-HJ-NP-Za-km-z]{87,88}$", RegexOptions.Compiled);

    private readonly SparkPluginDbContextFactory _factory;
    private readonly InvoiceRepository _invoiceRepository;

    public SparkLedgerQueryService(SparkPluginDbContextFactory factory, InvoiceRepository invoiceRepository)
    {
        _factory = factory;
        _invoiceRepository = invoiceRepository;
    }

    // ---- Query -----------------------------------------------------------------

    public async Task<SparkLedgerPage> QueryAsync(
        string storeId,
        SparkLedgerFilters filters,
        int skip,
        int pageSize,
        CancellationToken ct = default)
    {
        await using var ctx = _factory.CreateContext();

        var q = BuildBaseQuery(ctx, storeId, filters);

        var total = await q.LongCountAsync(ct).ConfigureAwait(false);
        var entries = await q
            .OrderByDescending(e => e.OccurredAt)
            .Skip(skip)
            .Take(pageSize + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = entries.Count > pageSize;
        if (hasMore) entries.RemoveAt(entries.Count - 1);

        List<SparkLedgerRow> rows = [.. (await AttachDecimalsAsync(ctx, entries, ct).ConfigureAwait(false))];

        // Attach WC order IDs from BTCPay invoice metadata (one batch query).
        var invoiceIds = rows
            .Select(r => r.Entry.BtcPayInvoiceId)
            .Where(id => id is not null)
            .Cast<string>()
            .Distinct()
            .ToArray();

        if (invoiceIds.Length > 0)
        {
            var invoices = await _invoiceRepository.GetInvoices(new InvoiceQuery
            {
                InvoiceId = invoiceIds,
                StoreId   = [storeId],
            }, ct).ConfigureAwait(false);

            var orderIdByInvoiceId = invoices
                .Where(inv => inv.Metadata.OrderId is not null)
                .ToDictionary(inv => inv.Id, inv => inv.Metadata.OrderId!);

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.Entry.BtcPayInvoiceId is not null
                    && orderIdByInvoiceId.TryGetValue(r.Entry.BtcPayInvoiceId, out var oid))
                    rows[i] = r with { WcOrderId = oid };
            }
        }

        // Totals use the same filters but without pagination.
        var totals = await ComputeTotalsAsync(ctx, storeId, filters, ct).ConfigureAwait(false);

        // Stablecoin totals per asset/chain for the current filter.
        var stablecoinTotals = await ComputeStablecoinTotalsAsync(ctx, storeId, filters, ct).ConfigureAwait(false);

        // Available assets/chains across the whole store (for filter buttons).
        var (availableAssets, availableChains) = await GetAvailableFiltersAsync(ctx, storeId, ct).ConfigureAwait(false);

        // Pending: unsettled stablecoin quotes still within the match window.
        var window = DateTimeOffset.UtcNow - PendingMatchWindow;
        var pending = await ctx.StablecoinQuotes
            .Where(q2 => q2.StoreId == storeId && q2.SdkPaymentId == null && q2.ExpiresAt > window)
            .OrderByDescending(q2 => q2.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new SparkLedgerPage(rows, totals, pending, skip, pageSize, hasMore, total,
            stablecoinTotals, availableAssets, availableChains);
    }

    // ---- CSV export ------------------------------------------------------------

    public async Task WriteCsvAsync(
        string storeId,
        SparkLedgerFilters filters,
        TextWriter writer,
        CancellationToken ct = default)
    {
        await using var ctx = _factory.CreateContext();

        var q = BuildBaseQuery(ctx, storeId, filters)
            .OrderByDescending(e => e.OccurredAt);

        // Stream in batches to avoid one giant allocation.
        const int batch = 500;
        var skip = 0;

        await writer.WriteLineAsync(CsvHeader).ConfigureAwait(false);

        while (true)
        {
            var entries = await q.Skip(skip).Take(batch).ToListAsync(ct).ConfigureAwait(false);
            if (entries.Count == 0) break;

            var rows = await AttachDecimalsAsync(ctx, entries, ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                await writer.WriteLineAsync(ToCsvRow(row)).ConfigureAwait(false);
            }

            skip += entries.Count;
            if (entries.Count < batch) break;
        }
    }

    // ---- Helpers (internal so tests can reach them without InternalsVisibleTo overhead) ----

    internal static bool IsCredit(SparkLedgerEntryType t) => t is
        SparkLedgerEntryType.LightningReceive or
        SparkLedgerEntryType.StablecoinReceive or
        SparkLedgerEntryType.OnchainDeposit;

    /// <summary>
    /// Formats stablecoin base units using the asset's decimal precision.
    /// Returns e.g. "49.111281 USDC · Ethereum".
    /// Returns null when <paramref name="paidBaseUnits"/> is null.
    /// </summary>
    internal static string? FormatStablecoinAmount(string? paidBaseUnits, string? asset, string? chain, int decimals)
    {
        if (paidBaseUnits is null || asset is null) return null;
        if (!BigInteger.TryParse(paidBaseUnits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var units))
            return null;

        var formatted = StablecoinAmounts.Format(units, decimals);
        var chainDisplay = ChainDisplayName(chain);
        return chainDisplay is not null
            ? $"{formatted} {asset.ToUpperInvariant()} · {chainDisplay}"
            : $"{formatted} {asset.ToUpperInvariant()}";
    }

    internal static string ChainDisplayName(string? chain) => chain?.ToLowerInvariant() switch
    {
        "ethereum" => "Ethereum",
        "base"     => "Base",
        "polygon"  => "Polygon",
        "bsc"      => "BNB Chain",
        "tron"     => "Tron",
        "solana"   => "Solana",
        "bitcoin"  => "Bitcoin",
        "hypercore" => "HyperCore",
        _          => chain ?? "",
    };

    internal static string? GetExplorerUrl(string? txHash, string? chain)
    {
        if (txHash is null || chain is null) return null;
        if (!ExplorerBase.TryGetValue(chain, out var baseUrl)) return null;
        if (!IsValidTxHash(txHash, chain)) return null;
        return baseUrl + txHash;
    }

    internal static bool IsValidTxHash(string hash, string chain) =>
        chain.ToLowerInvariant() switch
        {
            "solana" => SolanaBase58.IsMatch(hash),
            _        => EvmHexHash.IsMatch(hash),
        };

    /// <summary>
    /// Prefixes fields that could be interpreted as spreadsheet formulas.
    /// </summary>
    internal static string SanitizeCsvField(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        // Wrap in quotes and escape embedded quotes.
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    // ---- Privates --------------------------------------------------------------

    private static IQueryable<SparkLedgerEntry> BuildBaseQuery(
        SparkPluginDbContext ctx,
        string storeId,
        SparkLedgerFilters f)
    {
        var q = ctx.SparkLedger.Where(e => e.StoreId == storeId);

        // Direction filter.
        if (f.Direction == "in")
            q = q.Where(e => e.EntryType == SparkLedgerEntryType.LightningReceive
                           || e.EntryType == SparkLedgerEntryType.StablecoinReceive
                           || e.EntryType == SparkLedgerEntryType.OnchainDeposit);
        else if (f.Direction == "out")
            q = q.Where(e => e.EntryType == SparkLedgerEntryType.Send
                           || e.EntryType == SparkLedgerEntryType.Sweep
                           || e.EntryType == SparkLedgerEntryType.UnilateralExit);

        // Type filter.
        if (f.Type is not null)
        {
            q = f.Type switch
            {
                "lightning"   => q.Where(e => e.EntryType == SparkLedgerEntryType.LightningReceive),
                "stablecoin"  => q.Where(e => e.EntryType == SparkLedgerEntryType.StablecoinReceive),
                "onchain"     => q.Where(e => e.EntryType == SparkLedgerEntryType.OnchainDeposit),
                "sweep"       => q.Where(e => e.EntryType == SparkLedgerEntryType.Sweep),
                "send"        => q.Where(e => e.EntryType == SparkLedgerEntryType.Send),
                "exit"        => q.Where(e => e.EntryType == SparkLedgerEntryType.UnilateralExit),
                _             => q,
            };
        }

        // Asset + chain filter (stablecoin only).
        if (f.Asset is not null)
            q = q.Where(e => e.StablecoinAsset == f.Asset);
        if (f.Chain is not null)
            q = q.Where(e => e.StablecoinChain == f.Chain);

        // Period filter.
        if (f.Period > 0)
        {
            var from = DateTimeOffset.UtcNow.AddDays(-f.Period);
            q = q.Where(e => e.OccurredAt >= from);
        }

        return q;
    }

    private static async Task<SparkLedgerTotals> ComputeTotalsAsync(
        SparkPluginDbContext ctx,
        string storeId,
        SparkLedgerFilters filters,
        CancellationToken ct)
    {
        var q = BuildBaseQuery(ctx, storeId, filters);

        // EF translates this grouping into a single aggregation query.
        var agg = await q
            .GroupBy(_ => 1)
            .Select(g => new
            {
                InMsat  = g.Where(e => e.EntryType == SparkLedgerEntryType.LightningReceive
                                     || e.EntryType == SparkLedgerEntryType.StablecoinReceive
                                     || e.EntryType == SparkLedgerEntryType.OnchainDeposit)
                            .Sum(e => (long?)e.AmountMsat) ?? 0L,
                OutMsat = g.Where(e => e.EntryType == SparkLedgerEntryType.Send
                                     || e.EntryType == SparkLedgerEntryType.Sweep
                                     || e.EntryType == SparkLedgerEntryType.UnilateralExit)
                            .Sum(e => (long?)e.AmountMsat) ?? 0L,
                FeeMsat = g.Sum(e => (long?)e.FeeMsat) ?? 0L,
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (agg is null) return new SparkLedgerTotals();
        return new SparkLedgerTotals(agg.InMsat, agg.OutMsat, agg.FeeMsat);
    }

    private static async Task<IReadOnlyList<StablecoinAssetTotal>> ComputeStablecoinTotalsAsync(
        SparkPluginDbContext ctx,
        string storeId,
        SparkLedgerFilters filters,
        CancellationToken ct)
    {
        var rows = await BuildBaseQuery(ctx, storeId, filters)
            .Where(e => e.EntryType == SparkLedgerEntryType.StablecoinReceive
                     && e.StablecoinPaidBaseUnits != null
                     && e.StablecoinAsset != null
                     && e.StablecoinChain != null
                     && e.StablecoinQuoteId != null)
            .Select(e => new
            {
                e.StablecoinAsset,
                e.StablecoinChain,
                e.StablecoinPaidBaseUnits,
                e.StablecoinQuoteId,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0) return [];

        var quoteIds = rows.Select(r => r.StablecoinQuoteId!).Distinct().ToList();
        var decimalsMap = await ctx.StablecoinQuotes
            .Where(q => quoteIds.Contains(q.Id))
            .ToDictionaryAsync(q => q.Id, q => q.Decimals, ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => (r.StablecoinAsset!.ToUpperInvariant(), r.StablecoinChain!.ToLowerInvariant()))
            .Select(g =>
            {
                var asset = g.Key.Item1;
                var chain = g.Key.Item2;
                var dec   = g.Select(r => decimalsMap.TryGetValue(r.StablecoinQuoteId!, out var d) ? d : 6)
                             .FirstOrDefault(d => d != 0, 6);
                var total = g.Aggregate(BigInteger.Zero, (sum, r) =>
                    BigInteger.TryParse(r.StablecoinPaidBaseUnits, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var units) ? sum + units : sum);
                var formatted = FormatStablecoinAmount(total.ToString(CultureInfo.InvariantCulture), asset, chain, dec)
                                ?? $"{total} {asset}";
                return new StablecoinAssetTotal(asset, chain, formatted);
            })
            .OrderBy(st => st.Asset).ThenBy(st => st.Chain)
            .ToList();
    }

    private static async Task<(IReadOnlyList<string> Assets, IReadOnlyList<string> Chains)> GetAvailableFiltersAsync(
        SparkPluginDbContext ctx,
        string storeId,
        CancellationToken ct)
    {
        var pairs = await ctx.SparkLedger
            .Where(e => e.StoreId == storeId
                     && e.EntryType == SparkLedgerEntryType.StablecoinReceive
                     && e.StablecoinAsset != null
                     && e.StablecoinChain != null)
            .Select(e => new { e.StablecoinAsset, e.StablecoinChain })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var assets = pairs.Select(p => p.StablecoinAsset!.ToUpperInvariant()).Distinct().OrderBy(a => a).ToList<string>();
        var chains = pairs.Select(p => p.StablecoinChain!.ToLowerInvariant()).Distinct().OrderBy(c => c).ToList<string>();

        return (assets, chains);
    }

    private static async Task<IReadOnlyList<SparkLedgerRow>> AttachDecimalsAsync(
        SparkPluginDbContext ctx,
        IReadOnlyList<SparkLedgerEntry> entries,
        CancellationToken ct)
    {
        // One extra query to fetch quote decimals for stablecoin rows.
        var quoteIds = entries
            .Where(e => e.StablecoinQuoteId is not null)
            .Select(e => e.StablecoinQuoteId!)
            .Distinct()
            .ToList();

        Dictionary<string, int> decimals = [];
        if (quoteIds.Count > 0)
        {
            decimals = await ctx.StablecoinQuotes
                .Where(q => quoteIds.Contains(q.Id))
                .ToDictionaryAsync(q => q.Id, q => q.Decimals, ct)
                .ConfigureAwait(false);
        }

        return entries
            .Select(e =>
            {
                var d = e.StablecoinQuoteId is not null && decimals.TryGetValue(e.StablecoinQuoteId, out var dec)
                    ? dec
                    : (int?)null;
                return new SparkLedgerRow(e, d);
            })
            .ToList();
    }

    // ---- CSV -------------------------------------------------------------------

    private const string CsvHeader =
        "Date (UTC),Type,Direction,Amount (sat),Fee (sat),BTC," +
        "Stablecoin Amount,Asset,Chain,Invoice Rate (EUR),Invoice Rate (RON)," +
        "BTCPay Invoice,SdkPaymentId,PaymentHash,Description";

    private static string ToCsvRow(SparkLedgerRow row)
    {
        var e = row.Entry;
        var amtSat  = e.AmountMsat / 1000m;
        var feeSat  = e.FeeMsat   / 1000m;
        var btcAmt  = e.AmountMsat / 100_000_000_000m;
        var dir     = IsCredit(e.EntryType) ? "IN" : "OUT";
        var typeName = e.EntryType switch
        {
            SparkLedgerEntryType.LightningReceive  => "Lightning",
            SparkLedgerEntryType.StablecoinReceive => "Stablecoin",
            SparkLedgerEntryType.OnchainDeposit    => "On-chain",
            SparkLedgerEntryType.Send              => "Send",
            SparkLedgerEntryType.Sweep             => "Sweep",
            SparkLedgerEntryType.UnilateralExit    => "Exit",
            _ => e.EntryType.ToString(),
        };

        string stableAmt = "";
        if (e.StablecoinPaidBaseUnits is not null && row.StablecoinDecimals is int dec)
        {
            if (BigInteger.TryParse(e.StablecoinPaidBaseUnits, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var units))
            {
                stableAmt = StablecoinAmounts.Format(units, dec);
            }
        }

        var fields = new string[]
        {
            e.OccurredAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            typeName,
            dir,
            amtSat.ToString("F0", CultureInfo.InvariantCulture),
            feeSat.ToString("F0", CultureInfo.InvariantCulture),
            btcAmt.ToString("F11", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.'),
            stableAmt,
            e.StablecoinAsset ?? "",
            e.StablecoinChain ?? "",
            e.InvoiceRateEur?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
            e.InvoiceRateRon?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
            e.BtcPayInvoiceId ?? "",
            e.SdkPaymentId ?? "",
            e.PaymentHash ?? "",
            e.Description ?? "",
        };

        var sb = new StringBuilder();
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(SanitizeCsvField(fields[i]));
        }
        return sb.ToString();
    }
}
