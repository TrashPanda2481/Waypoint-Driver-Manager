// Driver source contracts. Ported from sources/base.py + oem/model_pack.py.

namespace Waypoint.Core;

public interface IDriverSource
{
    string SourceId { get; }

    // Metadata only — must not download.
    IReadOnlyList<DriverCandidate> Search(IReadOnlyList<string> hwids);

    // Must throw on hash mismatch — never return an unverified download.
    Task<string> FetchAsync(DriverCandidate candidate, string destDir, CancellationToken cancellationToken = default);
}

// One bundle covering a whole system model. Resolving it to individual
// drivers is the OEM installer's job, not Waypoint's.
public sealed record DriverPack(
    string PackId,
    string ModelName,
    string ModelKey, // vendor-specific model id
    string OsLabel,
    string Version,
    DateOnly? ReleaseDate,
    string Url,
    string HashAlgorithm, // "sha256", "md5" — whatever the vendor publishes
    string HashValue,
    long SizeBytes,
    string SourceId);

// Model-keyed, not HWID-keyed: "what bundle fits this system model".
public interface IModelDriverPackSource
{
    string SourceId { get; }

    IReadOnlyList<DriverPack> PacksForModel(string modelKey);
}
