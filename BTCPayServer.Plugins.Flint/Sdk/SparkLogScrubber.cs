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
/// <para>
/// <b>Every pattern is bounded by the work it does, never by a clock.</b> Each one used to carry a 50 ms match
/// timeout as its guard against catastrophic backtracking, with <see cref="Scrub(string?, string)"/> turning a
/// timeout into total redaction. That guard measured the wrong thing: .NET times a match in elapsed time, not in
/// work, so a thread descheduled — or suspended for a garbage collection — in the middle of a match that needed
/// microseconds lost the race just the same. With six runnable threads per core, about one call in two hundred
/// thousand on the text of an ordinary <c>ObjectDisposedException</c> came back as the fallback, every one of them a
/// <see cref="SensitiveValue"/> timeout on a sentence with nothing in it to redact; without the contention, none
/// did in two million. So a merchant was now and then told that an ordinary error "could not be shown safely", an
/// operator's log lost lines for no reason, and a unit test flaked.
/// </para>
/// <para>
/// So the bound moved from the clock to the engine. Every pattern but one is linear on the backtracking engine as
/// written, and each one says why; <see cref="HeaderCredential"/> is not, and runs on <c>NonBacktracking</c>, which is
/// linear by construction. None carries a timeout. The catch in <c>Scrub</c> is unchanged and still turns anything
/// unexpected into total redaction; what it no longer sees is a busy machine. <c>SparkLogScrubberTests</c> finds
/// every pattern this class holds by reflection and refuses a timeout on any.
/// </para>
/// <para>
/// <b>Why not <c>NonBacktracking</c> for everything.</b> It would be the tidier rule, and on the runtime this was
/// measured on (.NET 10.0.11) it is the wrong one: that engine does not always return the leftmost match. On
/// <see cref="SensitiveValue"/>'s pattern it matches <c>"]privkey:apikey: "</c> from <c>apikey</c> rather than from
/// <c>privkey</c>, which leaves <c>privkey</c>'s value in the line; reduced to <c>"?[ab]:\s*(?:"[^"]*"|\S+)</c>, it
/// matches <c>"]a:b: "</c> at index 4 where the backtracking engine, correctly, matches at 2. It drops captures too,
/// which is why <see cref="HeaderCredentialOnEveryLine"/> is not on it. A redactor that sometimes starts late is one
/// that sometimes leaks, so the engine is used only where the backtracking one cannot be bounded, and only after a
/// differential check against it — a sample of which <c>SparkLogScrubberTests</c> keeps running, because whether the
/// two engines agree is a property of the runtime, not of this file.
/// </para>
/// <para>
/// <b>Passes are added at the end, not folded into the ones before.</b> Each pass runs on what the previous ones
/// left, so changing what an early pass consumes changes what every later one can see — and a pass that hides more
/// in one place can make a later one hide less in another. A new pass appended after the rest replaces only what
/// it matches, so it can hide more than the pipeline before it but never less. That is why
/// <see cref="HeaderCredentialOnEveryLine"/> runs last rather than replacing <see cref="HeaderCredential"/>, which
/// runs first.
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
    /// <para>
    /// Linear on the backtracking engine, which is why it needs no timeout. Every loop in it is followed by
    /// something the loop cannot match, so the engine never hands characters back to one; and the only text an
    /// attempt scans beyond what it keeps is a quoted value that turns out to be unterminated, which runs to the
    /// next quote — so no two attempts scan the same stretch. It stays off <c>NonBacktracking</c> on purpose: this
    /// is the pattern that engine gets wrong (see the class remarks).
    /// </para>
    /// </remarks>
    private static readonly Regex SensitiveValue = new(
        $$"""
        (?ix)
        ( \\?"? \b (?: {{SensitiveNames}} ) \b \\?"? \s* [:=] \s* )
        (?: \\?" [^"\\]* \\?" | ' [^']* ' | [^\s,;}\]\)]+ )
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout);

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
    /// <para>
    /// Linear on the backtracking engine for the reasons <see cref="SensitiveValue"/> is, and more simply: the
    /// value it looks for is a fixed 64 characters.
    /// </para>
    /// </remarks>
    private static readonly Regex SensitiveHexValue = new(
        $$"""
        (?ix)
        ( \\?"? \b (?: {{SensitiveNames}} ) \b \\?"? \s+ )
        \b [0-9a-f]{64} \b
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout);

    /// <summary>
    /// An extended private key. Unambiguous: nothing public starts with these prefixes.
    /// </summary>
    /// <remarks>
    /// Linear on the backtracking engine: the leading <c>\b</c> means an attempt can only begin where a word does,
    /// and the key run is the last thing in the pattern, so each word is scanned at most once, by the attempt that
    /// begins at its start.
    /// </remarks>
    private static readonly Regex ExtendedPrivateKey = new(
        @"\b[xtyzuv]prv[1-9A-HJ-NP-Za-km-z]{50,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout);

    /// <summary>
    /// The names whose value is the rest of their line: the credential headers, and the scheme word that starts one.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="HeaderCredential"/> and by both halves of <see cref="HeaderCredentialOnEveryLine"/> — the
    /// name that starts a match and the name that carries it onto the next line — for the reason
    /// <see cref="SensitiveNames"/> is shared: a name added to one and not the others is silently unredacted there.
    /// </remarks>
    private const string CredentialHeaders = "authorization | bearer | cookie | set-cookie";

    /// <summary>
    /// A credential whose value is the rest of the line rather than a delimited token — where that line is the last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <c>Authorization</c> header's value is a scheme and then the secret, so redacting "the value" after
    /// the colon removes the word <c>Bearer</c> and leaves the token. Everything after the name goes.
    /// </para>
    /// <para>
    /// <b>Only where the value reaches the end of the text, and kept that way on purpose.</b> Without
    /// <c>Multiline</c> the <c>$</c> means the end of the text, so this matches a credential on the last line, or a
    /// name whose value the <c>\s*</c> reaches across line breaks from further up. On its own that let a credential on
    /// any other line through; <see cref="HeaderCredentialOnEveryLine"/> closes that as a separate pass at the end,
    /// rather than here, because every pass after this one runs on what this one leaves. Redacting to the end of
    /// every line here was tried: it took the rest of header lines that later passes needed — a
    /// <c>private_key:</c> whose value was on the next line, the first half of a phrase broken across the break — and
    /// of a million random inputs, 328 came through with a secret that this pattern, left alone, let them catch.
    /// </para>
    /// <para>
    /// On <c>NonBacktracking</c>, because the backtracking engine cannot bound it. On a line with a newline before
    /// the end, <c>.*</c> runs to that newline, fails, and gives it back a character at a time — once for every
    /// credential word in front of it. That is quadratic: ten thousand <c>bearer </c> ahead of a newline took over a
    /// second on the backtracking engine, which the old timeout turned into a dropped line and no timeout would turn
    /// into a stalled SDK callback thread. On <c>NonBacktracking</c> the same line takes under a millisecond.
    /// </para>
    /// <para>
    /// Safe on that engine where <see cref="SensitiveValue"/> is not because it begins with a word boundary rather
    /// than an optional character: the late match was only ever seen with an optional start, and dropping
    /// <see cref="SensitiveValue"/>'s made it go away. Some sixteen million inputs, random and exhaustive, agreed
    /// with the backtracking engine match for match before it moved; <c>SparkLogScrubberTests</c> keeps a seeded
    /// sample of them running, multi-line shapes included.
    /// </para>
    /// </remarks>
    private static readonly Regex HeaderCredential = new(
        $$"""
        (?ix)
        \b ( {{CredentialHeaders}} ) \b \s* :? \s* .* $
        """,
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout);

    /// <summary>
    /// The same credential on any line, run last, over what every other pass has left.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every line, not just the last.</b> <see cref="HeaderCredential"/> only matches where a value reaches the
    /// end of the text, so <c>authorization: Bearer …</c> with any line after it — even a second trailing newline —
    /// reached the log and the merchant's error text, token and all. Here <c>.</c> stops at a line feed, so the
    /// closing <c>.*</c> is the rest of the line, with deliberately no <c>$</c> after it. A carriage return is
    /// ordinary text to <c>.</c>, so on <c>\r\n</c> text it goes with the redacted value while the line break
    /// survives: one character too many is the direction this class errs in.
    /// </para>
    /// <para>
    /// <b>Last, so that it can only add.</b> A pass replaces what it matches and nothing else, so one at the end can
    /// hide more than the passes before it left visible but never less, and they run exactly as they did before it
    /// existed. First, it took the rest of lines those passes needed — see <see cref="HeaderCredential"/> for what
    /// that cost. Last, over a million random inputs with secrets planted in them, it never left one visible that
    /// the pipeline without it had hidden, and on text with no line feed in it, it changes nothing at all.
    /// </para>
    /// <para>
    /// <b>A line that ends in one of these names runs on into the next line with anything on it.</b> That is the loop
    /// in the middle: when nothing but spaces and a colon follow the last name on a line, the line break, any blank
    /// lines and the next line's indent are taken too, and the next line is the value — for as many lines as that
    /// holds. A folded header puts the value there (<c>authorization: Bearer</c>, and the token indented below it), and
    /// so does a YAML-style dump; the rest of the line alone would let that token through. The cost is that a sentence
    /// ending in one of these words loses the line after it — the trade <c>SparkErrors.Describe</c> names for these
    /// patterns eating to the end of a line. <c>SparkLogBridgeTests</c> pins both halves.
    /// </para>
    /// <para>
    /// <b>Linear on the backtracking engine, which is why it carries no timeout.</b> Nothing after the name can fail —
    /// the loop either takes another line or stops, and the closing <c>.*</c> matches anything — so an attempt that
    /// finds a name keeps everything it scans, and the one search inside an attempt, for the last name on a line, is a
    /// single pass back over that line. The whitespace around the colon is split so that no two loops can claim the
    /// same character: written as <c>[^\S\n]*:?[^\S\n]*</c>, a long run of spaces with no colon in it gave the engine
    /// every way of dividing the run between the two, and 128 KB of them took nine seconds.
    /// </para>
    /// <para>
    /// Not on <c>NonBacktracking</c>, which would have bounded it without that care: the engine loses the capture this
    /// pattern keeps the name in whenever a match takes the text's final line break — reduced, <c>(c)\b\r\n</c>
    /// matches <c>"c\r\n"</c> with group 1 missing, where the backtracking engine has it — which it did for one input
    /// in eighty of a million random ones here.
    /// </para>
    /// </remarks>
    private static readonly Regex HeaderCredentialOnEveryLine = new(
        $$"""
        (?ix)
        \b ( {{CredentialHeaders}} ) \b
        (?: (?: .* \b (?: {{CredentialHeaders}} ) \b )? [^\S\n]* (?: : [^\S\n]* )? \n \s* )*
        .*
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout);

    /// <summary>
    /// Word-shaped tokens, used to find runs of BIP39 words.
    /// </summary>
    /// <remarks>Linear on the backtracking engine: one character class, each run of it scanned once.</remarks>
    private static readonly Regex Word = new(
        "[A-Za-z]+", RegexOptions.Compiled | RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout);

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
    /// the process deadlocks — so anything that goes wrong yields a wholly redacted line rather than a
    /// partially scrubbed one. Losing a log line is always cheaper than leaking one. What can no longer go
    /// wrong is time: no pattern here carries a match timeout, so a given line scrubs the same way however
    /// busy the machine is (see the class remarks).
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
            // Last on purpose: it can only add to what the passes above hid, never change what they were given.
            scrubbed = HeaderCredentialOnEveryLine.Replace(scrubbed, match => match.Groups[1].Value + ": " + Redacted);
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
