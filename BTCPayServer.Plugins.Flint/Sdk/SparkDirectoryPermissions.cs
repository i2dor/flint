using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// Owner-only permissions for the directories this plugin creates under BTCPay's data directory.
/// </summary>
/// <remarks>
/// <para>
/// One implementation, deliberately shared. Both of the plugin's own directories under
/// <c>&lt;DataDir&gt;/Plugins/Spark</c> hold things that no other local user has any business reading — the
/// per-store SDK state in <c>&lt;storeId&gt;/…/storage.sql</c>, and the SDK's own <c>sdk.log</c> in
/// <c>logs</c> — and both are created by <c>Directory.CreateDirectory</c>, which applies the process umask
/// and so yields <c>0755</c> on a normal host. An external audit found the log directory hardened and the
/// storage directory not; the asymmetry existed because the hardening lived privately beside one of the two
/// callers. It lives here now so there is one place to change and nowhere to forget.
/// </para>
/// <para>
/// Only the plugin's own leaf directories are restricted, never <c>&lt;DataDir&gt;/Plugins</c> itself: that
/// one belongs to BTCPay and is shared with every other plugin.
/// </para>
/// </remarks>
internal static class SparkDirectoryPermissions
{
    private const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Creates <paramref name="directory"/> owner-only from the first instant it exists.
    /// </summary>
    /// <remarks>
    /// The mode is passed to the creation itself rather than applied afterwards, because "afterwards" is a
    /// window: a directory created at the process umask and then restricted spends its first moments at 0755,
    /// and what lands in it during those moments was readable. A umask can only clear bits from 0700, never
    /// add them, so the result is owner-only regardless of the host's umask. Creating a directory that already
    /// exists changes nothing — pair with <see cref="RestrictToOwner"/> where a pre-existing directory from an
    /// older plugin version needs hardening too.
    /// </remarks>
    internal static void CreateOwnerOnly(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            // Same posture as RestrictToOwner: directory ACLs on Windows are a different mechanism with
            // different defaults, and the plugin makes no claim about them.
            Directory.CreateDirectory(directory);
            return;
        }

        Directory.CreateDirectory(directory, OwnerOnly);
    }

    /// <summary>
    /// Makes <paramref name="directory"/> owner-only, so far as the platform allows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Best effort, and never throws. The plugin cannot choose the mode of the files the SDK creates inside
    /// (<c>sdk.log</c> was observed at <c>0644</c>), but it owns the directory, and a directory without
    /// other-execute cannot be traversed to reach the files inside it. A host where the mode cannot be set —
    /// a filesystem that does not carry Unix modes, a container running as a user that does not own the path —
    /// is a weaker host, not a reason to refuse to log or to refuse to run a wallet. The failure is reported at
    /// warning level so it is discoverable.
    /// </para>
    /// <para>
    /// Applied on every call rather than only at creation, which is what retroactively hardens a directory an
    /// earlier version of the plugin already created world-readable.
    /// </para>
    /// <para>
    /// A no-op on Windows, where <see cref="File.SetUnixFileMode(string, UnixFileMode)"/> is unsupported and
    /// throws. Directory ACLs there are a different mechanism with different defaults and the plugin makes no
    /// claim about them.
    /// </para>
    /// </remarks>
    internal static void RestrictToOwner(string directory, ILogger logger)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(directory, OwnerOnly);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not restrict the Spark directory {Directory} to its owner, so other users on this host "
                + "may be able to read what the SDK writes there", directory);
        }
    }
}
