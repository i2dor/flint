using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests.FundedRegtest;

/// <summary>
/// The redaction decisions the funded-run audit makes before it publishes anything.
/// </summary>
/// <remarks>
/// <para>
/// The funded suite withholds <c>forwarded.log</c> and <c>sdk.log</c> from the run artefacts when they carry
/// secret material, and prints fingerprints instead of values in <c>preimage-audit.md</c> where a withheld
/// source is what carries them. Those decisions are pure functions on the fixture
/// (<see cref="FundedRegtestWallet.AuditValue"/>, <c>AuditContext</c>, <c>AuditCell</c>, <c>HexRunRow</c>,
/// <c>ValueIsWithheld</c>) so they can be settled here, deterministically, without a wallet — the funded
/// collection itself stays gated on <c>SPARK_REGTEST_SEED</c> and money.
/// </para>
/// <para>
/// What each fact defends: a withheld source must never publish its raw values or context; a source that is
/// attached must keep publishing them verbatim (the artefact is evidence, not just an exclusion device); the
/// fingerprint must be the same one-way SHA-256 prefix the seed fingerprint uses, so a value fingerprint and
/// a verdict can be matched by hand; and only preimage-kind values are treated as secret material — payment
/// hashes and txids are public by design and redacting them would gut the table.
/// </para>
/// </remarks>
public class FundedRegtestAuditGatingTests
{
    // A well-formed 64-hex run that is not any real key — just distinct hex with the shape the tables key on.
    private const string FakePreimage =
        "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private static string ExpectedFingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();

    [Fact]
    public void A_withheld_source_prints_the_fingerprint_of_a_preimage_row_not_its_value()
    {
        var runs = FundedRegtestWallet.DistinctHexRuns($"payment preimage {FakePreimage} accepted");
        var run = Assert.Single(runs);
        Assert.Equal(FakePreimage, run.Value, ignoreCase: true);

        var row = FundedRegtestWallet.HexRunRow("sdk.log", "PREIMAGE", run, sourceWithheld: true);

        Assert.Contains(ExpectedFingerprint(FakePreimage), row, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FakePreimage, row, StringComparison.OrdinalIgnoreCase);
        // The context is withheld too: "payment preimage" beside a fingerprint would name the class of the
        // withheld value for every row at once.
        Assert.DoesNotContain("payment preimage", row, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redacted", row, StringComparison.OrdinalIgnoreCase);
        // The classification survives — telling a reader a run IS a preimage is the point of the table.
        Assert.Contains("PREIMAGE", row, StringComparison.Ordinal);
        Assert.StartsWith("| sdk.log |", row, StringComparison.Ordinal);
    }

    [Fact]
    public void An_attached_source_row_still_prints_the_value_and_the_context_verbatim()
    {
        var runs = FundedRegtestWallet.DistinctHexRuns($"payment preimage {FakePreimage} accepted");
        var run = Assert.Single(runs);

        var row = FundedRegtestWallet.HexRunRow("forwarded", "PREIMAGE", run, sourceWithheld: false);

        Assert.Contains(FakePreimage, row, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("payment preimage", row, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("redacted", row, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_withheld_forwarded_column_prints_the_word_withheld_instead_of_a_count()
    {
        var cell = FundedRegtestWallet.AuditCell(true, "0");

        Assert.Equal("*(withheld)*", cell);
        Assert.False(cell.Any(char.IsDigit), "a withheld cell leaked a number");
        Assert.Equal("3", FundedRegtestWallet.AuditCell(false, "3"));
    }

    [Fact]
    public void Only_a_preimage_carried_by_a_withheld_source_is_fingerprinted()
    {
        // A recorded preimage present in the withheld raw log: fingerprint it.
        Assert.True(FundedRegtestWallet.ValueIsWithheld("preimage", rawWithheld: true, 1, false, 0));
        // Present in the withheld forwarded log instead: same answer.
        Assert.True(FundedRegtestWallet.ValueIsWithheld("preimage", false, 0, forwardedWithheld: true, 2));
        // A preimage neither withheld file carries — the clean case the tests aim for — still prints its
        // value: nothing is being withheld for it, and the value is what proves the count columns mean
        // something.
        Assert.False(FundedRegtestWallet.ValueIsWithheld("preimage", rawWithheld: true, 0, false, 0));
        // Payment hashes and txids are public identifiers: withholding a file that mentions them says nothing
        // about printing the hash in the table.
        Assert.False(FundedRegtestWallet.ValueIsWithheld("payment hash", rawWithheld: true, 5, false, 0));
        Assert.False(FundedRegtestWallet.ValueIsWithheld("txid", false, 0, forwardedWithheld: true, 1));
        // A raw log that is attached may carry the preimage's hash with no fingerprinting.
        Assert.False(FundedRegtestWallet.ValueIsWithheld("preimage", rawWithheld: false, 0, false, 0));
    }

    [Fact]
    public void Fingerprints_use_the_same_one_way_SHA256_prefix_as_the_seed_fingerprint()
    {
        var cell = FundedRegtestWallet.AuditValue(true, FakePreimage);

        Assert.Equal($"`{ExpectedFingerprint(FakePreimage)}`", cell);
        Assert.Equal($"`{FakePreimage}`", FundedRegtestWallet.AuditValue(false, FakePreimage));
    }

    [Fact]
    public void An_attached_context_keeps_its_markdown_escaping_and_a_withheld_one_carries_nothing()
    {
        // Pipe escaped for the table, backtick swapped for a quote — the same escaping the attached table
        // rows have always used, so gating does not change what an unwithheld row looks like.
        Assert.Equal("`pay \\| invoice '123`", FundedRegtestWallet.AuditContext(false, "pay | invoice `123"));
        Assert.Equal(
            "*(redacted — source withheld)*",
            FundedRegtestWallet.AuditContext(true, "pay | invoice `123"));
    }
}
