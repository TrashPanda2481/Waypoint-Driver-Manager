// Streaming file hashes, lowercase hex to match Python's hexdigest().

using System.Security.Cryptography;

namespace Waypoint.Sources;

public static class Hashing
{
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // Only for vendor catalogs that publish nothing stronger (Dell's CatalogPC).
    public static string Md5File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
    }
}
