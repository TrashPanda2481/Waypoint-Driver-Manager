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

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(List<ScanRow>))]
[JsonSerializable(typeof(List<ApplyRow>))]
internal partial class CliJsonContext : JsonSerializerContext;
