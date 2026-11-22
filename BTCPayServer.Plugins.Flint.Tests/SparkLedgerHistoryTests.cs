using System.Globalization;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Services;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Unit tests for the pure helpers in <see cref="SparkLedgerQueryService"/>:
/// stablecoin amount formatting, CSV injection sanitisation, TX hash validation,
/// chain display names, and credit/debit classification.
/// </summary>
public class SparkLedgerHistoryTests
{
    // ---- FormatStablecoinAmount ------------------------------------------------

    [Fact]
    public void FormatStablecoinAmount_formats_6_decimal_usdc_ethereum()
    {
        // 49111281 base units at 6 decimals = 49.111281 USDC
        var result = SparkLedgerQueryService.FormatStablecoinAmount(
            "49111281", "USDC", "ethereum", decimals: 6);

        Assert.Equal("49.111281 USDC · Ethereum", result);
    }

    [Fact]
    public void FormatStablecoinAmount_formats_18_decimal_usdt_bsc()
    {
        // 10087341000000000000 base units at 18 decimals = 10.087341 USDT
        var result = SparkLedgerQueryService.FormatStablecoinAmount(
            "10087341000000000000", "USDT", "bsc", decimals: 18);

        Assert.Equal("10.087341 USDT · BNB Chain", result);
    }

    [Fact]
    public void FormatStablecoinAmount_returns_null_when_base_units_null()
    {
        var result = SparkLedgerQueryService.FormatStablecoinAmount(null, "USDC", "ethereum", 6);
        Assert.Null(result);
    }

    [Fact]
    public void FormatStablecoinAmount_returns_null_when_asset_null()
    {
        var result = SparkLedgerQueryService.FormatStablecoinAmount("1000000", null, "ethereum", 6);
        Assert.Null(result);
    }

    [Fact]
    public void FormatStablecoinAmount_uppercases_asset_symbol()
    {
        var result = SparkLedgerQueryService.FormatStablecoinAmount("1000000", "usdc", "base", 6);
        Assert.Contains("USDC", result!);
    }

    // ---- SanitizeCsvField (CSV injection protection) --------------------------

    [Theory]
    [InlineData("=1+1",  "'=1+1")]
    [InlineData("+cmd",  "'+cmd")]
    [InlineData("-1",    "'-1")]
    [InlineData("@SUM",  "'@SUM")]
    [InlineData("\tSUM", "'\tSUM")]
    public void SanitizeCsvField_prefixes_injection_chars(string input, string prefixedValue)
    {
        // The whole field is wrapped in quotes, so expected = "'" + prefixedValue + "'"
        var result = SparkLedgerQueryService.SanitizeCsvField(input);
        Assert.Equal("\"" + prefixedValue + "\"", result);
    }

    [Fact]
    public void SanitizeCsvField_does_not_prefix_safe_field()
    {
        // A regular description must not be modified.
        var result = SparkLedgerQueryService.SanitizeCsvField("Order #1234");
        Assert.Equal("\"Order #1234\"", result);
    }

    [Fact]
    public void SanitizeCsvField_escapes_embedded_double_quotes()
    {
        // A field with a " must have it doubled inside CSV quotes.
        var result = SparkLedgerQueryService.SanitizeCsvField("say \"hi\"");
        Assert.Equal("\"say \"\"hi\"\"\"", result);
    }

    [Fact]
    public void SanitizeCsvField_handles_null_and_empty()
    {
        Assert.Equal("\"\"", SparkLedgerQueryService.SanitizeCsvField(null));
        Assert.Equal("\"\"", SparkLedgerQueryService.SanitizeCsvField(""));
    }

    // ---- IsValidTxHash -------------------------------------------------------

    [Theory]
    [InlineData("0a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b", "ethereum", true)]
    [InlineData("0x0a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b", "ethereum", true)]
    [InlineData("0a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b", "base", true)]
    [InlineData("short", "ethereum", false)]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", "ethereum", false)]
    public void IsValidTxHash_validates_evm_hashes(string hash, string chain, bool expected)
    {
        Assert.Equal(expected, SparkLedgerQueryService.IsValidTxHash(hash, chain));
    }

    [Theory]
    [InlineData("5pHMea3bCBsLv7q9pHGCYmqN3gVGTmyMm8mJNpn3D7yKUwt3D8yZPUJT2GbSJ1GFGz1r5GZMECzrL12M3Ej4WVP", "solana", true)]
    [InlineData("short", "solana", false)]
    public void IsValidTxHash_validates_solana_hashes(string hash, string chain, bool expected)
    {
        Assert.Equal(expected, SparkLedgerQueryService.IsValidTxHash(hash, chain));
    }

    // ---- GetExplorerUrl ------------------------------------------------------

    [Fact]
    public void GetExplorerUrl_returns_etherscan_link_for_valid_evm_hash()
    {
        var hash = "0a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b";
        var url  = SparkLedgerQueryService.GetExplorerUrl(hash, "ethereum");
        Assert.Equal("https://etherscan.io/tx/" + hash, url);
    }

    [Fact]
    public void GetExplorerUrl_returns_null_for_invalid_hash()
    {
        var url = SparkLedgerQueryService.GetExplorerUrl("bad-hash", "ethereum");
        Assert.Null(url);
    }

    [Fact]
    public void GetExplorerUrl_returns_null_for_unknown_chain()
    {
        var hash = "0a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b";
        var url  = SparkLedgerQueryService.GetExplorerUrl(hash, "unknownchain");
        Assert.Null(url);
    }

    [Fact]
    public void GetExplorerUrl_returns_null_when_hash_null()
    {
        Assert.Null(SparkLedgerQueryService.GetExplorerUrl(null, "ethereum"));
    }

    // ---- IsCredit ------------------------------------------------------------

    [Theory]
    [InlineData(SparkLedgerEntryType.LightningReceive,  true)]
    [InlineData(SparkLedgerEntryType.StablecoinReceive, true)]
    [InlineData(SparkLedgerEntryType.OnchainDeposit,    true)]
    [InlineData(SparkLedgerEntryType.Send,              false)]
    [InlineData(SparkLedgerEntryType.Sweep,             false)]
    [InlineData(SparkLedgerEntryType.UnilateralExit,    false)]
    public void IsCredit_classifies_entry_types(SparkLedgerEntryType type, bool expected)
    {
        Assert.Equal(expected, SparkLedgerQueryService.IsCredit(type));
    }

    // ---- ChainDisplayName ----------------------------------------------------

    [Theory]
    [InlineData("ethereum", "Ethereum")]
    [InlineData("base",     "Base")]
    [InlineData("bsc",      "BNB Chain")]
    [InlineData("solana",   "Solana")]
    [InlineData("tron",     "Tron")]
    [InlineData("polygon",  "Polygon")]
    [InlineData("ETHEREUM", "Ethereum")]  // case-insensitive
    public void ChainDisplayName_returns_known_name(string chain, string expected)
    {
        Assert.Equal(expected, SparkLedgerQueryService.ChainDisplayName(chain));
    }

    [Fact]
    public void ChainDisplayName_returns_original_for_unknown_chain()
    {
        Assert.Equal("newchain", SparkLedgerQueryService.ChainDisplayName("newchain"));
    }
}
