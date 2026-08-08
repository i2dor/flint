using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Why a seed source could not produce a seed. <see cref="None"/> means it did.
/// </summary>
/// <remarks>
/// A code rather than only a sentence, because the two surfaces that consume this answer report a refusal
/// differently and must not each re-derive which refusal they are looking at. The setup page turns
/// <see cref="HotWalletNotAllowed"/> into an error banner and the API turns it into <c>403</c>; both attach
/// <see cref="InvalidMnemonic"/> to the phrase field and everything else to the seed-source field.
/// </remarks>
public enum SparkSeedRejection
{
    /// <summary>Not a rejection — a seed was produced.</summary>
    None,

    /// <summary>
    /// The server does not let this caller create hot wallets. Applies to every seed source, including reusing
    /// the store's existing on-chain seed.
    /// </summary>
    HotWalletNotAllowed,

    /// <summary>The requested seed source is not one this plugin offers.</summary>
    UnknownSeedSource,

    /// <summary>
    /// The store's on-chain hot-wallet seed cannot be reused — watch-only, no stored phrase, or core's
    /// hot-wallet service is not reachable.
    /// </summary>
    HotWalletUnavailable,

    /// <summary>The supplied recovery phrase is not a usable BIP39 mnemonic.</summary>
    InvalidMnemonic
}

/// <summary>
/// A seed for provisioning, or the reason there is not one.
/// </summary>
/// <param name="Mnemonic">
/// Set exactly when <paramref name="Rejection"/> is <see cref="SparkSeedRejection.None"/>. <b>Secret.</b> It goes
/// straight to <see cref="SparkStoreProvisioner.ProvisionAsync"/> and nowhere else — not into a log, not into a
/// view model, not into a response body except the one documented single disclosure of a freshly generated phrase.
/// </param>
/// <param name="Error">Set exactly when this is a rejection, and written for a merchant to read.</param>
public sealed record SparkSeedResolution(string? Mnemonic, SparkSeedRejection Rejection, string? Error)
{
    public static SparkSeedResolution Resolved(string mnemonic) =>
        new(mnemonic, SparkSeedRejection.None, null);

    public static SparkSeedResolution Rejected(SparkSeedRejection rejection, string error) =>
        new(null, rejection, error);

    /// <summary>True when <see cref="Mnemonic"/> is usable.</summary>
    public bool Succeeded => Rejection is SparkSeedRejection.None && Mnemonic is not null;
}

/// <summary>
/// Turns "provision this store from this kind of seed" into an actual seed, or into a refusal.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists so the setup page and the Greenfield API cannot disagree about what a seed source means.</b>
/// The decision it holds — the hot-wallet policy gate that covers <em>all three</em> sources, generating,
/// normalising an imported phrase, and reading the store's on-chain seed — used to live inline in
/// <c>SparkController.Setup</c>. A second caller would have had to re-implement it, and the parts most worth
/// getting right are the ones a re-implementation would quietly drop: that the policy gate applies to seed reuse
/// too, and that every rejection message is written here rather than relayed from NBitcoin or the SDK.
/// </para>
/// <para>
/// <b>Scoped, not a singleton</b>, and that is a constraint rather than a preference:
/// <see cref="IHotWalletSeedReader"/> resolves core's <c>HotwalletSafe</c> per request and the read is authorised
/// against the signed-in principal, so this type shares that lifetime. It is therefore never constructed during
/// BTCPay's own startup graph and cannot participate in the undetectable startup cycle that a type BTCPay
/// constructs from inside its own container can — see <c>SparkConnectionStringHandler</c>'s remarks.
/// </para>
/// <para>
/// The <see cref="ClaimsPrincipal"/> is passed in rather than read from an accessor so it is always the real
/// caller — a cookie principal on the setup page, an API-key principal on the API — and never a service identity.
/// Core's <c>CanUseHotWallet</c> helper judges both the same way BTCPay judges them.
/// </para>
/// </remarks>
public sealed class SparkSeedResolver
{
    private readonly IHotWalletSeedReader _hotWalletSeedReader;
    private readonly IAuthorizationService _authorizationService;
    private readonly ISettingsAccessor<PoliciesSettings> _policiesSettings;

    /// <param name="policiesSettings">
    /// The <em>accessor</em>, not the settings object. Core registers <c>PoliciesSettings</c> as a transient that
    /// reads through the accessor, and the accessor is populated by a startup task that needs the database — so a
    /// type taking the settings directly cannot be constructed before that task has run, and would capture a
    /// snapshot if it were. Reading <c>.Settings</c> per call also means an administrator turning
    /// <c>AllowHotWalletForAll</c> off takes effect on the next request rather than on the next restart.
    /// </param>
    public SparkSeedResolver(
        IHotWalletSeedReader hotWalletSeedReader,
        IAuthorizationService authorizationService,
        ISettingsAccessor<PoliciesSettings> policiesSettings)
    {
        _hotWalletSeedReader = hotWalletSeedReader;
        _authorizationService = authorizationService;
        _policiesSettings = policiesSettings;
    }

    /// <summary>
    /// The message shown when the server does not allow this caller to create hot wallets. Matches how BTCPay
    /// itself refuses on-chain hot wallets to non-admins when the policy is off.
    /// </summary>
    public const string HotWalletNotAllowedMessage =
        "A server administrator has not allowed non-admins to create hot wallets on this server.";

    /// <summary>
    /// <c>AllowHotWalletForAll || the caller is a server admin</c>, via core's own helper.
    /// </summary>
    /// <remarks>
    /// Every seed source is behind this gate, <em>including</em> reusing the store's existing hot-wallet seed:
    /// that copies key material the server already holds into a second wallet, which is exactly the capability
    /// the policy exists to control. Getting this wrong on one surface and right on the other would make the
    /// policy advisory.
    /// </remarks>
    public async Task<bool> CanUseHotWalletAsync(ClaimsPrincipal user) =>
        (await _authorizationService.CanUseHotWallet(_policiesSettings.Settings, user).ConfigureAwait(false))
        .CanCreateHotWallet;

    /// <summary>
    /// Whether the store's on-chain seed could be reused, for a page or a status response that wants to say so
    /// without provisioning anything.
    /// </summary>
    /// <remarks>
    /// Deliberately returns the <see cref="HotWalletSeedResult"/> whole — including its
    /// <see cref="HotWalletSeedResult.Mnemonic"/>, which callers on this path must ignore. Only
    /// <see cref="ResolveAsync"/> may use the phrase, and only to hand it to the provisioner.
    /// </remarks>
    public Task<HotWalletSeedResult> ReadHotWalletSeedAsync(
        ClaimsPrincipal user,
        string storeId,
        CancellationToken cancellationToken = default) =>
        _hotWalletSeedReader.ReadAsync(user, storeId, cancellationToken);

    /// <summary>
    /// Produces the seed to provision <paramref name="storeId"/> with, or the reason it cannot.
    /// </summary>
    /// <remarks>
    /// The policy gate runs first and unconditionally, before any seed is generated, read or parsed. That
    /// ordering is what makes the refusal cheap and total: a caller the server will not let create a hot wallet
    /// never gets as far as a phrase existing in memory.
    /// </remarks>
    public async Task<SparkSeedResolution> ResolveAsync(
        ClaimsPrincipal user,
        string storeId,
        SeedSource seedSource,
        string? importedMnemonic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        if (!await CanUseHotWalletAsync(user).ConfigureAwait(false))
        {
            return SparkSeedResolution.Rejected(
                SparkSeedRejection.HotWalletNotAllowed, HotWalletNotAllowedMessage);
        }

        switch (seedSource)
        {
            case SeedSource.Generated:
                return SparkSeedResolution.Resolved(SparkStoreProvisioner.GenerateMnemonic());

            case SeedSource.HotWallet:
                var seed = await _hotWalletSeedReader
                    .ReadAsync(user, storeId, cancellationToken)
                    .ConfigureAwait(false);
                return seed.IsAvailable
                    ? SparkSeedResolution.Resolved(seed.Mnemonic!)
                    : SparkSeedResolution.Rejected(
                        SparkSeedRejection.HotWalletUnavailable,
                        seed.Reason ?? "This store's on-chain wallet seed cannot be reused.");

            case SeedSource.Imported:
                return SparkStoreProvisioner.TryNormalizeMnemonic(importedMnemonic, out var normalized, out var error)
                    ? SparkSeedResolution.Resolved(normalized)
                    : SparkSeedResolution.Rejected(SparkSeedRejection.InvalidMnemonic, error);

            default:
                return SparkSeedResolution.Rejected(
                    SparkSeedRejection.UnknownSeedSource,
                    "Choose where this store's Spark seed comes from.");
        }
    }
}
