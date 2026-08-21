using System;
using System.IO;
using System.Threading;
using Breez.Sdk.Spark;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// Routes the SDK's Rust log lines into an <see cref="ILogger"/>, scrubbed.
/// </summary>
/// <remarks>
/// <para>
/// <c>Logger.Log</c> is a synchronous <c>void</c> called from SDK-owned threads, through the same
/// UniFFI callback machinery as the event listener. It is therefore held to the same rule: it must
/// never throw.
/// </para>
/// <para>
/// Every line goes through <see cref="SparkLogScrubber"/> first. What the SDK emits at each level was
/// measured rather than assumed — see that class — and at <c>debug</c>, the level Breez's production
/// checklist asks for, nothing secret appeared. The scrub is there for the lines the measurement could
/// not reach: an unfunded probe wallet never produces the ones a completed payment would.
/// </para>
/// </remarks>
public sealed class SparkLogBridge : Logger
{
    private readonly ILogger _logger;

    public SparkLogBridge(ILogger logger)
    {
        _logger = logger;
    }

    public void Log(LogEntry l)
    {
        try
        {
            var level = l.level?.ToLowerInvariant() switch
            {
                "error" => LogLevel.Error,
                "warn" => LogLevel.Warning,
                "info" => LogLevel.Information,
                "debug" => LogLevel.Debug,
                _ => LogLevel.Trace
            };
            // Downgraded by one step relative to the Rust level: the SDK is chatty at "info" and its
            // notion of severity is about the SDK, not about the merchant's BTCPay server.
            _logger.Log(
                level == LogLevel.Error ? LogLevel.Warning : level,
                "spark: {Line}",
                SparkLogScrubber.Scrub(l.line));
        }
        catch
        {
            // Nothing to report to, and throwing back into UniFFI is how the process deadlocks.
        }
    }
}

/// <summary>
/// One-shot initialisation of the SDK's process-global logging.
/// </summary>
/// <remarks>
/// <c>InitLogging</c> installs a global Rust <c>tracing</c> subscriber, takes ~450 ms on first call
/// (this is what actually loads the native library) and will very likely throw if called twice. It is
/// therefore per-process, not per-store.
/// </remarks>
public static class SparkLogging
{
    /// <summary>
    /// The most verbose filter this plugin will install, when a caller asks for something more verbose.
    /// </summary>
    internal const string MaxFilter = "debug";

    /// <summary>
    /// The filter installed when a caller supplies none at all.
    /// </summary>
    /// <remarks>
    /// <b>Not <see cref="MaxFilter"/>, and the difference is the whole point.</b> A missing filter is an
    /// absence of intent, and the clamp's business is verbosity — the level at which the service provider's
    /// session token was found on disk. Falling back to the most verbose level the plugin will accept resolves
    /// an absence of intent in the direction that leaked, so the fallback is the quiet one instead. Pinned to
    /// <see cref="Constants.SdkLogFilter"/> by <c>SparkLogBridgeTests</c>, so the level the plugin actually
    /// ships and the level it falls back to cannot drift apart.
    /// </remarks>
    internal const string DefaultFilter = "info";

    private static int _initialised;

    /// <summary>
    /// Initialises SDK logging once per process. Returns true if this call did the work.
    /// </summary>
    /// <param name="logFilter">Rust <c>env_logger</c> filter syntax, e.g. <c>info</c> or <c>debug</c>.</param>
    public static bool TryInitialise(string logDirectory, ILogger logger, string logFilter = "info")
    {
        ArgumentException.ThrowIfNullOrEmpty(logDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        if (Interlocked.CompareExchange(ref _initialised, 1, 0) != 0)
            return false;

        return Initialise(
            logDirectory,
            logger,
            logFilter,
            (directory, bridge, filter) => BreezSdkSparkMethods.InitLogging(directory, bridge, filter));
    }

    /// <summary>
    /// Everything <see cref="TryInitialise"/> does except the once-per-process guard.
    /// </summary>
    /// <param name="install">
    /// Installs the Rust subscriber. A seam only because the real one is process-global and one-shot: it
    /// cannot be called twice in a test run, so nothing above it could otherwise be exercised at all — and
    /// what is above it is the filter clamp and the directory mode, both of which are the guard.
    /// </param>
    internal static bool Initialise(
        string logDirectory,
        ILogger logger,
        string logFilter,
        Action<string, Logger, string> install)
    {
        logFilter = ClampFilter(logFilter, logger);

        try
        {
            SparkDirectoryPermissions.CreateOwnerOnly(logDirectory);
            // The SDK creates sdk.log itself, at the process umask — 0644 as observed, world-readable. The
            // plugin cannot choose the file's mode, but it owns the directory, and a directory without
            // other-execute cannot be traversed to reach the file inside it. Shared with the per-store storage
            // directory, which needs exactly the same treatment for exactly the same reason.
            SparkDirectoryPermissions.RestrictToOwner(logDirectory, logger);
            install(logDirectory, new SparkLogBridge(logger), logFilter);
            return true;
        }
        catch (Exception ex)
        {
            // Logging is a diagnostic aid, not a precondition for taking payments: a failure here must
            // not stop the plugin from starting. Reported at warning level with the reason so a
            // misconfigured log directory is discoverable.
            logger.LogWarning(ex, "Could not initialise Spark SDK logging into {LogDirectory}", logDirectory);
            return false;
        }
    }

    /// <summary>
    /// Refuses a filter that turns the Rust subscriber up to <c>trace</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a stylistic cap. At <c>trace</c> the SDK logs the service provider's GraphQL
    /// <c>session_token</c> in full, inside raw HTTP response bodies — a live bearer credential for the
    /// merchant's wallet, observed twice per authentication in a connect-only probe. Those lines go into
    /// <c>sdk.log</c> written by the Rust side, which no scrubbing on the C# bridge can reach, so the only
    /// place to stop them is here, before the subscriber is installed.
    /// </para>
    /// <para>
    /// A substring test rather than a parse of <c>env_logger</c> syntax, deliberately. The syntax admits
    /// per-module directives (<c>info,breez_sdk_spark=trace</c>), and a partial parser that missed one would
    /// fail open. Anything mentioning trace is refused wholesale, which can only ever be too strict.
    /// </para>
    /// <para>
    /// A null or blank filter is answered with <see cref="DefaultFilter"/>, not <see cref="MaxFilter"/>: see
    /// that constant for why a guard against verbosity must not resolve silence into the loudest level it
    /// tolerates.
    /// </para>
    /// </remarks>
    internal static string ClampFilter(string? logFilter, ILogger logger)
    {
        // Nothing asked for: fall back quietly rather than to the ceiling. Production always passes
        // Constants.SdkLogFilter, so this is the path a future caller reaches by forgetting the argument —
        // and forgetting an argument should not be how the SDK gets turned up.
        if (string.IsNullOrWhiteSpace(logFilter))
            return DefaultFilter;

        if (logFilter.Contains("trace", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "The Spark SDK log filter {Requested} would enable trace-level logging, which writes the "
                + "service provider's session token to disk in clear text. Using {Applied} instead",
                logFilter, MaxFilter);
            return MaxFilter;
        }

        return logFilter;
    }
}
