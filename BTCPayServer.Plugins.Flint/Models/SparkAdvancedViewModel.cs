using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace BTCPayServer.Plugins.Flint.Models;

/// <summary>
/// The Advanced page: wallet details, recovery-phrase provenance, the sweep tuning most stores never touch,
/// and removal.
/// </summary>
/// <remarks>
/// Everything here used to live on the status page — a merchant opening the plugin to check their balance had
/// to read past all of it. Holds no seed material, by construction, same as
/// <see cref="SparkStatusViewModel"/>.
/// </remarks>
public class SparkAdvancedViewModel
{
    [BindNever]
    public string StoreId { get; set; } = string.Empty;

    /// <summary>Where the seed came from, for the security-posture note.</summary>
    public SeedSource SeedSource { get; set; }

    public bool WalletRunning { get; set; }

    /// <summary>The wallet's Spark identity public key. Null when not running or not synced yet.</summary>
    public string? IdentityPubkey { get; set; }

    /// <summary>SDK storage path. <b>Null for anyone but a server admin</b> — a fact about the host, not the store.</summary>
    public string? StorageDirectory { get; set; }

    /// <summary>
    /// The sweep configuration, of which only the reserve and the fee policy are editable here. The rest is
    /// carried so the save can go through the same whole-object validation the sweep page uses.
    /// </summary>
    [ValidateNever]
    public SweepSettingsInput Settings { get; set; } = new();

    /// <summary>
    /// The merchant's own Breez API key, empty when the plugin's built-in one is in use. Not a secret in
    /// Breez's model — displayed back rather than masked — but it is what the SDK connects with, so saving it
    /// restarts the store's wallet.
    /// </summary>
    [Display(Name = "Breez API key")]
    public string? ApiKeyOverride { get; set; }
}
