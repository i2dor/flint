using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Sdk;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The scrubber gives the same answer for the same text, however busy the machine is.
/// </summary>
/// <remarks>
/// <para>
/// Every pattern in <see cref="SparkLogScrubber"/> used to carry a 50 ms match timeout as its guard against
/// catastrophic backtracking, and <c>Scrub</c> turns any exception into its total-redaction fallback. .NET measures
/// a match timeout in elapsed time rather than in work, so a thread that was descheduled — or suspended for a
/// garbage collection — in the middle of a match needing microseconds timed out all the same.
/// <c>SparkErrorsTests.Describe_gives_a_plain_message_for_a_disposed_instance</c> failed now and then in a full
/// parallel run because of it, with the fallback sentence where "no longer running" belonged; in production the
/// same race told a merchant that an ordinary error "could not be shown safely", and took lines out of the
/// operator's log for no reason at all.
/// </para>
/// <para>
/// The bound is now the engine rather than the clock — <see cref="SparkLogScrubber"/>'s remarks say which engine
/// each pattern runs on and why. The first three tests pin what that rests on, deterministically; the last one
/// is the regression itself, reproduced the way it happened.
/// </para>
/// <para>
/// A collection of its own, never run alongside another: the contention test saturates every core on purpose,
/// and this suite has tests with 50 ms deadlines of their own that it would otherwise turn flaky in their turn.
/// </para>
/// </remarks>
[Collection(SparkLogScrubberContentionCollection.Name)]
public class SparkLogScrubberTests
{
    // Shaped like the trace-level session token the log audit found, and the same stand-in SparkLogBridgeTests uses.
    private const string Token = "eyJ1aWQiOiIwMTlmZDQ5Ny03Yjc3LTZlZDgifQ.anPdDQ.9noMLLWPhHNXSkl3YtayTfpEtMbvonj";
    private const string Preimage = "9f2c1b7a4e8d0356f1a9c4b28e7d05316a4f9c2b8e7d0531f4a9c2b8e7d05314";

    /// <summary>How long the contention test keeps the machine busy: long enough to have caught the old timeouts every time.</summary>
    private static readonly TimeSpan ContentionDuration = TimeSpan.FromSeconds(2);

    /// <summary>
    /// No pattern the scrubber holds carries a match timeout.
    /// </summary>
    /// <remarks>
    /// The deterministic half of the regression. A timeout is a clock, and a clock makes the scrubber's answer a
    /// function of the scheduler: the contention test below can only catch that some of the time, this catches it
    /// every time. Found by reflection rather than listed by name, so a pattern added later is held to the same
    /// rule without anyone having to remember this test exists.
    /// </remarks>
    [Fact]
    public void No_pattern_carries_a_match_timeout()
    {
        var patterns = ScrubberPatterns();

        Assert.NotEmpty(patterns);
        Assert.All(patterns, pattern => Assert.True(
            pattern.Regex.MatchTimeout == Regex.InfiniteMatchTimeout,
            $"{pattern.Name} has a {pattern.Regex.MatchTimeout.TotalMilliseconds} ms match timeout. .NET measures it in "
            + "elapsed time, so a busy machine trips it on text that needs microseconds and Scrub drops the line. "
            + "Bound the pattern by its engine instead — see SparkLogScrubber's remarks."));
    }

    /// <summary>
    /// Every pattern on <c>NonBacktracking</c> finds exactly what the backtracking engine would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That engine is only used where the backtracking one cannot be bounded, because it is not a drop-in: on the
    /// .NET 10 runtime this was measured on, for patterns shaped like <c>SensitiveValue</c>'s it sometimes returns a
    /// match that starts after the leftmost one — which, for a redactor, means leaving the start of a secret where
    /// it was. The first inputs below are exactly those cases, so moving <c>SensitiveValue</c> onto the engine fails
    /// here instead of in somebody's log.
    /// </para>
    /// <para>
    /// The rest are seeded, so a failure reproduces: token soup built from the names, separators, quotes and line
    /// breaks the patterns key on. Some sixteen million such inputs agreed when <c>HeaderCredential</c> moved; this
    /// keeps a sample of them running against every runtime the plugin is built with, since the property belongs to
    /// the runtime and not to this repository.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_NonBacktracking_pattern_finds_what_the_backtracking_engine_finds()
    {
        var inputs = DifferentialInputs();

        foreach (var (name, nonBacktracking) in ScrubberPatterns().Where(p => p.Regex.Options.HasFlag(RegexOptions.NonBacktracking)))
        {
            var backtracking = new Regex(
                nonBacktracking.ToString(),
                nonBacktracking.Options & ~RegexOptions.NonBacktracking,
                Regex.InfiniteMatchTimeout);

            foreach (var input in inputs)
            {
                var expected = Describe(backtracking.Matches(input));
                var actual = Describe(nonBacktracking.Matches(input));
                Assert.True(
                    expected == actual,
                    $"{name} disagrees with the backtracking engine on \"{Escape(input)}\": "
                    + $"backtracking {expected}, NonBacktracking {actual}");
            }
        }
    }

    /// <summary>
    /// A line built to make <c>HeaderCredential</c> backtrack quadratically is scrubbed, not dropped.
    /// </summary>
    /// <remarks>
    /// Without <c>Multiline</c>, <c>$</c> only matches at the end of the text, so every credential word in front of a
    /// newline sends the backtracking engine to that newline and back again, a character at a time. For this line
    /// that is 1.1 s of backtracking: under the old 50 ms timeout it came back as the fallback every time, and on that
    /// engine with no timeout it would hold an SDK callback thread for the whole second. On <c>NonBacktracking</c> it
    /// takes under a millisecond, so the credential on the last line is redacted and nothing else is touched —
    /// deterministic in both directions, which is what makes it worth having next to the contention test.
    /// </remarks>
    [Fact]
    public void A_line_built_to_backtrack_quadratically_is_scrubbed_rather_than_dropped()
    {
        // "bearer" is a credential word but not a BIP39 one, so the phrase scan leaves the noise alone too.
        var noise = string.Concat(Enumerable.Repeat("bearer ", 10_000)) + "end";

        var scrubbed = SparkLogScrubber.Scrub($"{noise}\nauthorization: Bearer {Token}");

        Assert.Equal($"{noise}\nauthorization: {SparkLogScrubber.Redacted}", scrubbed);
    }

    /// <summary>
    /// The regression itself: scrubbing gives the same answer however busy the machine is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reproduces the conditions the old timeouts fired under — several runnable threads per core, each allocating
    /// enough to keep the collector suspending all of them — and checks every answer against the one the text
    /// deserves. Both of <c>Scrub</c>'s callers are in the mix, and so is a line that has something to redact, so the
    /// assertion is not only "nothing fell back" but "everything still scrubbed".
    /// </para>
    /// <para>
    /// Against the timeouts this failed ten runs out of ten, with between 4 and 15 of the 240,000 to 610,000 scrubs
    /// in each run coming back as a fallback sentence; the harness that diagnosed the flake found every one of those
    /// to be a <c>RegexMatchTimeoutException</c>, all of them in <c>SensitiveValue</c>. With no clock left to lose a
    /// race against, it has nothing to be flaky about.
    /// </para>
    /// </remarks>
    [Fact]
    public void Scrubbing_gives_the_same_answer_however_busy_the_machine_is()
    {
        (Func<string> Scrub, string Expected)[] cases =
        [
            // The flaky test's own input, through its own entry point.
            (() => SparkErrors.Describe(new ObjectDisposedException("BreezSdk")),
                "The Spark wallet for this store is no longer running."),
            (() => SparkErrors.Describe(new SdkException.SparkException(
                    $"@v1=Tree service error: verify_challenge rejected session_token: \"{Token}\"")),
                $"Tree service error: verify_challenge rejected session_token: {SparkLogScrubber.Redacted}"),
            // The log bridge's overload, whose fallback sentence is a different one.
            (() => SparkLogScrubber.Scrub($"claiming with preimage {Preimage} for payment 4b1f"),
                $"claiming with preimage {SparkLogScrubber.Redacted} for payment 4b1f"),
        ];

        var examples = new ConcurrentQueue<string>();
        var scrubs = 0L;
        var wrong = 0L;
        using var stop = new CancellationTokenSource();

        // Pure CPU burn, standing in for the rest of a parallel test run or a busy server.
        var hogs = Enumerable.Range(0, Environment.ProcessorCount * 2).Select(_ => new Thread(() =>
        {
            var x = 0UL;
            while (!stop.IsCancellationRequested)
                x = x * 6364136223846793005UL + 1442695040888963407UL;
            GC.KeepAlive(x);
        }) { IsBackground = true }).ToList();

        var callers = Enumerable.Range(0, Environment.ProcessorCount * 4).Select(_ => new Thread(() =>
        {
            var running = Stopwatch.StartNew();
            while (running.Elapsed < ContentionDuration)
            {
                foreach (var (scrub, expected) in cases)
                {
                    var actual = scrub();
                    Interlocked.Increment(ref scrubs);
                    if (actual != expected && Interlocked.Increment(ref wrong) <= 3)
                        examples.Enqueue($"\"{actual}\" instead of \"{expected}\"");
                }
            }
        }) { IsBackground = true }).ToList();

        hogs.ForEach(t => t.Start());
        callers.ForEach(t => t.Start());
        callers.ForEach(t => t.Join());
        stop.Cancel();
        hogs.ForEach(t => t.Join());

        Assert.True(
            wrong == 0,
            $"Under contention, {wrong} of {scrubs} scrubs gave a different answer, e.g. " + string.Join("; ", examples));
    }

    private static List<(string Name, Regex Regex)> ScrubberPatterns() =>
        typeof(SparkLogScrubber)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(Regex))
            .Select(field => (field.Name, (Regex)field.GetValue(null)!))
            .ToList();

    /// <summary>
    /// Fixed cases where the engines have been seen to disagree, then seeded token soup.
    /// </summary>
    private static List<string> DifferentialInputs()
    {
        List<string> inputs =
        [
            // SensitiveValue on NonBacktracking matches from apikey instead of privkey here, leaving privkey's value.
            "\"]privkey:apikey: \"",
            "\"(APIKEY:preimage: e",
            "\"]Mnemonic:apikey: t",
            "\" preimage:privkey= \"",
            "\".privkey:\"PreImage\":\\",
            // The shapes HeaderCredential exists for, including the multi-line ones that made it quadratic.
            "authorization: Bearer abcdef0123456789",
            "cookie: sid=1; bearer x",
            "bearer bearer bearer end\nauthorization: Bearer x",
            "set-cookie \nauthorization: y",
            "authorization: x\n",
            "a\nauthorization: x\n",
        ];

        string[] tokens =
        [
            "authorization", "Authorization", "bearer", "BEARER", "cookie", "set-cookie", "Set-Cookie", "coo\u212Aie",
            "\u017Fet-cookie", "xbearer", "cookies", "setcookie", "preimage", "PreImage", "privkey", "private_key",
            "apikey", "api_key", "session_token", "mnemonic", "passphrase", "auth_token",
            ":", ": ", "=", " = ", " ", "\t", "\n", "\r\n", "\r", "\u0085", "\"", "\\\"", "'", "\\", "]", ")", "}",
            ",", ";", "-", "_", "(", "Some(", "Bearer abc", "abc", "x", "[redacted]", Token, Preimage,
        ];
        var random = new Random(20260924);
        var soup = new StringBuilder();
        for (var i = 0; i < 20_000; i++)
        {
            soup.Clear();
            var length = random.Next(1, 16);
            for (var t = 0; t < length; t++)
                soup.Append(tokens[random.Next(tokens.Length)]);
            inputs.Add(soup.ToString());
        }

        return inputs;
    }

    private static string Describe(MatchCollection matches) =>
        string.Concat(matches.Select(m =>
            $"[{m.Index},{m.Length}"
            + string.Concat(m.Groups.Values.Skip(1).Select(g => g.Success ? $" {g.Index},{g.Length}" : " -"))
            + "]"));

    private static string Escape(string text) =>
        string.Concat(text.Select(c => c switch
        {
            '\\' => "\\\\",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            < ' ' or > '~' => $"\\u{(int)c:X4}",
            _ => c.ToString()
        }));
}

/// <summary>
/// The collection <see cref="SparkLogScrubberTests"/> runs in, alone.
/// </summary>
/// <remarks>
/// <c>DisableParallelization</c> makes xunit run it after every parallel collection has finished, so the contention
/// test's deliberate CPU saturation lands on nothing else. It has no fixture: running alone is the whole of what it
/// is for.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SparkLogScrubberContentionCollection
{
    public const string Name = "Spark log scrubber under contention";
}
