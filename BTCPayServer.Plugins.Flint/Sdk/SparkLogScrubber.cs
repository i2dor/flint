using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NBitcoin;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// Removes credential-shaped material from an SDK log line before it is forwarded anywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the SDK actually emits, measured rather than assumed.</b> A throwaway regtest wallet was connected
/// with the Rust subscriber at each level and every line was read.
/// </para>
/// <list type="bullet">
/// <item><description>At <c>info</c> and <c>debug</c> — the level Breez's "moving to production" checklist
/// asks for — nothing secret appeared. The content is SQLite migration DDL, gRPC request structs carrying the
/// wallet's <em>public</em> identity key, TLS handshake details, operator hostnames and sync summaries. No
/// mnemonic, no preimage, no private key, no bearer token.</description></item>
/// <item><description>At <c>trace</c> the service provider's GraphQL <c>session_token</c> is logged in full,
/// inside raw response bodies, twice per authentication. That is a live bearer credential for the merchant's
/// wallet. <c>SparkLogging</c> therefore refuses a filter that enables trace at all; this class is the second
/// line rather than the first.</description></item>
/// </list>
/// <para>
/// <b>What that measurement could not cover.</b> The probe wallet was unfunded and no payment was ever made
/// through it, so the lines a completed Lightning receive produces — the ones that would carry a preimage —
/// were never emitted and remain unaudited. The redactions below are written against that gap: they key on
/// the names the SDK's own schema uses (<c>preimage</c>, and the SQLite columns beside it) rather than on
/// anything that was observed, precisely because the observation is incomplete.
/// </para>
/// <para>
/// <b>Why not redact by shape alone.</b> A 64-character hex run is a preimage, a payment hash, a transaction
/// id or half a public key, and a 66-character one is an identity pubkey — all of which are either public or
/// the entire diagnostic value of the line. Redacting every long hex run would leave logs that cannot answer
/// the questions they exist for. So the rule is contextual: redact a value when the name attached to it says
/// it is a secret. The two exceptions are shapes that can only be secrets — an extended private key, and a
/// run of BIP39 words.
/// </para>
/// <para>
/// <b>That was re-examined when an external audit raised it, and the answer did not change.</b> The audit
/// observed correctly that a bare 64-hex preimage with no name beside it survives this class, and left the
/// call open. Three things decide it:
/// </para>
/// <list type="number">
/// <item><description><b>The shape carries no information.</b> A preimage, a payment hash, a txid and a
/// public key's x-coordinate are all exactly 32 bytes of hex. There is no test that separates them, so a
/// shape rule is not "redact secrets", it is "redact all four". Two of those four — the txid and the payment
/// hash — are how an operator finds a merchant's money and how a payment is correlated between this plugin's
/// log and the SDK's. A sweep sits at <em>Sent</em> with its txid and nothing else; blanking it would take
/// away the only handle on funds in flight.</description></item>
/// <item><description><b>It would not protect the file where a preimage would actually persist.</b> This class
/// runs on the C# bridge and only touches lines being forwarded into BTCPay's logger.
/// <c>&lt;DataDir&gt;/Plugins/Spark/logs/sdk.log</c> is written by the Rust subscriber and never passes
/// through here at all. So the cost — a blinded operator log — is paid in full, and the benefit does not
/// reach the artefact that outlives the process.</description></item>
/// <item><description><b>The measured content is full of legitimate 64-hex.</b> At <c>info</c> and
/// <c>debug</c>, the levels this plugin will actually run at, the probe's lines are gRPC request structs,
/// operator hostnames and sync summaries — identifiers throughout. A scrubber that turns those into
/// <c>[redacted]</c> is one an operator switches off, and a scrubber that is switched off redacts
/// nothing.</description></item>
/// </list>
/// <para>
/// So: no shape-based redaction of bare hex. The unaudited gap is closed instead by the <em>level</em> guard —
/// <c>SparkLogging.ClampFilter</c>, which is the only mechanism that reaches <c>sdk.log</c> — and by keeping
/// this class keyed on names. What would change the decision is evidence rather than argument: a funded-wallet
/// observation showing the SDK emitting a preimage with no name attached. The funded-regtest suite
/// (<c>Tests/FundedRegtest/</c>) produces exactly that observation, as its <c>preimage-audit.md</c> artefact.
/// </para>
/// <para>
/// <b>The bounded half of that alternative has since been taken.</b> Rust's <c>tracing</c> can format a field
/// through <c>Display</c> rather than as a key/value pair, which puts a sensitive name and its value on either
/// side of a space instead of a colon — <c>received preimage 9f2c1b7a…</c> — and the separator requirement let
/// it through. <see cref="SensitiveHexValue"/> now accepts whitespace as well, but only in front of exactly 64
/// hex characters. That is still name-keyed, so it is not the shape rule rejected above: a bare 64-hex run
/// with no sensitive name before it is untouched, and the txid an operator needs to find funds in flight
/// survives. Both patterns draw their names from <see cref="SensitiveNames"/> so neither can quietly fall
/// behind the other.
/// </para>
/// </remarks>
internal static class SparkLogScrubber
{
    /// <summary>What a redacted value is replaced with. Distinctive, so its presence is greppable.</summary>
    internal const string Redacted = "[redacted]";

    /// <summary>
    /// The names whose value is never safe to log.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="SensitiveValue"/> and <see cref="SensitiveHexValue"/> so the two cannot drift.
    /// A name added to one and not the other is silently unredacted on whichever shape was missed, and the
    /// repository has already paid for that lesson once: the storage directory was world-readable for as long
    /// as it was, because the hardening sat private beside the only caller that used it.
    /// </remarks>
    private const string SensitiveNames =
        """
          mnemonic | seed_?phrase | recovery_?phrase | passphrase
        | preimage
        | private_?key | priv_?key | secret_?key | signing_?key | master_?secret
        | api_?key | session_?token | access_?token | refresh_?token | auth_?token
        """;

    /// <summary>
    /// A sensitive name, a <c>:</c> or <c>=</c>, and the value that follows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The separator is what distinguishes a value from a mention. The SDK's migration DDL contains
    /// <c>preimage TEXT,</c> and <c>json_extract(details, '$.Lightning.preimage')</c>; neither is a secret and
    /// neither matches.
    /// </para>
    /// <para>
    /// The value alternation prefers a quoted string, then falls back to a bare run. The bare run deliberately
    /// stops at a closing bracket so a Rust <c>Some("…")</c> wrapper loses its contents rather than its
    /// terminator — the point is that the secret is gone, not that the line stays pretty.
    /// </para>
    /// </remarks>
    private static readonly Regex SensitiveValue = new(
        $$"""
        (?ix)
        ( \\?"? \b (?: {{SensitiveNames}} ) \b \\?"? \s* [:=] \s* )
        (?: \\?" [^"\\]* \\?" | ' [^']* ' | [^\s,;}\]\)]+ )
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// A sensitive name, whitespace, and a 64-character hex run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rust's <c>tracing</c> does not always emit a key/value pair. A line formatted through <c>Display</c> —
    /// <c>received preimage 9f2c1b7a…</c> — carries the name and the secret with nothing but a space between
    /// them, and <see cref="SensitiveValue"/> requires a <c>:</c> or <c>=</c>, so it passes straight through.
    /// </para>
    /// <para>
    /// This stays <em>name-keyed</em>, which is the whole reason it is safe to add. It is not the shape rule
    /// that was considered and rejected above: a bare 64-hex run with no sensitive name in front of it still
    /// survives, so a txid, a payment hash and a pubkey's x-coordinate are all untouched, and the operator
    /// keeps the identifiers that make a log worth reading. The only thing that changes is that a name already
    /// on the list no longer escapes redaction by being followed by a space instead of a colon.
    /// </para>
    /// <para>
    /// Restricted to exactly 64 hex characters, with word boundaries. That is narrow enough that the prose
    /// cases which motivated requiring a separator in the first place cannot match — <c>preimage TEXT,</c> has
    /// no hex after it, and a sentence mentioning a preimage does not continue with 32 bytes of it.
    /// </para>
    /// </remarks>
    private static readonly Regex SensitiveHexValue = new(
        $$"""
        (?ix)
        ( \\?"? \b (?: {{SensitiveNames}} ) \b \\?"? \s+ )
        \b [0-9a-f]{64} \b
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// An extended private key. Unambiguous: nothing public starts with these prefixes.
    /// </summary>
    private static readonly Regex ExtendedPrivateKey = new(
        @"\b[xtyzuv]prv[1-9A-HJ-NP-Za-km-z]{50,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// A credential whose value is the rest of the line rather than a delimited token.
    /// </summary>
    /// <remarks>
    /// An <c>Authorization</c> header's value is a scheme and then the secret, so redacting "the value" after
    /// the colon removes the word <c>Bearer</c> and leaves the token. Everything after the name goes.
    /// </remarks>
    private static readonly Regex HeaderCredential = new(
        @"(?i)\b(authorization|bearer|cookie|set-cookie)\b\s*:?\s*.*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Word-shaped tokens, used to find runs of BIP39 words.
    /// </summary>
    private static readonly Regex Word = new(
        "[A-Za-z]+", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>Shortest valid BIP39 phrase, and therefore the length a run has to reach to be redacted.</summary>
    private const int ShortestPhrase = 12;

    /// <summary>
    /// The English BIP39 wordlist, as a set.
    /// </summary>
    /// <remarks>
    /// Only English. The plugin generates and normalises English phrases and NBitcoin's other wordlists load
    /// lazily from embedded resources; adding them would cost startup time to cover a phrase this plugin
    /// cannot produce. A non-English phrase would still be caught by name wherever the SDK labelled it.
    /// </remarks>
    private static readonly HashSet<string> Bip39English =
        new(Wordlist.English.GetWords(), StringComparer.Ordinal);

    /// <summary>
    /// Returns <paramref name="line"/> with anything credential-shaped replaced.
    /// </summary>
    /// <remarks>
    /// Never throws. This runs inside a UniFFI callback from an SDK-owned thread, where an exception is how
    /// the process deadlocks — so a regex timeout, or anything else, yields a wholly redacted line rather than
    /// a partially scrubbed one. Losing a log line is always cheaper than leaking one.
    /// </remarks>
    internal static string Scrub(string? line) =>
        Scrub(line, $"{Redacted} (a Spark SDK log line could not be scrubbed and was dropped)");

    /// <summary>
    /// The same, with the caller's own sentence for the total-redaction case.
    /// </summary>
    /// <remarks>
    /// Not every caller writes to the operator's log. <c>SparkErrors.Describe</c> scrubs text that lands in
    /// merchant-facing banners and stored records, where "a Spark SDK log line could not be scrubbed" is both
    /// the wrong noun and a second sentence nobody asked for next to the failure it accompanies — so the sink
    /// supplies its own fallback instead of inheriting the log bridge's.
    /// </remarks>
    internal static string Scrub(string? line, string fallback)
    {
        if (string.IsNullOrEmpty(line))
            return string.Empty;

        try
        {
            var scrubbed = HeaderCredential.Replace(line, match => match.Groups[1].Value + ": " + Redacted);
            scrubbed = SensitiveValue.Replace(scrubbed, match => match.Groups[1].Value + Redacted);
            scrubbed = SensitiveHexValue.Replace(scrubbed, match => match.Groups[1].Value + Redacted);
            scrubbed = ExtendedPrivateKey.Replace(scrubbed, Redacted);
            scrubbed = RedactPhrases(scrubbed);
            return scrubbed;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Replaces every maximal run of twelve or more space-separated BIP39 words.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scanned as runs rather than matched as one pattern, which is not a stylistic choice. A regex for
    /// "twelve or more short lowercase words" is greedy, so on <c>failed to derive from &lt;phrase&gt; on
    /// regtest</c> it swallows the surrounding prose into the match — and then the all-words-are-BIP39 check
    /// fails on that prose and the phrase is emitted intact. Walking the tokens finds the run that is actually
    /// a phrase, whatever surrounds it.
    /// </para>
    /// <para>
    /// A single space is required between words, which is what a mnemonic looks like — including inside the
    /// quotes of <c>mnemonic="…"</c> — and which keeps a column of unrelated words in a formatted table from
    /// being read as one.
    /// </para>
    /// </remarks>
    private static string RedactPhrases(string line)
    {
        var words = Word.Matches(line);
        if (words.Count < ShortestPhrase)
            return line;

        var runStart = 0;
        var runEnd = 0;
        var runLength = 0;

        // One past the end, so a run that reaches the end of the line closes through the same branch.
        for (var i = 0; i <= words.Count; i++)
        {
            var word = i < words.Count ? words[i] : null;
            var extendsRun = word is not null
                             && Bip39English.Contains(word.Value)
                             && runLength > 0
                             && word.Index == runEnd + 1;

            if (extendsRun)
            {
                runLength++;
                runEnd = word!.Index + word.Length;
                continue;
            }

            if (runLength >= ShortestPhrase)
            {
                // The scan restarts against the shortened string rather than tracking an offset: a line
                // carrying two phrases is not a case worth extra arithmetic for, and lines are short.
                return RedactPhrases(
                    string.Concat(line.AsSpan(0, runStart), Redacted, line.AsSpan(runEnd)));
            }

            if (word is not null && Bip39English.Contains(word.Value))
            {
                runStart = word.Index;
                runEnd = word.Index + word.Length;
                runLength = 1;
            }
            else
            {
                runLength = 0;
            }
        }

        return line;
    }
}
