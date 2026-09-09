// Output shapes for --json, source-generated so the AOT binary needs no
// reflection. Key names match the Python CLI's payloads.

using System.Text.Json.Serialization;

namespace Waypoint.Cli;

internal sealed record ScanRow(
    string InstanceId,
    string FriendlyName,
    string ClassName,
    string Status,
    bool Ambiguous,
    int CandidateCount,
    IReadOnlyList<string> Notes);

internal sealed record ApplyRow(
    string InstanceId,
    bool Success,
    IReadOnlyList<string> Commands,
    string Message);

internal sealed record DriverPackRow(
    string SourceId,
    string ModelName,
    string ModelKey,
    string OsLabel,
    string Version,
    string? ReleaseDate,
    string Url,
    string HashAlgorithm,
    string HashValue,
    long SizeBytes);

internal sealed record DriverPackReport(
    string Manufacturer,
    string ProductName,
    string Sku,
    string BaseboardProduct,
    IReadOnlyList<DriverPackRow> Packs,
    string? HpPlatform,
    IReadOnlyList<string> FailedSources);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(List<ScanRow>))]
[JsonSerializable(typeof(List<ApplyRow>))]
[JsonSerializable(typeof(DriverPackReport))]
internal partial class CliJsonContext : JsonSerializerContext;
