using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// One asset a chain carries, as the provider describes it.
/// </summary>
/// <param name="Symbol">
/// The value stored in <c>SweepSettings.EvmAsset</c> and matched against a route's <c>Asset</c>. The provider's
/// own spelling, because that is what the match compares against — and the match is exact, so <c>USDT</c>,
/// <c>USDT0</c> and <c>USD₮0</c> are three assets rather than one.
/// </param>
/// <param name="Name">
/// The asset's full name (<c>Tether</c>, <c>Bridged USDC</c>), for a picker to show alongside the symbol. Purely
/// how it reads; nothing matches on it.
/// </param>
/// <param name="Decimals">
/// <b>Diagnostic only. Nothing computes with this.</b> It is read off the route rather than assumed because
/// assuming is how this goes wrong — USDT is 6 decimals on every chain here except BSC, where it is 18, and
/// tBTC is 18 where cbBTC is 8. Carrying the provider's own answer means a future caller has that answer rather
/// than inventing one. The <em>send</em> path never reads it: <see cref="Sdk.SparkAmounts"/> takes decimals from
/// the live <c>CrossChainRoutePair</c> it is about to send on, which is the only copy that cannot be stale.
/// Zero means "not reported", which is what an entry synthesised for a store's own saved asset holds.
/// </param>
public sealed record CrossChainAsset(string Symbol, string Name, int Decimals);

/// <summary>
/// One cross-chain destination the settings page offers: a chain, and the assets it carries.
/// </summary>
/// <param name="Chain">
/// The value stored in <c>SweepSettings.EvmChain</c> and matched against a route's <c>Chain</c>. Spelled the
/// way the provider spells it, because that is what the match compares against.
/// </param>
/// <param name="Label">How the chain reads in a picker. Cosmetic; nothing matches on it.</param>
/// <param name="AddressExplorer">
/// Where an address on this chain can be looked up, as a base URL an address is appended to — or null when this
/// build does not know one.
/// </param>
/// <remarks>
/// <para>
/// <b><see cref="AddressExplorer"/> is display-only</b>, and optional in both directions: a chain without one is
/// offered exactly like any other, and a link is simply not drawn. Nothing routes, validates or sends on it.
/// </para>
/// <para>
/// It exists because a delivered cross-chain sweep has nothing else to point at. The SDK reports no
/// destination-chain transaction hash — Orchestra's conversion detail carries status, quote and order ids, the
/// delivered amount, the recipient, the chain and the asset, and no hash — so the most a merchant chasing a
/// delivery can be given is their own address on the destination chain's explorer. It lives on this record
/// rather than in a table of its own because a second list of chains would drift from this one.
/// </para>
/// </remarks>
public sealed record CrossChainDestination(
    string Chain,
    string Label,
    IReadOnlyList<CrossChainAsset> Assets,
    string? AddressExplorer = null);

/// <summary>
/// What the two pickers on the sweep page render, for one store, at one moment.
/// </summary>
/// <remarks>
/// <para>
/// A single object rather than four properties on the view model, because the interesting facts about these
/// pickers are relationships between those properties rather than properties in their own right:
/// <see cref="Selected"/> is always in <see cref="Chains"/>, <see cref="SelectedAsset"/> is always in
/// <see cref="Assets"/>, and <see cref="Assets"/> belongs to <see cref="Selected"/> rather than to whatever the
/// form happens to hold. Computed together, those hold by construction. Set independently, they hold until
/// somebody sets three of the four.
/// </para>
/// <para>
/// It matters because an <c>option</c> that is not present is not merely missing: the browser selects the first
/// option instead, and the next save posts <em>that</em>. A picker that cannot render a store's destination
/// silently changes it.
/// </para>
/// </remarks>
public sealed class CrossChainPicker
{
    private CrossChainPicker(
        IReadOnlyList<CrossChainDestination> chains,
        CrossChainDestination selected,
        CrossChainAsset selectedAsset)
    {
        Chains = chains;
        Selected = selected;
        SelectedAssetEntry = selectedAsset;
    }

    /// <summary>The chains offered, including this store's own if the catalogue does not list it.</summary>
    public IReadOnlyList<CrossChainDestination> Chains { get; }

    /// <summary>The chain entry the picker opens on. Always one of <see cref="Chains"/>.</summary>
    public CrossChainDestination Selected { get; }

    /// <summary>The asset entry the picker opens on. Always one of <see cref="Assets"/>.</summary>
    public CrossChainAsset SelectedAssetEntry { get; }

    /// <summary>The assets offered on <see cref="Selected"/>. Never empty.</summary>
    public IReadOnlyList<CrossChainAsset> Assets => Selected.Assets;

    /// <summary>
    /// The chain value an <c>option</c> must carry to be selected — the catalogue's spelling, not the store's.
    /// </summary>
    /// <remarks>
    /// The route table is matched case-insensitively, so a store saved as <c>POLYGON</c> is on the same route as
    /// one saved as <c>polygon</c> — but an <c>option</c> is selected by exact value, so comparing the raw field
    /// against the list would match nothing and leave the browser selecting whatever came first. That store
    /// would then post arbitrum the next time anything on the page was saved: a destination it never chose, on a
    /// chain the merchant is not watching. Saving from the page therefore rewrites such a value to the
    /// catalogue's own spelling, which is a normalisation and not a change of destination.
    /// </remarks>
    public string SelectedChain => Selected.Chain;

    /// <inheritdoc cref="SelectedChain"/>
    public string SelectedAsset => SelectedAssetEntry.Symbol;

    /// <summary>
    /// The picker with nothing fetched and nothing configured: the static floor, on its default destination.
    /// </summary>
    /// <remarks>
    /// It exists so that a view model nobody filled in renders the floor rather than two empty selects. An empty
    /// select posts a blank chain and a blank asset, and a blank asset is stored and then silently resolved to
    /// the default at the point of sending — a destination the merchant never chose, arrived at by omission.
    /// </remarks>
    public static CrossChainPicker Offline { get; } = Over(CrossChainCatalog.Fallback, null, null);

    /// <summary>
    /// What to offer a store whose settings hold <paramref name="savedChain"/> and <paramref name="savedAsset"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The saved values are always in the returned lists.</b> A store configured before a chain left the
    /// catalogue — or through Greenfield, which takes either field as free text — must be able to save an edit to
    /// an unrelated field without its destination changing under it. A picker that silently substituted the
    /// nearest option would redirect that store's money on its next sweep, and one that refused to render the
    /// value would block every other setting on the page behind a chain the merchant may well still want.
    /// </para>
    /// <para>
    /// That is also why the catalogue being stale, or being the floor, is survivable: a fetch that never
    /// succeeded cannot take a destination away from a store that already has one.
    /// </para>
    /// <para>
    /// Blanks resolve to the defaults rather than to an empty option, mirroring
    /// <see cref="SweepSettings.EffectiveCrossChainChain"/>: nothing stored still means arbitrum is what a sweep
    /// would use, and showing an unchosen picker would say otherwise.
    /// </para>
    /// </remarks>
    public static CrossChainPicker Over(
        IReadOnlyList<CrossChainDestination> catalogue, string? savedChain, string? savedAsset)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var chain = CrossChainCatalog.EffectiveChain(savedChain);
        var asset = CrossChainCatalog.EffectiveAsset(savedAsset);

        var known = catalogue.FirstOrDefault(entry => CrossChainCatalog.Matches(entry.Chain, chain));

        if (known is null)
        {
            // Labelled and named with the merchant's own spelling, because there is nothing else to call it, and
            // with zero decimals because nothing here knows what this asset's are.
            var mine = new CrossChainDestination(chain, chain, [new CrossChainAsset(asset, asset, 0)]);
            return new CrossChainPicker([.. catalogue, mine], mine, mine.Assets[0]);
        }

        var carried = known.Assets.FirstOrDefault(candidate =>
            CrossChainCatalog.Matches(candidate.Symbol, asset));

        if (carried is not null)
            return new CrossChainPicker(catalogue, known, carried);

        // Appended rather than prepended: a saved asset this build does not list is the exception, and the
        // chain's own assets should still read in the order the projection put them in.
        var added = new CrossChainAsset(asset, asset, 0);
        var widened = known with { Assets = [.. known.Assets, added] };

        return new CrossChainPicker(
            [.. catalogue.Select(entry => CrossChainCatalog.Matches(entry.Chain, chain) ? widened : entry)],
            widened,
            added);
    }
}

/// <summary>
/// What the sweep page's chain and asset pickers offer: the provider's live route table, cached, with a static
/// floor underneath it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fetched, because hardcoding it is wrong and the provider says so.</b> The supported set changes as chains
/// and assets are added, and <c>GET /v1/orchestration/routes</c> is public, unauthenticated, and returns every
/// live pair with its chain id, contract address, decimals and display names. The list this replaced named six
/// chains and one asset, and was already missing five chains and — on every chain it did list — USDC, the most
/// widely carried asset in the table.
/// </para>
/// <para>
/// <b>Cached hard, because a provider round trip per page render is the amplification the audit flagged</b>
/// (ApiSurface F1: request-thread fan-out on a GET, which any store viewer can loop). Three things follow from
/// that, and none of them is optional:
/// </para>
/// <list type="number">
/// <item><description><b>No render ever waits for the network.</b> <see cref="Snapshot"/> returns whatever is
/// already in hand and, at most, <em>schedules</em> a refresh. A cold cache renders the floor and upgrades
/// itself on a later request; it does not render slowly.</description></item>
/// <item><description><b>At most one fetch per interval, whatever the request rate.</b> A single gate plus a
/// next-attempt stamp means a thousand concurrent renders cause one fetch, and an endpoint that is already down
/// is not retried by every reload. That is what makes this safe on a GET where an SDK call was
/// not.</description></item>
/// <item><description><b>The projection is cached, not the body.</b> The response is ~1.9 MB and 2,851 routes,
/// of which about a hundred are reachable from Spark; parsing it per render would merely move the waste from
/// the network to the CPU. It is parsed once per refresh, and the handful of entries that survive is what lives
/// in memory.</description></item>
/// </list>
/// <para>
/// <b>Non-authoritative, in both directions.</b> <see cref="CrossChainRouteResolver"/> re-reads the SDK's live
/// route table before every send and refuses what it does not find, and the settings validator still only
/// requires the two fields to be non-empty — so a route missing from a stale catalogue is not vetoed, and one
/// wrongly present is not authorised. Greenfield takes both fields as free text and continues to. A chain
/// missing here costs a merchant a picker entry; a chain wrongly here costs them a refusal that names the chains
/// which do carry their asset. Neither can move money somewhere unsupported.
/// </para>
/// <para>
/// <b>EVM only, for now.</b> Spark also reaches solana, tron, ton, xrp and zcash, but the settings fields are
/// <c>EvmAddress</c>/<c>EvmChain</c> and the address check is <c>SweepDestinationResolver.TryParseEvm</c> —
/// twenty hex bytes and an EIP-55 checksum. Offering a chain whose addresses that check rejects would be
/// offering a destination the save refuses. So the projection keeps only chains that are EVM-addressed, decided
/// from the payload rather than from a list somebody has to maintain: see <see cref="IsEvmChainId"/> and
/// <see cref="IsEvmContractAddress"/>.
/// </para>
/// </remarks>
public sealed class CrossChainCatalog
{
    /// <summary>The provider's public route table. No authentication, no key, no per-store state.</summary>
    public const string RoutesUrl = "https://orchestration.flashnet.xyz/v1/orchestration/routes";

    /// <summary>The named <see cref="HttpClient"/> this reads through. See <c>SparkPlugin.Execute</c>.</summary>
    public const string HttpClientName = "spark-cross-chain-catalog";

    /// <summary>
    /// The most of a response that will be read before it is abandoned.
    /// </summary>
    /// <remarks>
    /// The real body is about 1.9 MB. This is a ceiling on a response from a host this plugin does not operate,
    /// read on a background thread inside a merchant's server: without one, a hung or hostile endpoint that
    /// streams forever is an out-of-memory fault in BTCPay rather than a picker that fell back to its floor.
    /// </remarks>
    public const long MaxResponseBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Where an address on each chain can be looked up, keyed by the provider's spelling of the chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not fetched, and deliberately incomplete.</b> The route table carries an icon path and no explorer, so
    /// there is nothing to discover here — and a wrong entry is worse than a missing one by a wide margin. A
    /// merchant chasing a delivery that has not arrived, landing on the wrong chain's explorer, is told with
    /// apparent authority that their money is not there. A merchant with no link is told nothing and keeps
    /// looking. So only explorers that are unambiguously the canonical site for their chain are listed, and a
    /// chain without one is offered exactly like any other.
    /// </para>
    /// <para>
    /// Six are the Etherscan family, each the canonical explorer for its chain. <c>snowtrace.io</c> is
    /// Avalanche's C-Chain, which is where an EVM address exists. <c>plasmascan.to</c> is Plasma's, on the
    /// owner's confirmation; it is a Blockscout deployment, hence the same <c>/address/</c> route as the rest.
    /// </para>
    /// <para>
    /// Left out on purpose: <c>hyperevm</c>, <c>monad</c>, <c>robinhood</c>, <c>sei</c> and <c>tempo</c>. Each
    /// has candidate explorers that nobody here has confirmed, and sei's in particular needs a chain query
    /// parameter that appending an address would not supply. An unverified guess belongs nowhere near this
    /// table.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> Explorers =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["arbitrum"] = "https://arbiscan.io/address/",
            ["avalanche"] = "https://snowtrace.io/address/",
            ["base"] = "https://basescan.org/address/",
            ["bsc"] = "https://bscscan.com/address/",
            ["ethereum"] = "https://etherscan.io/address/",
            ["optimism"] = "https://optimistic.etherscan.io/address/",
            ["plasma"] = "https://plasmascan.to/address/",
            ["polygon"] = "https://polygonscan.com/address/"
        };

    /// <summary>
    /// The list used when the fetch has failed or has never run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A floor, not the truth.</b> It is deliberately the smallest thing that keeps the page honest with no
    /// network at all: six chains verified by hand against the provider's table, carrying the one asset this
    /// plugin was built around. The real table is larger in both directions — more chains, and USDC on nearly
    /// all of them — and a successful fetch replaces this wholesale. Nothing should be added here to "keep it
    /// current"; that is what the fetch is for. What it must keep doing is let the settings page render and save
    /// with the network unreachable, which is the only reason it exists.
    /// </para>
    /// <para>
    /// It also fixes the order of the chains it names: <see cref="ProjectAsync"/> lists these first, in this
    /// order, and everything the provider adds after them alphabetically. So the default destination stays at
    /// the top of the picker rather than moving about as the provider's table changes.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<CrossChainDestination> Fallback =
    [
        new("arbitrum", "Arbitrum", [Tether], Explorers["arbitrum"]),
        new("optimism", "Optimism", [Tether], Explorers["optimism"]),
        new("polygon", "Polygon", [Tether], Explorers["polygon"]),
        new("plasma", "Plasma", [Tether], Explorers["plasma"]),
        new("ethereum", "Ethereum", [Tether], Explorers["ethereum"]),
        new("bsc", "BNB Smart Chain", [Tether], Explorers["bsc"])
    ];

    /// <summary>
    /// How long a successful projection is served before another fetch is scheduled.
    /// </summary>
    /// <remarks>
    /// Hours rather than minutes, because of what this list is for. It feeds a picker on a settings page a
    /// merchant visits a handful of times in the life of a store, and a chain added to the provider's table this
    /// morning being offered this evening rather than this minute costs nobody anything — while a short TTL
    /// would turn every busy BTCPay into a polling client of somebody else's public endpoint.
    /// </remarks>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    /// <summary>
    /// How long a failed fetch is left alone before another is scheduled.
    /// </summary>
    /// <remarks>
    /// Short next to <see cref="Ttl"/>, because the first fetch after a restart decides whether a merchant sees
    /// the real table or the floor, and a six-hour penalty for one refused connection is a long time to show
    /// them six chains when the provider carries thirteen. Long enough that a page somebody is reloading cannot
    /// become a retry loop against an endpoint that is already down.
    /// </remarks>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The whole fetch, including connect, response and parse.
    /// </summary>
    /// <remarks>
    /// Bounded because it runs unattended: nothing is waiting for it, so nothing else would ever notice a socket
    /// that hung. Unlike an <c>IBreezSdk</c> call this one really can be cancelled — it is an
    /// <see cref="HttpClient"/> — so the timeout ends the request rather than merely abandoning the wait.
    /// </remarks>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static CrossChainAsset Tether => new(SweepSettings.DefaultCrossChainAsset, "Tether", 6);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<CrossChainCatalog> _logger;

    /// <summary>Zero when no refresh is in flight. Half of the fan-out guard; <see cref="_state"/> is the rest.</summary>
    private int _refreshing;

    private State _state;

    public CrossChainCatalog(
        IHttpClientFactory httpClientFactory,
        TimeProvider time,
        ILogger<CrossChainCatalog> logger)
    {
        _httpClientFactory = httpClientFactory;
        _time = time;
        _logger = logger;

        // Due immediately, so the first page view schedules the first fetch. Nothing is fetched at startup: this
        // is a picker on a settings page, and making BTCPay's boot wait on a third-party HTTP endpoint to draw
        // one would be a far worse trade than showing the floor once.
        _state = new State(Fallback, Live: false, NextAttempt: DateTimeOffset.MinValue);
    }

    /// <summary>
    /// The chains to offer right now, and a refresh scheduled if one is due.
    /// </summary>
    /// <remarks>
    /// <b>Never blocks and never throws.</b> The caller is a request thread rendering a page; it gets the
    /// projection from the last successful fetch, or the last one before the network went away, or
    /// <see cref="Fallback"/> if there has never been one. Which of those it is is not something the page needs
    /// to know: every one of them is a list of chains, and none of them decides anything.
    /// </remarks>
    public IReadOnlyList<CrossChainDestination> Snapshot()
    {
        var state = Volatile.Read(ref _state);

        if (_time.GetUtcNow() >= state.NextAttempt
            && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            // Fire and forget, on purpose and exactly once. The request that happened to be first pays nothing
            // for it — this does not await, and the work runs on the thread pool — and every other request
            // meanwhile finds the gate closed and renders what is already here.
            _ = Task.Run(() => RefreshAsync(CancellationToken.None));
        }

        return state.Chains;
    }

    /// <summary>What the two pickers on the sweep page should render for this store. Never blocks.</summary>
    public CrossChainPicker PickerFor(string? savedChain, string? savedAsset) =>
        CrossChainPicker.Over(Snapshot(), savedChain, savedAsset);

    /// <summary>Whether what <see cref="Snapshot"/> returns came from the provider rather than from the floor.</summary>
    /// <remarks>
    /// For diagnostics and tests. Nothing user-facing turns on it: a merchant told "this list may be incomplete"
    /// learns nothing they can act on, and the list is not what decides their sweep in either case.
    /// </remarks>
    public bool IsLive => Volatile.Read(ref _state).Live;

    /// <summary>Whether <see cref="Snapshot"/> would schedule a refresh right now.</summary>
    /// <remarks>
    /// Internal, and it exists so the schedule rule — hours after a success, minutes after a failure, and never
    /// on the render in between — can be asserted against a stopped clock instead of by racing the thread pool.
    /// A test that could only observe this by waiting for a background task would be a test that passed on a
    /// fast machine.
    /// </remarks>
    internal bool RefreshDue => _time.GetUtcNow() >= Volatile.Read(ref _state).NextAttempt;

    /// <summary>
    /// Fetches and projects the route table, replacing what <see cref="Snapshot"/> returns if it succeeds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A failure keeps the previous answer</b> rather than reverting to the floor, and only moves the next
    /// attempt forward. A table read this morning that cannot be read now is still a far better description of
    /// what the provider carries than a list frozen at the last plugin release.
    /// </para>
    /// <para>
    /// An empty projection is treated as a failure. Zero Spark-sourced EVM routes is not a plausible state of the
    /// world; it is a payload whose shape has changed under us, and replacing a working picker with an empty one
    /// on the strength of a parse that found nothing would be trusting the parse over the evidence.
    /// </para>
    /// <para>
    /// Public so a test can drive it deterministically rather than race the scheduler; nothing in the plugin
    /// calls it directly. It is not itself gated — <see cref="Snapshot"/> is what holds the single-flight
    /// guard — because the only cost of two overlapping refreshes is that the later one wins, and a call this
    /// deliberate should do what it says rather than silently decline.
    /// </para>
    /// </remarks>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var deadline = new CancellationTokenSource(FetchTimeout, _time);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, deadline.Token);

            var client = _httpClientFactory.CreateClient(HttpClientName);

            using var response = await client
                .GetAsync(RoutesUrl, HttpCompletionOption.ResponseHeadersRead, bounded.Token)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            await using var body = await response.Content
                .ReadAsStreamAsync(bounded.Token)
                .ConfigureAwait(false);

            await using var capped = new CappedStream(body, MaxResponseBytes);

            var projected = await ProjectAsync(capped, bounded.Token).ConfigureAwait(false);

            if (projected.Count == 0)
            {
                throw new InvalidOperationException(
                    "the route table carried no EVM destination reachable from Spark");
            }

            Volatile.Write(ref _state, new State(projected, Live: true, NextAttempt: _time.GetUtcNow() + Ttl));

            _logger.LogInformation(
                "Cross-chain destination catalogue refreshed from {Url}: {Chains} chains, {Assets} assets",
                RoutesUrl, projected.Count, projected.Sum(entry => entry.Assets.Count));

            return true;
        }
        catch (Exception ex)
        {
            var state = Volatile.Read(ref _state);

            // A warning and not an error: nothing is broken by this. The picker keeps working, the page renders,
            // settings save, and every sweep still resolves its route against the SDK's own live table.
            _logger.LogWarning(ex,
                "Could not refresh the cross-chain destination catalogue from {Url}. Continuing with the {Source} "
                + "list; this decides which destinations the settings page offers and nothing else",
                RoutesUrl, state.Live ? "previously fetched" : "built-in fallback");

            Volatile.Write(ref _state, state with { NextAttempt = _time.GetUtcNow() + RetryInterval });
            return false;
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    /// <summary>
    /// Turns the provider's route table into the handful of destinations a picker can offer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every rule below is a filter, and each drops routes that genuinely exist. That is the right direction for
    /// this class to be wrong in: a destination dropped here is one a merchant cannot pick off a list, and
    /// Greenfield still takes both fields as free text. A destination wrongly kept is one they can configure and
    /// then never sweep to.
    /// </para>
    /// <list type="number">
    /// <item><description><b>Sourced from Spark.</b> The table is every pair the orchestrator carries, in both
    /// directions; the only ones this plugin can send are those leaving Spark. Both Spark source assets are
    /// taken — BTC for an ordinary store, USDB for a Stable Balance one — because the picker is configured
    /// before either is known, and the two reach almost the same set.</description></item>
    /// <item><description><b>EVM-addressed</b>, by <see cref="IsEvmChainId"/> and
    /// <see cref="IsEvmContractAddress"/> rather than by a list of chain names, which is the entire point of
    /// fetching this. It drops chains with no chain id at all (bitcoin, lightning, Spark itself), chains whose
    /// id is namespaced rather than numeric (solana, ton, xrp, zcash), and — the case a numeric id alone gets
    /// wrong — tron, whose chain id is a number but whose addresses are base58.</description></item>
    /// <item><description><b>Eligibility flags are deliberately not filtered on.</b> <c>exactOutEligible</c> and
    /// <c>fixedEligible</c> describe quote shapes, and this plugin asks for neither: a sweep is an ordinary send
    /// of an amount it already holds. Ten Spark-sourced routes carry both as false, including the only route to
    /// sei — and dropping a destination over a flag nothing here uses would be exactly the stale picker vetoing
    /// a route the provider carries.</description></item>
    /// <item><description><b>No whitespace in a symbol.</b> The page hands each chain's asset list to the
    /// browser as a space-separated attribute, so a symbol containing a space would split into two assets that
    /// do not exist. Dropping it costs one option; keeping it posts a destination nobody chose.</description></item>
    /// </list>
    /// <para>
    /// Chains are ordered by <see cref="Fallback"/> first and then alphabetically, so the default destination
    /// stays where a merchant last saw it. Assets put the plugin's default first for a reason that is not
    /// cosmetic: the page's script selects the first option when the chain a merchant switches to does not carry
    /// the asset that was showing.
    /// </para>
    /// </remarks>
    internal static async Task<IReadOnlyList<CrossChainDestination>> ProjectAsync(
        Stream json, CancellationToken cancellationToken = default)
    {
        var payload = await JsonSerializer
            .DeserializeAsync<RouteTable>(json, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        var order = Fallback
            .Select((entry, index) => (entry.Chain, index))
            .ToDictionary(pair => pair.Chain, pair => pair.index, StringComparer.OrdinalIgnoreCase);

        return (payload?.Routes ?? [])
            .Where(route => route is not null && Matches(route.SourceChain, SparkChain))
            .Select(route => route!.Destination)
            .Where(IsOffered)
            .Select(pair => pair!)
            .GroupBy(pair => pair.Chain, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => order.TryGetValue(group.Key, out var index) ? index : int.MaxValue)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CrossChainDestination(
                // The provider's own spelling, taken off a route rather than from the grouping key, which is
                // only case-insensitively equal to it.
                group.First().Chain,
                Label(group),
                Assets(group),
                Explorers.TryGetValue(group.Key, out var explorer) ? explorer : null))
            .ToList();
    }

    /// <summary>
    /// Where to look up <paramref name="address"/> on <paramref name="chain"/>, or null when there is nowhere to
    /// send the merchant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null rather than a best guess in every uncertain case — an unknown chain, a chain whose explorer this
    /// build does not know, or no address to look up. A caller renders the link or renders nothing; there is no
    /// third answer to get wrong, and a half-built URL would be a link to somebody's home page at best.
    /// </para>
    /// <para>
    /// Static, and read straight off <see cref="Explorers"/> rather than off the fetched catalogue, so a link on
    /// a sweep record does not appear and disappear with the state of a cache. The two questions are different:
    /// whether a chain can be picked is about what the provider carries now, and where to look an address up is
    /// about a sweep that already happened.
    /// </para>
    /// <para>
    /// Unlike <see cref="EffectiveChain"/> this does <em>not</em> substitute the default chain for a blank one.
    /// It is the record's own chain that says where a sweep went; guessing arbitrum for a missing one would
    /// point at a chain that was never involved.
    /// </para>
    /// </remarks>
    public static string? AddressLinkFor(string? chain, string? address)
    {
        if (string.IsNullOrWhiteSpace(chain) || string.IsNullOrWhiteSpace(address))
            return null;

        // Escaped even though an EVM address is hex by the time it is stored: this composes a URL, and a
        // composer that trusts its input is one settings edit away from being an injection point.
        return Explorers.TryGetValue(chain.Trim(), out var explorer)
            ? explorer + Uri.EscapeDataString(address.Trim())
            : null;
    }

    /// <summary>The chain a sweep would use, given what the form holds.</summary>
    /// <remarks>
    /// The same rule as <see cref="SweepSettings.EffectiveCrossChainChain"/>, over the form rather than over the
    /// stored settings — on a re-render the page is showing the merchant's rejected post, which is not what is
    /// stored.
    /// </remarks>
    public static string EffectiveChain(string? chain) =>
        string.IsNullOrWhiteSpace(chain) ? SweepSettings.DefaultCrossChainChain : chain.Trim();

    /// <inheritdoc cref="EffectiveChain"/>
    public static string EffectiveAsset(string? asset) =>
        string.IsNullOrWhiteSpace(asset) ? SweepSettings.DefaultCrossChainAsset : asset.Trim();

    /// <summary>
    /// Case-insensitive and exact, the same comparison the route resolver applies.
    /// </summary>
    /// <remarks>
    /// It has to be the same one, or the picker could show a store its saved value as "known" while the resolver
    /// treated it as a different asset — and <c>USDT0</c> is not <c>USDT</c>, which is the whole reason that
    /// comparison is exact rather than a prefix.
    /// </remarks>
    internal static bool Matches(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>The provider's name for the chain every route this plugin can send has to leave from.</summary>
    private const string SparkChain = "spark";

    /// <summary>Whether a destination is one this build can offer at all. See <see cref="ProjectAsync"/>.</summary>
    private static bool IsOffered(RoutePair? pair) =>
        pair is not null
        && !string.IsNullOrWhiteSpace(pair.Chain)
        && !string.IsNullOrWhiteSpace(pair.Asset)
        // A symbol with a space in it cannot survive the page's own encoding of the per-chain asset list.
        && !pair.Asset.Any(char.IsWhiteSpace)
        && pair.Decimals >= 0
        && IsEvmChainId(pair.ChainId)
        && IsEvmContractAddress(pair.ContractAddress);

    /// <summary>
    /// Whether a chain id is an EVM one: a plain decimal number, as EIP-155 defines them.
    /// </summary>
    /// <remarks>
    /// The provider reports a chain id for nearly everything it carries, but only EVM chains get a bare number.
    /// The rest are either absent (bitcoin, lightning, Spark) or CAIP-2 namespaced — <c>solana:5eykt4Us…</c>,
    /// <c>ton:mainnet</c>, <c>xrp:mainnet</c>, <c>zcash:mainnet</c> — and a namespace is the provider saying, in
    /// as many words, that this is not an EVM chain id. Digits only, so nothing with a prefix, a sign or a
    /// separator passes.
    /// </remarks>
    private static bool IsEvmChainId(string? chainId) =>
        !string.IsNullOrEmpty(chainId) && chainId.All(character => character is >= '0' and <= '9');

    /// <summary>
    /// Whether a contract address is one an EVM chain could have: absent, or twenty hex bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Absent means the chain's native asset — ETH on ethereum, POL on polygon — which is an ordinary
    /// destination with no contract to check.
    /// </para>
    /// <para>
    /// This is the check <see cref="IsEvmChainId"/> alone gets wrong, and it earns its keep on exactly two
    /// chains. <b>Tron</b> reports chain id <c>728126428</c> — a plain number, because its VM is EVM-compatible —
    /// while its addresses are base58 and begin with <c>T</c>, so a merchant cannot receive there with the 0x
    /// address this page asks for. <b>HyperCore</b> reports <c>1337</c> and identifies assets by short
    /// 0x-prefixed indices rather than by contract, which are not addresses either. Both fail on the length, and
    /// they fail for the right reason: the address the merchant would type could not reach them.
    /// </para>
    /// </remarks>
    private static bool IsEvmContractAddress(string? contract)
    {
        if (string.IsNullOrEmpty(contract))
            return true;

        return contract.Length == 42
            && contract.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && contract.Skip(2).All(Uri.IsHexDigit);
    }

    /// <summary>How the chain reads, preferring the provider's own display name over its routing key.</summary>
    private static string Label(IEnumerable<RoutePair> group)
    {
        var display = group
            .Select(pair => pair.ChainDisplayName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

        return string.IsNullOrWhiteSpace(display) ? group.First().Chain : display.Trim();
    }

    /// <summary>
    /// The distinct assets a chain carries, the plugin's default first and the rest alphabetically.
    /// </summary>
    /// <remarks>
    /// Distinct because a chain appears once per Spark source asset, so every destination is listed at least
    /// twice in the payload. The ordering is stated rather than left to the payload because it is not only
    /// cosmetic: the page's script falls back to the first option when the chain a merchant switches to does not
    /// carry the asset that was showing, and landing on a stablecoin wherever one exists is better than landing
    /// on whichever asset the provider happened to list first.
    /// </remarks>
    private static IReadOnlyList<CrossChainAsset> Assets(IEnumerable<RoutePair> group) =>
    [
        .. group
            .GroupBy(pair => pair.Asset, StringComparer.OrdinalIgnoreCase)
            .Select(assets => assets.First())
            .Select(pair => new CrossChainAsset(
                pair.Asset,
                string.IsNullOrWhiteSpace(pair.AssetDisplayName) ? pair.Asset : pair.AssetDisplayName.Trim(),
                pair.Decimals))
            .OrderByDescending(asset => Matches(asset.Symbol, SweepSettings.DefaultCrossChainAsset))
            .ThenBy(asset => asset.Symbol, StringComparer.OrdinalIgnoreCase)
    ];

    /// <param name="Live">Whether <paramref name="Chains"/> came from the provider or is the built-in floor.</param>
    /// <param name="NextAttempt">
    /// When a refresh may next be scheduled. Replacing the whole record on every attempt is what makes the gate
    /// work: a reader takes one consistent snapshot of all three fields, so the loser of a race schedules
    /// nothing rather than fetching a second time.
    /// </param>
    private sealed record State(
        IReadOnlyList<CrossChainDestination> Chains,
        bool Live,
        DateTimeOffset NextAttempt);

    /// <summary>
    /// A read-only view of a response body that stops at <see cref="MaxResponseBytes"/>.
    /// </summary>
    /// <remarks>
    /// The bound has to be applied while reading rather than by checking <c>Content-Length</c>, because a
    /// chunked response does not have one — and a response with no declared length is exactly the case worth
    /// defending against.
    /// </remarks>
    private sealed class CappedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _read;

        public CappedStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Count(count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Count(_inner.Read(buffer, offset, count));

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;

            if (_read > _limit)
            {
                throw new InvalidOperationException(
                    $"the route table exceeded the {_limit:N0} byte ceiling this plugin will read");
            }

            return read;
        }
    }

    /// <summary>The response body, cut down to the fields this projection reads.</summary>
    /// <remarks>
    /// Everything the provider sends and this does not name is ignored, which is the point: a field added
    /// upstream must not be able to turn the live table into the fallback.
    /// </remarks>
    private sealed record RouteTable
    {
        [JsonPropertyName("routes")]
        public IReadOnlyList<Route?>? Routes { get; init; }
    }

    private sealed record Route
    {
        [JsonPropertyName("sourceChain")]
        public string? SourceChain { get; init; }

        [JsonPropertyName("destination")]
        public RoutePair? Destination { get; init; }
    }

    private sealed record RoutePair
    {
        [JsonPropertyName("chain")]
        public string Chain { get; init; } = string.Empty;

        [JsonPropertyName("asset")]
        public string Asset { get; init; } = string.Empty;

        [JsonPropertyName("assetDisplayName")]
        public string? AssetDisplayName { get; init; }

        [JsonPropertyName("chainDisplayName")]
        public string? ChainDisplayName { get; init; }

        [JsonPropertyName("contractAddress")]
        public string? ContractAddress { get; init; }

        [JsonPropertyName("decimals")]
        public int Decimals { get; init; }

        /// <remarks>
        /// A string rather than a number, and that is the provider's own choice rather than a convenience here:
        /// the non-EVM chains carry CAIP-2 identifiers such as <c>ton:mainnet</c> in this field. Parsing it as a
        /// number would fail the whole payload rather than the one route.
        /// </remarks>
        [JsonPropertyName("chainId")]
        public string? ChainId { get; init; }
    }
}
