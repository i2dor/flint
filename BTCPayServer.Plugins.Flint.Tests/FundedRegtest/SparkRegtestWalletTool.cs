using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;
using SdkNetwork = Breez.Sdk.Spark.Network;

namespace BTCPayServer.Plugins.Flint.Tests.FundedRegtest;

/// <summary>
/// The two operator actions that stand up the CI regtest wallet: make a seed, and find out where to send it money.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are tests and not a console project.</b> Both need the plugin's own SDK client, its storage
/// provider and its connect options — the whole graph the test project already composes. A separate executable
/// would either duplicate that or be added to the solution and shipped in the plugin package, and neither is
/// worth it for a tool run twice a year. They carry their own category so nothing else ever picks them up.
/// </para>
/// <para>
/// <b>The seed is never transmitted, and that is the whole design.</b> The obvious workflow — CI generates a
/// mnemonic and hands it back through an encrypted artefact — needs a passphrase channel, and the only channel a
/// <c>workflow_dispatch</c> offers is its inputs, which GitHub records against the run <em>unmasked</em> and
/// shows to everyone with read access. It would put the key next to the lock. So generation happens on the
/// operator's own machine, offline, and the only thing that ever crosses a network is the operator pasting the
/// result into GitHub's secret form. See <see cref="Generate_a_wallet_mnemonic_for_the_operator_to_store"/> for
/// the guard that keeps it that way.
/// </para>
/// <para>
/// Neither test joins <see cref="FundedRegtestWallet"/>'s collection. That is deliberate:
/// <see cref="Print_the_funding_details_for_the_configured_wallet"/> has to work on a wallet holding
/// <em>nothing</em>, since its entire purpose is to tell the operator where to send the first coins, and the
/// funded fixture refuses to start below <see cref="FundedRegtestWallet.MinimumBalanceSats"/>.
/// </para>
/// </remarks>
[Trait("Category", "RegtestWalletTool")]
public class SparkRegtestWalletTool
{
    /// <summary>Opt-in for the offline generator. Deliberately not the same variable that gates anything else.</summary>
    public const string GenerateVariable = "SPARK_REGTEST_WALLET_GENERATE";

    private readonly ITestOutputHelper _output;

    public SparkRegtestWalletTool(ITestOutputHelper output) => _output = output;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Mints a fresh 12-word BIP39 mnemonic and prints it, for the operator to paste into a repository secret.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refuses to run on a CI runner, by design.</b> This is the only place in the repository that prints a
    /// private key, and a job log is a published document. The guard is not advice in a comment — it is an
    /// assertion, so a future workflow that tries to call this fails loudly rather than quietly leaking a wallet
    /// into a log that anyone with read access can download for ninety days.
    /// </para>
    /// <para>
    /// Needs no network, which is what makes it runnable by a maintainer whose IP Lightspark blocks — the exact
    /// situation this plugin's maintainer is in. Generation is arithmetic; only funding needs the SSP.
    /// </para>
    /// </remarks>
    [Fact]
    public void Generate_a_wallet_mnemonic_for_the_operator_to_store()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(GenerateVariable) == "1",
            $"Set {GenerateVariable}=1 to mint a new CI regtest wallet seed. Run it on your own machine, never "
            + "in CI. See docs/testing.md, \"A funded regtest wallet for CI\".");

        // The guard. A job log is published; a seed is not.
        Assert.True(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true",
            "This generator refuses to run on a GitHub Actions runner. It prints a wallet's private key to "
            + "stdout, and a job log is readable by everyone with access to the repository and is retained for "
            + "months. Generate the seed on your own machine and paste it into the repository secret "
            + $"{FundedRegtestWallet.SeedVariable}; the `spark-regtest-wallet` workflow then reads the secret and "
            + "prints only the deposit address.");

        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();

        _output.WriteLine("");
        _output.WriteLine("=== A new Spark regtest wallet seed =========================================");
        _output.WriteLine("");
        _output.WriteLine(mnemonic);
        _output.WriteLine("");
        _output.WriteLine($"Fingerprint: {Fingerprint(mnemonic)}   (SHA-256 prefix — safe to quote in an issue)");
        _output.WriteLine("");
        _output.WriteLine("Next:");
        _output.WriteLine($"  1. Store it as the repository secret {FundedRegtestWallet.SeedVariable}.");
        _output.WriteLine("     Settings > Secrets and variables > Actions > New repository secret.");
        _output.WriteLine("  2. Run the `spark-regtest-wallet` workflow to print the deposit address.");
        _output.WriteLine("  3. Fund that address from https://app.lightspark.com/regtest-faucet.");
        _output.WriteLine("");
        _output.WriteLine("This is regtest money and worth nothing, but the wallet is shared CI infrastructure:");
        _output.WriteLine("do not paste it into an issue, a PR, or a chat.");
        _output.WriteLine("=============================================================================");

        // Not a formality. If NBitcoin's word list or entropy source ever changed under us, the operator would
        // paste a phrase the SDK rejects and debug it against a live service instead of here.
        Assert.Equal(12, mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.True(new Mnemonic(mnemonic, Wordlist.English).IsValidChecksum);
    }

    /// <summary>
    /// Connects the configured seed to Lightspark regtest and prints where to send it money.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the <c>spark-regtest-wallet</c> workflow runs. It reads
    /// <see cref="FundedRegtestWallet.SeedVariable"/> — a masked GitHub secret — and emits the deposit address,
    /// the identity pubkey and the current balance. The mnemonic itself appears nowhere: what identifies the
    /// wallet in the output is a SHA-256 prefix, which is enough for an operator to confirm the run used the
    /// wallet they think it did and useless to anyone who reads it.
    /// </para>
    /// <para>
    /// <b>The deposit address is static.</b> That is a property of Spark, not an accident, and it is why a
    /// runbook step can say "fund this address" once and stay true: the same address keeps working for every
    /// later top-up, so the operator never has to re-run this to add money.
    /// </para>
    /// <para>
    /// The last assertion re-reads everything the test emitted and fails if the seed is in it. The output is
    /// assembled in one buffer precisely so that check can be exhaustive rather than a promise.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Print_the_funding_details_for_the_configured_wallet()
    {
        Assert.SkipUnless(
            FundedRegtestWallet.IsEnabled,
            $"Set {FundedRegtestWallet.SeedVariable} to the wallet's BIP39 mnemonic to print its deposit "
            + "address.");

        var seed = Environment.GetEnvironmentVariable(FundedRegtestWallet.SeedVariable)!.Trim();
        try
        {
            _ = new Mnemonic(seed, Wordlist.English);
        }
        catch (Exception ex)
        {
            Assert.Fail(
                $"{FundedRegtestWallet.SeedVariable} is not a valid English BIP39 mnemonic ({ex.GetType().Name}). "
                + "It must be the space-separated word list and nothing else — no quotes, no trailing newline. "
                + "GitHub's secret form keeps a trailing newline if you paste one, so re-paste without it.");
        }

        var storageDirectory = Path.Combine(
            Path.GetTempPath(), "spark-regtest-wallet-tool", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageDirectory);

        var events = Channel.CreateBounded<SparkEventEnvelope>(new BoundedChannelOptions(64));
        var factory = new SparkSdkClientFactory(
            new ToolStorageProvider(storageDirectory),
            new NBitcoinBolt11Parser(Network.RegTest, NullLogger<NBitcoinBolt11Parser>.Instance),
            NullLoggerFactory.Instance);

        ISparkSdkClient? sdk = null;
        try
        {
            sdk = await factory.ConnectAsync(
                new SparkConnectOptions(
                    "regtest-wallet-tool", seed, passphrase: null, apiKey: null, SdkNetwork.Regtest),
                events.Writer,
                Ct);

            var address = await sdk.GetBitcoinDepositAddressAsync(Ct);
            // Sync first: GetInfo alone was observed returning a stale balance for ~20 s, and an operator
            // checking whether their top-up landed is exactly the person that would mislead.
            await sdk.SyncWalletAsync(Ct);
            var info = await sdk.GetInfoAsync(ensureSynced: true, Ct);
            var unclaimed = await sdk.ListUnclaimedDepositsAsync(Ct);

            var report = new StringBuilder();
            report.AppendLine("## Spark CI regtest wallet");
            report.AppendLine();
            report.AppendLine($"- **Seed fingerprint**: `{Fingerprint(seed)}` (SHA-256 prefix; not reversible)");
            report.AppendLine($"- **Identity pubkey**: `{info.IdentityPubkey}`");
            report.AppendLine($"- **Balance**: **{info.BalanceSats:N0}** sats "
                + $"(the funded suite needs {FundedRegtestWallet.MinimumBalanceSats:N0})");
            report.AppendLine($"- **Unclaimed on-chain deposits**: {unclaimed.Count}");
            report.AppendLine();
            report.AppendLine("### Send regtest sats here");
            report.AppendLine();
            report.AppendLine("```");
            report.AppendLine(address);
            report.AppendLine("```");
            report.AppendLine();
            report.AppendLine(
                "This address is **static** — it does not rotate, so keep it for every future top-up and you "
                + "will not need to run this workflow again.");
            report.AppendLine();
            report.AppendLine(
                "Fund it at <https://app.lightspark.com/regtest-faucet> (reCAPTCHA-gated, so a human has to do "
                + "it). The SDK claims the deposit automatically on the next connect; the balance above will "
                + "not move until it does, which takes a confirmation.");
            report.AppendLine();
            report.AppendLine(
                info.BalanceSats >= FundedRegtestWallet.MinimumBalanceSats
                    ? "**The wallet is funded.** The `funded-regtest-test` job will run."
                    : $"**The wallet is below the floor.** Top it up to at least "
                      + $"{FundedRegtestWallet.MinimumBalanceSats:N0} sats or the funded suite fails with an "
                      + "instruction to do so.");

            var text = report.ToString();
            _output.WriteLine(text);
            Console.WriteLine(text);

            // Rendered on the run's summary page, which is where an operator will actually look — a deposit
            // address buried in a build log is a step they will get wrong.
            if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
                await File.AppendAllTextAsync(summary, text, Ct);

            Assert.False(string.IsNullOrWhiteSpace(address), "the SDK returned no deposit address");
            Assert.False(string.IsNullOrWhiteSpace(info.IdentityPubkey), "the SDK returned no identity pubkey");

            // Everything this test emitted, checked in one place. The report is built as a single string so
            // this can be exhaustive rather than a claim about each WriteLine.
            Assert.False(
                FundedRegtestWallet.SeedAppearsIn(text, seed),
                "the wallet's mnemonic reached this tool's output, which is published to a job log and a run "
                + "summary. Treat the wallet as compromised and rotate it.");
        }
        finally
        {
            if (sdk is not null)
            {
                await sdk.DisconnectAsync();
                sdk.Dispose();
            }

            try
            {
                Directory.Delete(storageDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();

    private sealed class ToolStorageProvider : ISparkStorageProvider
    {
        private readonly string _path;

        public ToolStorageProvider(string path) => _path = path;

        public SparkStorageTarget GetTarget(string storeId) => new SparkStorageTarget.Directory(_path);
    }
}
