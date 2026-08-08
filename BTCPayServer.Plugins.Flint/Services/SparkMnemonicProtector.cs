using System;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Encrypts and decrypts the store's BIP39 mnemonic at rest.
/// </summary>
/// <remarks>
/// <para>
/// The mnemonic controls all of the store's Spark funds and — when the merchant chooses to reuse the
/// BTCPay hot-wallet seed — its on-chain funds too. Store settings live in BTCPay's database in plain
/// JSON, so the mnemonic is wrapped with <c>IDataProtector</c> before it ever reaches
/// <see cref="SparkSettings"/>. It must never be echoed into a view model or an HTML form; the setup UI
/// writes it once and reads it back never.
/// </para>
/// <para>
/// Consequence worth knowing before shipping: data-protection keys live in the BTCPay data directory.
/// Losing them makes the stored mnemonic unrecoverable, which is why the setup flow shows the merchant
/// their seed once and asks them to back it up themselves.
/// </para>
/// </remarks>
public class SparkMnemonicProtector
{
    private readonly IDataProtector _protector;

    public SparkMnemonicProtector(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(Constants.DataProtectionPurpose);
    }

    public string Protect(string mnemonic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mnemonic);
        return _protector.Protect(mnemonic);
    }

    /// <summary>
    /// Recovers a mnemonic. Returns null when the payload is absent or cannot be decrypted, which
    /// happens when the data-protection keyring has been lost or replaced.
    /// </summary>
    public string? TryUnprotect(string? protectedMnemonic)
    {
        if (string.IsNullOrEmpty(protectedMnemonic))
            return null;
        try
        {
            return _protector.Unprotect(protectedMnemonic);
        }
        catch (Exception)
        {
            // Swallowed deliberately and reported by the caller with store context: the exception text
            // from data protection is not actionable for a merchant, and the caller can say something
            // useful ("re-enter your seed for store X").
            return null;
        }
    }
}
