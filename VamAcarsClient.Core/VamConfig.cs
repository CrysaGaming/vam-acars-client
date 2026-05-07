namespace VamAcarsClient.Core;

/// <summary>
/// Static configuration for the ACARS-client. Constants are baked into
/// the build for now — no JSON config file in v1. Once we have multiple
/// environments (production vam.kevindrack.de vs a future staging URL)
/// we'll move this to a runtime-loaded JSON config.
/// </summary>
public static class VamConfig
{
    /// <summary>Production server. Cloudflared tunnel terminates here.</summary>
    public const string ApiBaseUrl = "https://vam.kevindrack.de";

    /// <summary>User-Agent header sent on every API request. Lets the
    /// server log identify our client and version separately from
    /// browsers and the bot.</summary>
    public const string UserAgent = "VamAcarsClient/0.1 (.NET 10; Windows)";

    /// <summary>Path under %LOCALAPPDATA% where we store the encrypted
    /// pairing-token. Per-user encryption via DPAPI means this folder
    /// only contains opaque bytes — safe even if the file is copied
    /// somewhere else (the data won't decrypt off this machine for
    /// this user).</summary>
    public const string LocalAppDataFolderName = "VamAcarsClient";

    /// <summary>Filename of the encrypted token blob inside the local
    /// app-data folder.</summary>
    public const string TokenFileName = "token.bin";
}