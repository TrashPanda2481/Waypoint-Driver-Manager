// The diff card from Architecture.md 3.1: installed against candidate, field
// for field, plus where the candidate came from. 3.1's rule is that no install
// proceeds from a summary number alone, so every field the decision rests on
// has to be on screen.

using Waypoint.Core;

namespace Waypoint.Gui;

public sealed class DetailCard
{
    public static DetailCard Empty { get; } = new() { HasSelection = false };

    public bool HasSelection { get; private init; }

    public string DeviceName { get; private init; } = string.Empty;
    public string ClassName { get; private init; } = string.Empty;
    public string InstanceId { get; private init; } = string.Empty;
    public string Status { get; private init; } = string.Empty;

    public string InstalledVersion { get; private init; } = "-";
    public string InstalledDate { get; private init; } = "-";
    public string InstalledPublisher { get; private init; } = "-";
    public string InstalledSignature { get; private init; } = "-";

    public string CandidateVersion { get; private init; } = "-";
    public string CandidateDate { get; private init; } = "-";
    public string CandidatePublisher { get; private init; } = "-";
    public string CandidateSignature { get; private init; } = "-";
    public string CandidateSource { get; private init; } = "-";
    public string CandidateSize { get; private init; } = "-";

    public string Warnings { get; private init; } = string.Empty;
    public bool HasWarnings => Warnings.Length > 0;

    public string Hwids { get; private init; } = string.Empty;

    public string CandidateHeader { get; private init; } = "Candidate";

    public static DetailCard From(DeviceAssessment assessment)
    {
        var device = assessment.Device;
        var installed = device.Installed;
        var candidate = assessment.Candidates.Count > 0 ? assessment.Candidates[0] : null;

        // Ambiguity is not restated here: Matching sets the flag and its note
        // together, so the wording lives in one place.
        var warnings = new List<string>(assessment.Notes);
        if (device.ProblemCode is int code)
        {
            warnings.Insert(0, $"Device Manager problem code {code}.");
        }

        return new DetailCard
        {
            HasSelection = true,
            DeviceName = device.FriendlyName,
            ClassName = device.ClassName,
            InstanceId = device.InstanceId,
            Status = assessment.Status.ToWireString(),

            InstalledVersion = installed?.Version ?? "none",
            InstalledDate = Date(installed?.DriverDate),
            InstalledPublisher = Text(installed?.Publisher),
            InstalledSignature = installed is null ? "-" : installed.SignatureType.ToWireString(),

            CandidateHeader = assessment.Candidates.Count > 1
                ? $"Best candidate (of {assessment.Candidates.Count})"
                : "Candidate",
            CandidateVersion = candidate?.Version ?? "none found",
            CandidateDate = Date(candidate?.DriverDate),
            CandidatePublisher = Text(candidate?.Publisher),
            CandidateSignature = candidate is null ? "-" : candidate.SignatureType.ToWireString(),
            CandidateSource = Text(candidate?.SourceId),
            CandidateSize = candidate is null || candidate.SizeBytes <= 0
                ? "-"
                : $"{candidate.SizeBytes / 1024d / 1024d:0.#} MB",

            Warnings = string.Join(Environment.NewLine, warnings),
            // Most-specific first, the order the matcher uses.
            Hwids = string.Join(Environment.NewLine, device.Hwids),
        };
    }

    private static string Date(DateOnly? value) => value?.ToString("yyyy-MM-dd") ?? "-";

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
}
