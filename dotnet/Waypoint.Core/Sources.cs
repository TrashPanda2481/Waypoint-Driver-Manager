// Driver source contracts. Ported from src/waypoint/sources/base.py and
// src/waypoint/sources/oem/model_pack.py.

namespace Waypoint.Core;

// Every source — Windows Update, an OEM catalog, a local cache — implements
// this. The engine treats them identically and merges their results.
public interface IDriverSource
{
    string SourceId { get; }

    // Metadata only: must not download. Download happens later, for selected
    // candidates only, and is hash-verified before use.
    IReadOnlyList<DriverCandidate> Search(IReadOnlyList<string> hwids);

    // Downloads, verifies against candidate.Sha256, returns the local path.
    // Must throw on hash mismatch — never hand back an unverified download.
    Task<string> FetchAsync(DriverCandidate candidate, string destDir, CancellationToken cancellationToken = default);
}

// One bundle covering a whole system model, for one OS. Resolving it down to
// individual drivers is the OEM installer's job, not Waypoint's.
public sealed record DriverPack(
    string PackId,
    string ModelName,
    string ModelKey, // the vendor-specific identifier this was matched on
    string OsLabel,
    string Version,
    DateOnly? ReleaseDate,
    string Url,
    string HashAlgorithm, // whatever the vendor actually publishes: "sha256", "md5", ...
    string HashValue,
    long SizeBytes,
    string SourceId);

// Dell's and Lenovo's driver-pack catalogs answer "what bundle fits this
// system model", not "what fits this hardware ID" — a different shape from
// IDriverSource, modelled separately rather than stretching that interface.
public interface IModelDriverPackSource
{
    string SourceId { get; }

    IReadOnlyList<DriverPack> PacksForModel(string modelKey);
}
