using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.Flint.Data;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BTCPayServer.Plugins.Flint.Models;

/// <summary>Row returned by <see cref="Services.SparkLedgerQueryService"/>.</summary>
public record SparkLedgerRow(SparkLedgerEntry Entry, int? StablecoinDecimals);

/// <summary>Aggregated totals over the filtered period.</summary>
public record SparkLedgerTotals(long TotalInMsat = 0, long TotalOutMsat = 0, long TotalFeeMsat = 0)
{
    public long NetMsat => TotalInMsat - TotalOutMsat - TotalFeeMsat;
}

/// <summary>Filter parameters for <see cref="Services.SparkLedgerQueryService"/>.</summary>
public class SparkLedgerFilters
{
    /// <summary>"in", "out", or null for both.</summary>
    public string? Direction { get; set; }

    /// <summary>"lightning", "stablecoin", "onchain", "sweep", "send", "exit", or null for all.</summary>
    public string? Type { get; set; }

    /// <summary>Asset symbol filter, e.g. "USDC".</summary>
    public string? Asset { get; set; }

    /// <summary>Chain filter, e.g. "ethereum".</summary>
    public string? Chain { get; set; }

    /// <summary>0 = all time; positive = number of days back from now.</summary>
    public int Period { get; set; }
}

/// <summary>One page of ledger entries plus totals and pending quotes.</summary>
public record SparkLedgerPage(
    IReadOnlyList<SparkLedgerRow> Rows,
    SparkLedgerTotals Totals,
    IReadOnlyList<StablecoinQuote> PendingQuotes,
    int Skip,
    int PageSize,
    bool HasMore,
    long TotalCount);

/// <summary>ViewModel for the Wallet History page.</summary>
public class SparkLedgerHistoryViewModel
{
    [BindNever]
    public string StoreId { get; set; } = "";

    public IReadOnlyList<SparkLedgerRow> Rows { get; set; } = [];
    public SparkLedgerTotals Totals { get; set; } = new();
    public IReadOnlyList<StablecoinQuote> PendingQuotes { get; set; } = [];

    public SparkLedgerFilters Filters { get; set; } = new();

    public int Skip { get; set; }
    public int PageSize { get; set; } = 50;
    public bool HasMore { get; set; }
    public long TotalCount { get; set; }

    /// <summary>True when the current user has CanModifyStoreSettings; shows the CSV export button.</summary>
    public bool CanExportCsv { get; set; }
}
