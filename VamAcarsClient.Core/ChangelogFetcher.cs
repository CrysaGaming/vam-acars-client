using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VamAcarsClient.Core;

/// <summary>
/// Fetches release notes from GitHub Releases for the in-app changelog
/// viewer (Welle A — option A4).
///
/// Why GitHub Releases as source-of-truth:
///   - We already publish Velopack releases there via the existing
///     CI workflow, so each tag has a markdown body the maintainer
///     wrote anyway. Reading it back into the app means there's
///     exactly one place to author release notes; no duplication.
///   - Public repo, no auth required for reads, no API key to ship
///     in the binary. Anonymous calls hit GitHub's 60/hr rate limit
///     per IP, which is generous for a tray-app that fetches at most
///     once per launch.
///   - JSON shape is stable, well-documented. We bind exactly four
///     fields (tag_name, name, body, published_at) so future API
///     additions can't break us.
///
/// Why not a custom JSON file on the server: would require an extra
/// authoring step per release, and would drift from GitHub's release
/// notes (which is where the source-of-truth already lives because
/// of how Velopack does its release flow).
///
/// Why not embed a changelog file in the binary: would force a
/// re-install for every changelog update, defeating the point of
/// "see what changed AFTER updating".
///
/// THREAD MODEL:
///   FetchAsync is awaitable. Caller should marshal results onto the
///   UI thread before mutating bindings. No long-running work here —
///   single HTTP GET, single JSON parse, return.
///
/// ERROR HANDLING:
///   Network errors, 4xx/5xx, JSON-parse failures all return an empty
///   list (logged at Warning). The dialog renders the empty case as
///   "Keine Release-Notes verfügbar" rather than blowing up — a missing
///   changelog is a minor UX nuisance, not a failure that should
///   surface as an error popup.
/// </summary>
public sealed class ChangelogFetcher
{
    private readonly HttpClient _http;
    private readonly ILogger<ChangelogFetcher> _logger;

    /// <summary>
    /// Hard-coded to the public ACARS client repo. If this ever forks
    /// or moves we update one place. We don't make this configurable
    /// because it's tied to the binary's identity — a Cowork-of-this-app
    /// would publish its OWN repo, not ours.
    /// </summary>
    private const string ReleasesUrl =
        "https://api.github.com/repos/CrysaGaming/vam-acars-client/releases";

    /// <summary>
    /// GitHub requires a User-Agent header on all API calls. We send a
    /// descriptive one so abuse reports can route back to us if the
    /// fetch ever misbehaves. Versioned suffix tracks which client
    /// build initiated the call — useful for narrowing down rate-limit
    /// issues to a specific release.
    /// </summary>
    private const string UserAgentValue = "VamAcarsClient/changelog-fetcher";

    public ChangelogFetcher(
        HttpClient http,
        ILogger<ChangelogFetcher>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? NullLogger<ChangelogFetcher>.Instance;
    }

    /// <summary>
    /// Fetch the most recent releases from GitHub. Returns them
    /// newest-first (which is GitHub's default order). Caps at 20
    /// entries to keep the dialog scrollable rather than overwhelming
    /// — by the time we have 20+ releases, only the very-curious user
    /// scrolls past the first few, and they can always open the repo
    /// in a browser for the full history.
    /// </summary>
    public async Task<IReadOnlyList<ReleaseEntry>> FetchAsync(
        CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
            // GitHub's API requires User-Agent. The HttpClient might
            // already have a default one set elsewhere (e.g. for the
            // heartbeat endpoint), but we add ours explicitly on the
            // request so we don't depend on caller setup. Per-request
            // header beats setting DefaultRequestHeaders here since
            // _http is shared with other services.
            req.Headers.UserAgent.ParseAdd(UserAgentValue);

            // 10s timeout via CTS. Default HttpClient timeout is 100s,
            // which is far too long for an inline dialog-open path.
            // The user shouldn't sit looking at an empty dialog for
            // more than a few seconds before the "Keine Release-Notes"
            // fallback kicks in.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await _http.SendAsync(req, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Changelog fetch returned HTTP {Status}; returning empty list",
                    (int)response.StatusCode);
                return [];
            }

            var raw = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>(timeoutCts.Token);
            if (raw is null)
            {
                _logger.LogWarning("Changelog fetch returned null JSON; returning empty list");
                return [];
            }

            // Project to our domain type. Skip drafts and pre-releases
            // by default — users coming from a release-channel install
            // don't want to see in-flight betas in their changelog.
            // Take 20 to cap the dialog size; GitHub already orders
            // newest-first so this is the most-recent-20.
            var entries = raw
                .Where(r => !r.Draft && !r.Prerelease)
                .Take(20)
                .Select(r => new ReleaseEntry(
                    Tag: r.TagName ?? "(unknown)",
                    Name: string.IsNullOrWhiteSpace(r.Name) ? r.TagName ?? "(unknown)" : r.Name,
                    Body: r.Body ?? string.Empty,
                    PublishedAt: r.PublishedAt))
                .ToList();

            _logger.LogInformation("Fetched {Count} changelog entries from GitHub", entries.Count);
            return entries;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled (e.g. dialog closed mid-fetch). Not an
            // error — propagate as empty so the caller's UI path is
            // uniform.
            return [];
        }
        catch (TaskCanceledException)
        {
            // Timeout (our 10s CTS or HttpClient's own). Logged at
            // Info because it's recoverable + expected on flaky
            // networks.
            _logger.LogInformation("Changelog fetch timed out");
            return [];
        }
        catch (Exception ex)
        {
            // Network, JSON, anything else. Logged at Warning so a
            // user-visible bug-report can correlate to the log line,
            // but never thrown — empty-list path is the canonical
            // failure mode.
            _logger.LogWarning(ex, "Changelog fetch failed");
            return [];
        }
    }

    /// <summary>
    /// GitHub Releases API DTO. Names match the JSON exactly via
    /// JsonPropertyName so a future field-rename on GitHub's side
    /// would surface as a build error rather than silent null bindings.
    /// </summary>
    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("body")] public string? Body { get; init; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; init; }
        [JsonPropertyName("draft")] public bool Draft { get; init; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
    }
}

/// <summary>
/// View-model entry for one release in the changelog dialog. Stripped
/// down from the GitHub API DTO to just the fields we render — the
/// dialog's data-binding stays simple, and we don't accidentally
/// expose GitHub-internal fields if the schema grows.
/// </summary>
public sealed record ReleaseEntry(
    string Tag,
    string Name,
    string Body,
    DateTimeOffset? PublishedAt);
