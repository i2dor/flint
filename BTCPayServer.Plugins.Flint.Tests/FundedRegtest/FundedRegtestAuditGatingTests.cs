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
/// secret material, and <c>preimage-audit.md</c> prints fingerprints instead of values — for preimage-kind
/// rows always, and for any row whose source file was withheld. Those decisions are pure functions on the
/// fixture
/// (<see cref="FundedRegtestWallet.AuditValue"/>, <c>AuditContext</c>, <c>AuditCell</c>, <c>HexRunRow</c>,
/// <c>ValueIsWithheld</c>, <c>SecretMaterialIn</c>) so they can be settled here, deterministically, without
/// a wallet — the funded collection itself stays gated on <c>SPARK_REGTEST_SEED</c> and money.
/// </para>
/// <para>
/// What each fact defends: a withheld source must never publish its raw values or context; a source that is
/// attached must keep publishing them verbatim (the artefact is evidence, not just an exclusion device); the
/// fingerprint must be the same one-way SHA-256 prefix the seed fingerprint uses, so a value fingerprint and
/// a verdict can be matched by hand; preimage-kind values are secret on sight — the fingerprint plus the
/// row's counts proves exactly what the value proved, so their rows print it whatever the sources hold —
/// while payment hashes and txids are public by design and redacting them would gut the table; and the
/// secret detectors must name seed-, preimage- and token-shaped text without a live wallet.
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
    public void Preimage_kind_values_are_fingerprinted_on_sight_and_public_identifiers_are_not()
    {
        // A recorded preimage is fingerprinted on its row whatever the state of the log sources: the
        // fingerprint plus the row's occurrence counts proves the same evidence property the verbatim value
        // would, so there is no source configuration under which publishing it buys the artefact anything
        // the withheld-risk does not outweigh.
        Assert.True(FundedRegtestWallet.ValueIsWithheld("preimage"));
        Assert.True(FundedRegtestWallet.ValueIsWithheld("Recovery preimage"));
        // Payment hashes, txids and idempotency keys are public identifiers: their rows print the value,
        // which is what lets a reader match the artefact against an invoice or a block explorer, and
        // withholding a file that happens to mention them says nothing about publishing them.
        Assert.False(FundedRegtestWallet.ValueIsWithheld("payment hash"));
        Assert.False(FundedRegtestWallet.ValueIsWithheld("sweep txid"));
        Assert.False(FundedRegtestWallet.ValueIsWithheld("sweep idempotency key"));
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

    // A deterministic fake seed phrase: twelve real BIP39 words, no more of a secret than the fake preimage.
    private const string FakeMnemonic =
        "abandon ability able about above absent absorb abstract absurd abuse access accident";

    [Fact]
    public void SecretMaterialIn_names_seed_shaped_preimage_shaped_and_token_shaped_text()
    {
        // Six or more consecutive mnemonic words is a seed leak even when the line is truncated.
        Assert.Equal(
            "the wallet seed",
            FundedRegtestWallet.SecretMaterialIn(
                "restoring wallet from word list: absent absorb abstract absurd abuse access accident",
                FakeMnemonic,
                []));

        // A recorded preimage value appearing anywhere in the text — no key name beside it required.
        Assert.Equal(
            "a payment preimage",
            FundedRegtestWallet.SecretMaterialIn(
                $"payment completed, opaque blob {FakePreimage} in payload",
                FakeMnemonic,
                [new FundedRegtestWallet.KnownIdentifier("preimage", FakePreimage)]));

        // The session_token name next to a value — the shape SparkLogScrubber redacts on.
        Assert.Equal(
            "the service provider's session token",
            FundedRegtestWallet.SecretMaterialIn(
                """{"data":{"session_token":"s3cr3t-session-value"}}""",
                FakeMnemonic,
                []));

        // The same hex and a txid as PUBLIC identifiers do not trip the gate.
        Assert.Null(
            FundedRegtestWallet.SecretMaterialIn(
                $"invoice paid hash={FakePreimage} txid=01020304050607080910",
                FakeMnemonic,
                [
                    new FundedRegtestWallet.KnownIdentifier("payment hash", FakePreimage),
                    new FundedRegtestWallet.KnownIdentifier("sweep txid", "01020304050607080910")
                ]));
    }

    [Fact]
    public void Secret_detectors_pass_null_empty_and_clean_text_through()
    {
        Assert.Null(FundedRegtestWallet.SecretMaterialIn(null, FakeMnemonic, []));
        Assert.Null(FundedRegtestWallet.SecretMaterialIn("", FakeMnemonic, []));
        Assert.Null(FundedRegtestWallet.SecretMaterialIn("a perfectly ordinary debug line", FakeMnemonic, []));
        Assert.False(FundedRegtestWallet.SeedAppearsIn("", FakeMnemonic));
        Assert.False(FundedRegtestWallet.SeedAppearsIn("a perfectly ordinary debug line", FakeMnemonic));
        Assert.False(FundedRegtestWallet.SessionTokenAppearsIn(""));
        Assert.False(FundedRegtestWallet.SessionTokenAppearsIn("session_timeout reached, nothing to redact"));
    }
}
