using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;
using SdkNetwork = Breez.Sdk.Spark.Network;

namespace BTCPayServer.Plugins.Flint.Tests.FundedRegtest;

/// <summary>
/// One connected, <b>funded</b> Lightspark-regtest wallet, shared by the whole funded suite.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Every other suite in this repository can be run by anyone who clones it. This one
/// cannot: it needs money. The unfunded probe in <c>SparkRegtestIntegrationTests</c> reaches the real service
/// provider but stops at the point where anything settles, so the paths that only exist after settlement — a
/// completed receive, a cooperative exit, the resolution of a record left in flight by a crash — have never run
/// against the real SDK at all. Neither have the log lines a completed payment emits — which is the one stated
/// gap in this plugin's log audit, since the wallet that audit was performed on was never funded and never
/// paid, so the lines a preimage would ride on were never produced.
/// </para>
/// <para>
/// <b>Gating.</b> Opt-in on <c>SPARK_REGTEST_SEED</c> holding the wallet's BIP39 mnemonic, exactly as
/// <c>SPARK_POSTGRES_TESTS</c> and <c>SPARK_INTEGRATION_TESTS</c> gate their suites. Absent, every test in the
/// collection skips and this fixture connects nothing.
/// </para>
/// <para>
/// <b>Drain, and how it is kept small.</b> A cooperative exit costs a flat fee — the amount does not change it —
/// so each sweep the suite performs burns roughly 2,000 sats whatever it moves. The principal is not burned: the
/// default sweep destination is <em>this wallet's own static deposit address</em>, so an exit sends the money to
/// an address the same wallet monitors and the SDK re-claims it on-chain afterwards. Set
/// <c>SPARK_REGTEST_SWEEP_ADDRESS</c> to override that if a self-directed exit ever stops being accepted; the
/// runbook in docs/testing.md says what to do then.
/// </para>
/// <para>
/// <b>Serialised.</b> One wallet, three tests that each move its money, and a balance that lags settlement by
/// ~20 s: these must not run concurrently. Hence a collection fixture rather than a class fixture, and hence
/// the collection declaration at the bottom of this file.
/// </para>
/// </remarks>
public sealed class FundedRegtestWallet : IAsyncLifetime
{
    public const string CollectionName = "Spark funded regtest";

    /// <summary>The BIP39 mnemonic of a funded Lightspark-regtest wallet. The gate for this whole suite.</summary>
    public const string SeedVariable = "SPARK_REGTEST_SEED";

    /// <summary>
    /// Where a cooperative exit sends to. Optional; defaults to this wallet's own deposit address so the
    /// principal comes back.
    /// </summary>
    public const string SweepAddressVariable = "SPARK_REGTEST_SWEEP_ADDRESS";

    /// <summary>Where the log-audit artefact is written. Optional; CI sets it so the upload step can find it.</summary>
    public const string ArtifactVariable = "SPARK_REGTEST_ARTIFACT_DIR";

    /// <summary>
    /// The floor below which the suite refuses to start, rather than failing later inside an assertion about
    /// something else.
    /// </summary>
    /// <remarks>
    /// Two cooperative exits at ~2,000 sats of fee each, 20,000 sats in flight per exit until the on-chain
    /// deposit is re-claimed, and a 2,000 sat self-payment. 100,000 leaves room for a run whose principal has
    /// not landed back yet.
    /// </remarks>
    public const long MinimumBalanceSats = 100_000;

    public static string SkipReason =>
        $"Set {SeedVariable} to the BIP39 mnemonic of a funded Lightspark-regtest wallet to run the funded "
        + "suite. See docs/testing.md, \"A funded regtest wallet for CI\", for how to make one and fund it.";

    /// <summary>
    /// The message a drained wallet fails with. Deliberately an instruction rather than an assertion about
    /// numbers: a maintainer reading a red CI run should not have to work out that 3,412 &lt; 100,000 means
    /// "go to the faucet".
    /// </summary>
    public static string TopUpMessage(long haveSats, long needSats, string what) =>
        $"The CI regtest wallet is out of money: {what} needs {needSats:N0} sats and the wallet holds "
        + $"{haveSats:N0}. TOP UP THE CI WALLET — the runbook is in docs/testing.md under \"A funded regtest "
        + "wallet for CI\"; run the `spark-regtest-wallet` workflow to print the deposit address, then send "
        + "regtest sats to it from https://app.lightspark.com/regtest-faucet. Nothing is wrong with the code.";

    private static string? RawSeed =>
        Environment.GetEnvironmentVariable(SeedVariable) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    public static bool IsEnabled => RawSeed is not null;

    private readonly List<KnownIdentifier> _identifiers = [];
    private string? _mnemonic;
    private string? _storageDirectory;
    private string? _logDirectory;
    private bool _loggingInstalled;

    /// <summary>The connected wallet. Only valid when <see cref="IsEnabled"/>.</summary>
    public ISparkSdkClient Sdk { get; private set; } = null!;

    /// <summary>A store id for the plugin-side collaborators. Not a real BTCPay store.</summary>
    public string StoreId => "funded-regtest";

    /// <summary>Everything the SDK's Rust subscriber forwarded through <see cref="SparkLogBridge"/>, scrubbed.</summary>
    public CapturingLogger<SparkLogBridge> ForwardedLog { get; } = new();

    /// <summary>The wallet's static Bitcoin deposit address.</summary>
    public string DepositAddress { get; private set; } = null!;

    /// <summary>Where a cooperative exit in this suite sends to.</summary>
    public string SweepDestination { get; private set; } = null!;

    /// <summary>A non-reversible fingerprint of the seed, so a run can be tied to a wallet without printing one.</summary>
    public string SeedFingerprint { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        if (RawSeed is not { } seed)
            return;

        // Fail on a malformed secret here rather than three layers down inside the SDK, where the message is
        // "@v1=…" garbage and the cause is not obvious.
        try
        {
            _ = new Mnemonic(seed, Wordlist.English);
        }
        catch (Exception ex)
        {
            Assert.Fail(
                $"{SeedVariable} is not a valid English BIP39 mnemonic ({ex.GetType().Name}). It must be the "
                + "space-separated word list and nothing else — no quotes, no trailing newline.");
        }

        _mnemonic = seed;
        SeedFingerprint = Fingerprint(seed);

        var root = Path.Combine(Path.GetTempPath(), "spark-funded-regtest", Guid.NewGuid().ToString("N"));
        _storageDirectory = Path.Combine(root, "storage");
        _logDirectory = Path.Combine(root, "logs");
        Directory.CreateDirectory(_storageDirectory);
        Directory.CreateDirectory(_logDirectory);

        // Installed before the connect, because the subscriber is process-global and one-shot: lines emitted
        // before it exists are gone, and the connect is where the SDK is loudest. "debug" rather than the
        // shipped Constants.SdkLogFilter of "info" on purpose — debug is the level Breez's production checklist
        // asks for and the level the plugin's own log audit was performed at, so it is the level the remaining
        // gap has to be closed at. ClampFilter still refuses anything mentioning trace, which is where the
        // session token lives; this suite does not try to talk it out of that.
        _loggingInstalled = SparkLogging.TryInitialise(_logDirectory, ForwardedLog, "debug");

        var events = Channel.CreateBounded<SparkEventEnvelope>(new BoundedChannelOptions(256));
        var factory = new SparkSdkClientFactory(
            new FixedStorageProvider(_storageDirectory),
            new NBitcoinBolt11Parser(Network.RegTest, NullLogger<NBitcoinBolt11Parser>.Instance),
            NullLoggerFactory.Instance);

        Sdk = await factory.ConnectAsync(
            new SparkConnectOptions(
                StoreId,
                seed,
                passphrase: null,
                apiKey: null,
                SdkNetwork.Regtest,
                // The SDK's own default is Rate(1 sat/vB), which is a cap rather than a bid and strands
                // deposits. This suite depends on deposits being claimed — that is how the principal of a
                // self-directed cooperative exit comes back — so it sets a deliberately generous ceiling.
                maxDepositClaimFee: new SparkMaxFee.Rate(50)),
            events.Writer);

        DepositAddress = await Sdk.GetBitcoinDepositAddressAsync();
        SweepDestination = Environment.GetEnvironmentVariable(SweepAddressVariable) is { } configured
            && !string.IsNullOrWhiteSpace(configured)
                ? configured.Trim()
                : DepositAddress;

        var balance = await SyncBalanceAsync();
        if (balance < MinimumBalanceSats)
            Assert.Fail(TopUpMessage(balance, MinimumBalanceSats, "the funded regtest suite"));
    }

    /// <summary>
    /// Forces a sync and returns the balance.
    /// </summary>
    /// <remarks>
    /// The sync is not optional. <c>GetInfo(ensureSynced: true)</c> was observed returning a stale balance for
    /// ~20 s after settlement; only an explicit <c>SyncWallet</c> moves it, and everything in this suite that
    /// compares a balance against a threshold has to see the current one.
    /// </remarks>
    public async Task<long> SyncBalanceAsync(CancellationToken cancellationToken = default)
    {
        await Sdk.SyncWalletAsync(cancellationToken);
        var info = await Sdk.GetInfoAsync(ensureSynced: true, cancellationToken);
        return info.BalanceSats;
    }

    /// <summary>Fails with the top-up instruction unless the wallet can cover <paramref name="needSats"/>.</summary>
    public async Task RequireBalanceAsync(long needSats, string what, CancellationToken cancellationToken = default)
    {
        var balance = await SyncBalanceAsync(cancellationToken);
        if (balance < needSats)
            Assert.Fail(TopUpMessage(balance, needSats, what));
    }

    /// <summary>
    /// Registers a value the log audit should hunt for by name — a preimage, a payment hash, a txid.
    /// </summary>
    /// <remarks>
    /// This is what turns the artefact from "some log lines" into an answer. Every 64-hex run the SDK emitted
    /// is classified against these, so a human reading it once can say which runs are secrets and which are
    /// the identifiers an operator needs, rather than eyeballing hex.
    /// </remarks>
    public void RegisterIdentifier(string kind, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        lock (_identifiers)
        {
            if (!_identifiers.Any(i => string.Equals(i.Value, value, StringComparison.OrdinalIgnoreCase)))
                _identifiers.Add(new KnownIdentifier(kind, value));
        }
    }

    /// <summary>A snapshot of how many lines the bridge has forwarded, to bound a later slice.</summary>
    public int ForwardedLineCount => ForwardedLog.Lines.Count;

    /// <summary>The lines forwarded since <paramref name="from"/>.</summary>
    public IReadOnlyList<string> ForwardedSince(int from)
    {
        var lines = ForwardedLog.Lines;
        return from >= lines.Count ? [] : lines.Skip(from).ToList();
    }

    /// <summary>The seed, for the tests that must prove it was not logged. Never write this anywhere.</summary>
    public string Mnemonic => _mnemonic
        ?? throw new InvalidOperationException("The funded regtest wallet is not enabled.");

    public async ValueTask DisposeAsync()
    {
        if (_mnemonic is null)
            return;

        string? seedLeak = null;
        try
        {
            seedLeak = WriteAudit();
        }
        catch (Exception ex)
        {
            // A failed artefact write must not turn a green suite red: the artefact is evidence for a human,
            // not a result. The tests carry their own assertions. A *detected seed leak* is the one exception,
            // thrown below — outside this catch — so a broken artefact writer cannot swallow it.
            Console.WriteLine($"funded-regtest: could not write the log-audit artefact: {ex}");
        }

        try
        {
            if (Sdk is not null)
            {
                // Disconnect then Dispose: after Disconnect alone the instance still serves the network.
                await Sdk.DisconnectAsync();
                Sdk.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"funded-regtest: could not disconnect cleanly: {ex}");
        }

        TryDeleteStorage();

        if (seedLeak is not null)
        {
            // Hard failure, not just a withheld attachment: a run that leaked the funded wallet's seed into a
            // log is a security regression, and a red suite is the only signal nobody can skim past. Thrown
            // last so the artefacts are written and the SDK is torn down before the run turns red.
            throw new InvalidOperationException($"funded-regtest seed leak: {seedLeak}");
        }
    }

    /// <summary>
    /// Writes the artefact that settles the log audit's one stated gap: what the SDK emits when a payment
    /// actually completes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three files. <c>forwarded.log</c> is what an operator's BTCPay log would have shown — everything that
    /// went through <see cref="SparkLogScrubber"/>. <c>sdk.log</c> is the raw file the Rust subscriber wrote,
    /// which no C# scrubbing reaches and which is therefore the artefact the question is actually about.
    /// <c>preimage-audit.md</c> is the answer: every distinct 64-hex run the SDK emitted, classified against
    /// the preimage, payment hash and txid the tests recorded, with the words on either side of it.
    /// </para>
    /// <para>
    /// <b>Both log files are withheld if the seed, a payment preimage, or the service provider's
    /// <c>session_token</c> would be in them</b>, each leaving a <c>&lt;name&gt;.WITHHELD.txt</c> marker
    /// naming the reason in the file's place. These artefacts are downloadable by anyone who can see the
    /// repository, and the seed is a CI secret. A leak would be a finding worth failing on — the tests assert
    /// against it — but it must not also be a publication. In <c>preimage-audit.md</c> the known-value rows
    /// for preimages print a one-way fingerprint rather than the value whatever the sources hold — a
    /// fingerprint plus the occurrence counts proves the same property — rows sourced from a withheld file
    /// print a fingerprint and a redaction marker in place of value and context, and a withheld source's
    /// count columns read <em>withheld</em> rather than numbers.
    /// </para>
    /// </remarks>
    /// <returns>
    /// A leak description when the wallet seed appeared in either log surface, null otherwise. Returned rather
    /// than thrown so the artefact — the evidence — is fully written before the caller turns the run red.
    /// </returns>
    private string? WriteAudit()
    {
        var directory = Environment.GetEnvironmentVariable(ArtifactVariable) is { } configured
            && !string.IsNullOrWhiteSpace(configured)
                ? configured.Trim()
                : Path.Combine(AppContext.BaseDirectory, "funded-regtest-artifacts");
        Directory.CreateDirectory(directory);

        var forwarded = ForwardedLog.Lines;
        var forwardedText = string.Join('\n', forwarded);
        // The forwarded log passes the same gate as the raw one — same artefact bundle, same download
        // permissions, same retention window. What differs is what passing through the scrubber was supposed
        // to mean: secret material in *this* file is a scrubber hole rather than an unreachable gap,
        // which is what the header line and the verdict below both say. Withheld means deleted-on-sight too:
        // a file an earlier, cleaner run wrote into the artefact directory would otherwise survive the gate.
        var forwardedSecret = SecretMaterialIn(forwardedText);
        var forwardedWithheld = forwardedSecret is not null;
        var forwardedCarriesSeed = SeedAppearsIn(forwardedText, _mnemonic!);
        var forwardedPath = Path.Combine(directory, "forwarded.log");
        if (forwardedWithheld)
            WithholdArtifact(forwardedPath, $"it carries {forwardedSecret} in post-scrub output");
        else
            File.WriteAllLines(forwardedPath, forwarded);

        var raw = ReadRawSdkLog();
        var rawSecret = raw is null ? null : SecretMaterialIn(raw);
        var rawWithheld = rawSecret is not null;
        var rawCarriesSeed = raw is not null && SeedAppearsIn(raw, _mnemonic!);
        if (raw is not null)
        {
            var rawPath = Path.Combine(directory, "sdk.log");
            if (rawWithheld)
                WithholdArtifact(rawPath, $"it carries {rawSecret}");
            else
                File.WriteAllText(rawPath, raw);
        }

        var report = new StringBuilder();
        report.AppendLine("# Spark SDK log audit — funded regtest run");
        report.AppendLine();
        report.AppendLine($"- Wallet seed fingerprint: `{SeedFingerprint}` (SHA-256 prefix; not reversible)");
        report.AppendLine($"- SDK log filter: `debug` (installed: {_loggingInstalled})");
        report.AppendLine(forwardedWithheld
            ? $"- Forwarded lines (post-scrub, what BTCPay's logger saw): **{forwarded.Count}**, "
              + $"**`forwarded.log` WITHHELD from this artefact because {forwardedSecret} appears in it** — "
              + "this file is post-scrub output, so the scrubber has a hole."
            : $"- Forwarded lines (post-scrub, what BTCPay's logger saw): **{forwarded.Count}**, "
              + "attached as `forwarded.log`");
        report.AppendLine(raw is null
            ? "- Raw `sdk.log`: **not found** — the Rust subscriber wrote nothing to the log directory."
            : $"- Raw `sdk.log`: **{raw.Split('\n').Length}** lines, "
              + (rawWithheld
                  ? $"**WITHHELD from this artefact because {rawSecret} appears in it**"
                  : "attached as `sdk.log`"));
        report.AppendLine();

        report.AppendLine("## What this file is for");
        report.AppendLine();
        report.AppendLine(
            "The plugin's audit of the SDK's log output was performed on an **unfunded** wallet, so the "
            + "lines a *completed* payment emits — the ones a preimage would ride on — were never produced and "
            + "remain unaudited. `SparkLogScrubber`'s remarks say the same thing and say that what would "
            + "overturn its name-keyed design is an observation, not an argument.");
        report.AppendLine();

        // Whether this artefact is evidence at all turns on one thing: did a payment actually complete? A run
        // that aborted on a drained wallet still produces several hundred lines of connect chatter and a
        // clean-looking empty table, and a reader skimming it would reasonably conclude the gap was measured
        // and found empty. It was not measured. Saying so here, loudly, is the difference between evidence and
        // a false negative that closes a security question on no data.
        var observedSettlement = Snapshot().Any(i =>
            i.Kind.Contains("preimage", StringComparison.OrdinalIgnoreCase));
        report.AppendLine(observedSettlement
            ? "**This run completed a payment**, so the lines below are the ones §8.6 is missing. Read the "
              + "table once and the question is settled."
            : "> **THIS RUN DID NOT COMPLETE A PAYMENT, SO IT MEASURES NOTHING.** No preimage was recorded, "
              + "which means the suite aborted before settling anything — almost always a drained wallet (see "
              + "the balance in the job log). The empty table below is *not* the finding \"the SDK logs no "
              + "preimage\"; it is the absence of an observation. Top up the CI wallet and re-run before "
              + "citing this artefact for anything.");
        report.AppendLine();

        report.AppendLine("## Known values from this run");
        report.AppendLine();
        report.AppendLine(
            "Preimage values print as one-way SHA-256 fingerprints, not values: the fingerprint plus the "
            + "counts proves the same thing, and the value is exactly the secret this artefact exists to "
            + "keep out. Payment hashes, txids and idempotency keys are public identifiers and print "
            + "verbatim.");
        report.AppendLine();
        report.AppendLine("| kind | value | occurrences in forwarded (scrubbed) | occurrences in raw sdk.log |");
        report.AppendLine("|---|---|---|---|");
        foreach (var identifier in Snapshot())
        {
            var inForwarded = CountOccurrences(forwardedText, identifier.Value);
            var inRaw = raw is null ? (int?)null : CountOccurrences(raw, identifier.Value);
            report.AppendLine(
                $"| {identifier.Kind} "
                + $"| {AuditValue(ValueIsWithheld(identifier.Kind), identifier.Value)} "
                + $"| {AuditCell(forwardedWithheld, inForwarded.ToString())} "
                + $"| {(inRaw is null ? "n/a" : inRaw.ToString())} |");
        }

        report.AppendLine("| wallet seed | *(not printed)* | "
            + $"{(forwardedCarriesSeed ? "**PRESENT — this is a leak**" : AuditCell(forwardedWithheld, "0"))} | "
            + $"{(raw is null ? "n/a" : rawCarriesSeed ? "**PRESENT — this is a leak**" : "0")} |");
        report.AppendLine();

        report.AppendLine("## Every 64-hex run the SDK emitted, classified");
        report.AppendLine();
        report.AppendLine(
            "A preimage, a payment hash, a txid and half a public key are all 32 bytes of hex, which is why "
            + "`SparkLogScrubber` refuses to redact on shape. The classification column is the part that "
            + "matters: an `UNKNOWN` run that turns out to be a preimage is the finding. Rows sourced from a "
            + "withheld file keep their classification and print a one-way SHA-256 fingerprint in place of the "
            + "value and a redaction marker in place of the context — enough to match a fingerprint quoted by "
            + "a verdict or a known-value row, never enough to publish.");
        report.AppendLine();
        report.AppendLine("| source | classification | value | context |");
        report.AppendLine("|---|---|---|---|");
        var rows = 0;
        foreach (var (source, text, withheld) in new[]
                 {
                     ("forwarded", forwardedText, forwardedWithheld),
                     ("sdk.log", raw ?? string.Empty, rawWithheld),
                 })
        {
            foreach (var run in DistinctHexRuns(text))
            {
                if (rows++ >= 200)
                    break;
                report.AppendLine(HexRunRow(source, Classify(run.Value), run, withheld));
            }
        }

        if (rows == 0)
            report.AppendLine("| — | — | *(no 64-hex runs were logged at all)* | — |");

        report.AppendLine();
        report.AppendLine("## How to read this and close the gap");
        report.AppendLine();
        report.AppendLine(
            "1. The **preimage** row must show `0` occurrences in the forwarded column. The tests assert that; "
            + "if it is not zero the run is red and the scrubber has a hole. A forwarded column reading "
            + "*withheld* instead of a number is the same finding by another route: `forwarded.log` itself "
            + "was withheld because secret material appeared in the post-scrub output.");
        report.AppendLine(
            "2. Look at the `sdk.log` rows classified `PREIMAGE`. If there are none, the SDK does not write a "
            + "preimage to disk at `debug` and §8.6's gap closes as \"measured, nothing there\" — **but only if "
            + "the banner above says a payment completed.** On a run that settled nothing, \"no preimage rows\" "
            + "means no data, not a clean result.");
        report.AppendLine(
            "3. If there are some, read their **context**. A preimage preceded by a name the scrubber knows "
            + "(`preimage:`, `preimage `) is already handled. A preimage with no name beside it is the case "
            + "`SparkLogScrubber`'s remarks left open — and it lives in `sdk.log`, which the C# scrubber cannot "
            + "reach, so the fix is a level or a file-permissions question, not a regex.");
        report.AppendLine(
            "4. Record the answer in `Sdk/SparkLogScrubber.cs`'s remarks, replacing the stated gap with the "
            + "measurement. That is the whole point of this artefact.");

        File.WriteAllText(Path.Combine(directory, "preimage-audit.md"), report.ToString());

        // The verdict, after the evidence is on disk. Never includes a seed word — the fingerprint is enough
        // to match the report, and the report itself withholds the leaking file.
        if (forwardedCarriesSeed)
        {
            return "the wallet seed appears in the FORWARDED (post-scrub) log — the scrubber has a hole. "
                   + $"Seed fingerprint {SeedFingerprint}; see preimage-audit.md in the run artefacts.";
        }

        return rawCarriesSeed
            ? "the wallet seed appears in the raw sdk.log written by the Rust SDK. The file was withheld from "
              + $"the artefacts; seed fingerprint {SeedFingerprint}, details in preimage-audit.md."
            : null;
    }

    private IReadOnlyList<KnownIdentifier> Snapshot()
    {
        lock (_identifiers)
            return _identifiers.ToList();
    }

    private string Classify(string hex)
    {
        foreach (var identifier in Snapshot())
        {
            if (string.Equals(identifier.Value, hex, StringComparison.OrdinalIgnoreCase))
                return identifier.Kind.ToUpperInvariant();
        }

        return "UNKNOWN";
    }

    private string? ReadRawSdkLog()
    {
        if (_logDirectory is null || !Directory.Exists(_logDirectory))
            return null;

        var files = Directory.GetFiles(_logDirectory, "*.log");
        if (files.Length == 0)
            return null;

        var builder = new StringBuilder();
        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            // FileShare.ReadWrite because the Rust subscriber still holds the handle open; there is no way to
            // ask it to flush or close, so a partially written tail is simply what this reads.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            builder.Append(reader.ReadToEnd());
        }

        return builder.ToString();
    }

    /// <summary>
    /// True when the mnemonic, or any run of six of its words in order, appears in the text.
    /// </summary>
    /// <remarks>
    /// Six rather than the whole phrase because a leak does not have to be well formed to be a leak — a
    /// truncated log line carrying half the seed is still most of the wallet. Six is also well past the point
    /// where ordinary English could produce the sequence by accident.
    /// </remarks>
    internal static bool SeedAppearsIn(string text, string mnemonic)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        if (text.Contains(mnemonic, StringComparison.OrdinalIgnoreCase))
            return true;

        var words = mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        const int window = 6;
        for (var i = 0; i + window <= words.Length; i++)
        {
            if (text.Contains(string.Join(' ', words.Skip(i).Take(window)), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
            return 0;

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>
    /// The secret material a log would publish — the wallet seed, a preimage this run recorded, or the
    /// service provider's session token — or null when the text carries none of it.
    /// </summary>
    internal static string? SecretMaterialIn(
        string? text, string mnemonic, IReadOnlyList<KnownIdentifier> identifiers)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(mnemonic))
            return null;

        if (SeedAppearsIn(text, mnemonic))
            return "the wallet seed";

        foreach (var identifier in identifiers)
        {
            if (IsPreimageKind(identifier.Kind)
                && CountOccurrences(text, identifier.Value) > 0)
                return "a payment preimage";
        }

        return SessionTokenAppearsIn(text) ? "the service provider's session token" : null;
    }

    /// <summary>
    /// <see cref="SecretMaterialIn(string, string, IReadOnlyList{KnownIdentifier})"/> against this run's
    /// seed and recorded identifiers.
    /// </summary>
    private string? SecretMaterialIn(string text) => SecretMaterialIn(text, _mnemonic!, Snapshot());

    /// <summary>
    /// True when the text carries the service provider's GraphQL <c>session_token</c> name next to a value —
    /// the shape <c>SparkLogScrubber</c> redacts on. Trace, where the token is logged, is clamped out before
    /// the subscriber is installed, so this is belt-and-braces: if a raw response body ever reached the log,
    /// the artefact must not publish it.
    /// </summary>
    internal static bool SessionTokenAppearsIn(string text) => SessionTokenShape.IsMatch(text);

    private static readonly Regex SessionTokenShape = new(
        $$"""
        (?ix)
        \\?"? \b session_?token \b \\?"? \s* [:=] \s*
        (?: " [^"\\]* " | ' [^']* ' | [^\s,;}\]\)]+ )
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>Every distinct 64-character hex run in the text, with the 48 characters in front of it.</summary>
    internal static IReadOnlyList<HexRun> DistinctHexRuns(string text)
    {
        var found = new Dictionary<string, HexRun>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text))
            return [];

        for (var i = 0; i < text.Length; i++)
        {
            if (!IsHex(text[i]))
                continue;

            var start = i;
            while (i < text.Length && IsHex(text[i]))
                i++;

            var length = i - start;
            // Exactly 64: a longer run is not a 32-byte value with something stuck to it, it is a different
            // value, and reporting a slice of one as a preimage candidate would be noise.
            if (length != 64)
                continue;
            if (start > 0 && IsHexAdjacent(text[start - 1]))
                continue;

            var value = text.Substring(start, 64);
            if (found.ContainsKey(value))
                continue;

            var contextStart = Math.Max(0, start - 48);
            var context = text[contextStart..start].Replace('\n', ' ').Replace('\r', ' ');
            found[value] = new HexRun(value, context.Trim());
        }

        return found.Values.ToList();
    }

    private static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    // A hex run butted up against another hex character is part of something longer; letters g-z are not.
    private static bool IsHexAdjacent(char c) => IsHex(c);

    private static string Escape(string value) => value.Replace("|", "\\|").Replace("`", "'");

    internal static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();

    /// <summary>
    /// Withholds one log artefact from the bundle: deletes whatever an earlier run left at
    /// <paramref name="path"/> — so withholding cannot be undone by a previous, cleaner copy of the file —
    /// and writes a marker beside it naming the file and the reason.
    /// </summary>
    /// <remarks>
    /// Marker naming: <c>&lt;artefact&gt;.WITHHELD.txt</c>, one per withheld artefact
    /// (<c>forwarded.log.WITHHELD.txt</c>, <c>sdk.log.WITHHELD.txt</c>), so the bundle explains which file
    /// is missing and why without opening the audit markdown — including when the file was never written at
    /// all because this run skipped it at the gate. A failed delete is swallowed and recorded in the marker
    /// rather than thrown: the verdict on the run comes from the assertions, not from this bookkeeping.
    /// </remarks>
    private static void WithholdArtifact(string path, string reason)
    {
        var deleteFailed = false;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            deleteFailed = true;
        }
        catch (UnauthorizedAccessException)
        {
            deleteFailed = true;
        }

        // One write; its own IO errors are swallowed too. When the delete failed the marker says so,
        // because then the old copy may still be sitting in the directory and the reader must know.
        try
        {
            File.WriteAllText(
                $"{path}.WITHHELD.txt",
                $"{Path.GetFileName(path)} was withheld from this artefact bundle: {reason}.\n"
                + (deleteFailed
                    ? "Deleting the copy an earlier run left here FAILED — the file may still be in this "
                      + "directory; remove it manually before publishing the bundle.\n"
                    : "This run wrote no copy, and any copy an earlier run left here was removed.\n"));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether an identifier kind names a payment preimage, i.e. carries secret material.</summary>
    internal static bool IsPreimageKind(string kind) =>
        kind.Contains("preimage", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a known-value row must print a fingerprint instead of the value: every preimage-kind value
    /// does, unconditionally. A fingerprint plus the row's occurrence counts proves the same evidence
    /// property the verbatim value would — this exact preimage appeared this many times — so publishing the
    /// value itself is never what the row is for, whatever state the log sources are in. Payment hashes,
    /// txids and idempotency keys are public identifiers and print verbatim; the gate judges the sources
    /// that carried them, not the identifiers.
    /// </summary>
    internal static bool ValueIsWithheld(string kind) => IsPreimageKind(kind);

    /// <summary>A table value cell: the value, or its one-way fingerprint when the source is withheld.</summary>
    internal static string AuditValue(bool withheld, string value) =>
        withheld ? $"`{Fingerprint(value)}`" : $"`{value}`";

    /// <summary>A table context cell: the escaped context, or a redaction marker when withheld.</summary>
    internal static string AuditContext(bool withheld, string context) =>
        withheld ? "*(redacted — source withheld)*" : $"`{Escape(context)}`";

    /// <summary>A table count cell: the count, or the word <em>withheld</em> instead of numbers.</summary>
    internal static string AuditCell(bool withheld, string value) =>
        withheld ? "*(withheld)*" : value;

    /// <summary>One row of the 64-hex classification table, redacted when its source is withheld.</summary>
    internal static string HexRunRow(string source, string classification, HexRun run, bool sourceWithheld) =>
        $"| {source} | {classification} | {AuditValue(sourceWithheld, run.Value)} "
        + $"| {AuditContext(sourceWithheld, run.Context)} |";

    private void TryDeleteStorage()
    {
        if (_storageDirectory is null)
            return;
        try
        {
            Directory.Delete(Path.GetDirectoryName(_storageDirectory)!, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a run over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal sealed record KnownIdentifier(string Kind, string Value);

    internal sealed record HexRun(string Value, string Context);

    private sealed class FixedStorageProvider : ISparkStorageProvider
    {
        private readonly string _path;

        public FixedStorageProvider(string path) => _path = path;

        public SparkStorageTarget GetTarget(string storeId) => new SparkStorageTarget.Directory(_path);
    }
}

/// <summary>
/// The collection every funded-wallet test joins, so they share one wallet and run one at a time.
/// </summary>
[CollectionDefinition(FundedRegtestWallet.CollectionName, DisableParallelization = true)]
public sealed class FundedRegtestCollection : ICollectionFixture<FundedRegtestWallet>;
