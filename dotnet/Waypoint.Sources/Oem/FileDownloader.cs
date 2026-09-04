// Streaming HTTP download. Injected into the OEM sources as a delegate so
// tests never need network access.
// Ported from src/waypoint/sources/oem/http.py.

namespace Waypoint.Sources.Oem;

// url, destPath -> completes when the file is on disk.
public delegate Task DownloadFileAsync(string url, string destPath, CancellationToken cancellationToken);

public static class HttpDownloader
{
    private const string UserAgent =
        "Waypoint-Driver-Manager/0.1 (+https://github.com/TrashPanda2481/Waypoint-Driver-Manager)";

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    // Streams rather than buffering — Dell's catalog is ~57MB and driver
    // packs run to gigabytes.
    public static async Task DownloadAsync(string url, string destPath, CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(destPath);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }
}
