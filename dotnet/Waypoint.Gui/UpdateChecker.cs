// Notify-only update check against GitHub Releases. Never downloads or runs
// anything: until releases carry a real signing cert there is no signature
// worth pinning, so installing stays a deliberate step by the technician.

using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Waypoint.Gui;

internal enum UpdateState { UpToDate, Available, DevBuild, NoReleases }

internal sealed record ReleaseInfo(Version Version, string Tag)
{
    // Built from the tag, not the API's html_url: only ever opens our repo.
    public Uri Page => new($"{UpdateChecker.ReleasesPage}/tag/{Uri.EscapeDataString(Tag)}");
}

internal sealed record UpdateResult(UpdateState State, Version Current, ReleaseInfo? Latest);

internal static class UpdateChecker
{
    private const string Repo = "TrashPanda2481/Waypoint-Driver-Manager";
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repo}/releases");
    private static readonly Uri ReleasesApi = new($"https://api.github.com/repos/{Repo}/releases?per_page=30");

    // MSI versions are numeric major.minor.patch; compare on that.
    // Declared before Client: static initializers run in textual order.
    public static Version Current { get; } = Normalize(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Client = CreateClient();

    public static async Task<UpdateResult> CheckAsync(CancellationToken ct = default)
    {
        var json = await Client.GetStringAsync(ReleasesApi, ct);
        return Evaluate(Current, PickLatest(json));
    }

    public static UpdateResult Evaluate(Version current, ReleaseInfo? latest)
    {
        if (latest is null) return new(UpdateState.NoReleases, current, null);
        if (current == new Version(0, 0, 0)) return new(UpdateState.DevBuild, current, latest);
        return new(latest.Version > current ? UpdateState.Available : UpdateState.UpToDate, current, latest);
    }

    // Prereleases count: every release so far is one, so /releases/latest finds nothing.
    public static ReleaseInfo? PickLatest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        ReleaseInfo? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            if (!r.TryGetProperty("tag_name", out var tagEl) || tagEl.GetString() is not { } tag) continue;
            if (ParseTag(tag) is not { } v) continue;
            if (best is null || v > best.Version) best = new ReleaseInfo(v, tag);
        }
        return best;
    }

    // "v0.2.1-alpha.1" -> 0.2.1. Suffix dropped: the MSI can't carry it either.
    public static Version? ParseTag(string tag)
    {
        var core = tag.TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core, out var v) && v.Build >= 0 ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"waypoint-desktop/{Current}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
