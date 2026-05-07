using System.Security.Cryptography;

namespace VamAcarsClient.Core;

/// <summary>
/// Persists the ACARS pairing-token encrypted-at-rest using Windows DPAPI.
///
/// DPAPI = Data Protection API. The OS encrypts the bytes with a key
/// derived from the current user's logon credentials. Decryption only
/// works for the same Windows user on the same machine — copying the
/// token.bin to another PC or another user-account yields gibberish.
/// That's the whole security model: low-tech, no key-management on our
/// side, no cleartext token sitting on disk.
///
/// File location:
///   %LOCALAPPDATA%\&lt;LocalAppDataFolderName&gt;\&lt;TokenFileName&gt;
/// Roaming-disabled (LocalApplicationData, not ApplicationData) because
/// the encrypted blob is machine-bound anyway — syncing it via OneDrive
/// or roaming-profile would just produce a useless copy.
///
/// Threading: file-IO is synchronous. The token is written once at pair-
/// time and read once at startup, so async would only add complexity.
/// If we ever rotate tokens mid-session that decision can be revisited.
///
/// HISTORICAL NOTE: this used to be a static class with hardcoded paths
/// from VamConfig. Refactored to instance-based when M3.5 introduced
/// runtime-loaded configuration — VamConfig is now data, not constants,
/// so static methods couldn't reach it without going through a global,
/// which is hostile to testing.
/// </summary>
public sealed class TokenStore
{
    private readonly VamConfig _config;

    public TokenStore(VamConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Resolve the absolute path to the token blob. Creates the parent
    /// directory if missing — first-time pair on a fresh machine needs
    /// to write before the folder exists.
    /// </summary>
    private string GetTokenPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(localAppData, _config.Storage.LocalAppDataFolderName);
        Directory.CreateDirectory(folder); // no-op if already exists
        return Path.Combine(folder, _config.Storage.TokenFileName);
    }

    /// <summary>
    /// Encrypt and persist the bearer-token. Overwrites any existing
    /// token-file — used both for first-pair and re-pair (token rotation).
    /// </summary>
    public void Save(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("token must not be empty", nameof(token));

        var plaintext = System.Text.Encoding.UTF8.GetBytes(token);

        // CurrentUser scope: only this Windows user can decrypt. The
        // optional `entropy` parameter is null — adding a hardcoded
        // entropy buys nothing if it ships in our binary. Real entropy
        // would have to come from somewhere only the user knows, which
        // we don't have.
        var ciphertext = ProtectedData.Protect(
            plaintext,
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);

        File.WriteAllBytes(GetTokenPath(), ciphertext);
    }

    /// <summary>
    /// Try to load and decrypt the persisted token. Returns null if no
    /// token has been stored yet (file missing) or if decryption fails
    /// (corrupted file, copied from another machine/user). Caller treats
    /// null as "not paired, prompt for pairing-code".
    /// </summary>
    public string? TryLoad()
    {
        var path = GetTokenPath();
        if (!File.Exists(path)) return null;

        try
        {
            var ciphertext = File.ReadAllBytes(path);
            var plaintext = ProtectedData.Unprotect(
                ciphertext,
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            // File exists but can't be decrypted. Most common cause:
            // moved between user-accounts or machines. Treat as "no
            // token" so the caller will re-pair, which is the right
            // recovery path.
            return null;
        }
        catch (IOException)
        {
            // Disk-level error (file locked, permissions). Same fall-
            // back as decryption failure — try again next start.
            return null;
        }
    }

    /// <summary>
    /// Delete the persisted token. Called on user-initiated unpair
    /// (Settings → "Disconnect ACARS"). Idempotent — safe to call when
    /// no token exists.
    /// </summary>
    public void Clear()
    {
        var path = GetTokenPath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
