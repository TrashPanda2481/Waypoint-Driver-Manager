// The diff card is what 3.1's "no install proceeds from a summary number
// alone" rule rests on, so the fields it must show are pinned here.

using Waypoint.Core;
using Waypoint.Gui;

namespace Waypoint.Gui.Tests;

public class DetailCardTests
{
    private static Device Device(InstalledDriver? installed = null, int? problemCode = null)
        => new(
            Hwids: ["PCI\\VEN_10DE&DEV_2504", "PCI\\VEN_10DE&DEV_2504&CC_030000"],
            ClassGuid: "{4d36e968-e325-11ce-bfc1-08002be10318}",
            ClassName: "Display",
            FriendlyName: "NVIDIA GeForce RTX 3060",
            InstanceId: "PCI\\VEN_10DE&DEV_2504\\3&11583659&0&0010",
            ProblemCode: problemCode,
            Installed: installed);

    private static DriverCandidate Candidate(string version = "33.0.1.0")
        => new(
            Hwid: "PCI\\VEN_10DE&DEV_2504",
            ClassGuid: "{4d36e968-e325-11ce-bfc1-08002be10318}",
            Version: version,
            DriverDate: new DateOnly(2026, 8, 1),
            Publisher: "NVIDIA",
            SignatureType: SignatureType.Whql,
            Sha256: new string('a', 64),
            SizeBytes: 700 * 1024 * 1024,
            SourceId: "dell_catalog",
            SourceUrl: "https://example.invalid/catalog",
            DownloadUri: "https://example.invalid/driver.cab");

    private static DeviceAssessment Assessment(
        Device device,
        DeviceStatus status = DeviceStatus.UpgradeAvailable,
        bool ambiguous = false,
        params DriverCandidate[] candidates)
        => new()
        {
            Device = device,
            Status = status,
            Ambiguous = ambiguous,
            Candidates = [.. candidates],
        };

    [Fact]
    public void ShowsBothSidesOfTheComparison()
    {
        var installed = new InstalledDriver("32.0.16.1088", new DateOnly(2026, 7, 22), "NVIDIA", SignatureType.Attestation);

        var card = DetailCard.From(Assessment(Device(installed), candidates: Candidate()));

        Assert.Equal("32.0.16.1088", card.InstalledVersion);
        Assert.Equal("2026-07-22", card.InstalledDate);
        Assert.Equal("attestation", card.InstalledSignature);

        Assert.Equal("33.0.1.0", card.CandidateVersion);
        Assert.Equal("2026-08-01", card.CandidateDate);
        Assert.Equal("whql", card.CandidateSignature);
        Assert.Equal("dell_catalog", card.CandidateSource);
    }

    [Fact]
    public void SaysWhenThereIsNoInstalledDriverRatherThanShowingBlanks()
    {
        var card = DetailCard.From(Assessment(Device(), DeviceStatus.Missing, candidates: Candidate()));

        Assert.Equal("none", card.InstalledVersion);
        Assert.Equal("-", card.InstalledDate);
    }

    [Fact]
    public void SaysWhenNoCandidateWasFound()
    {
        var card = DetailCard.From(Assessment(Device(), DeviceStatus.UpToDate));

        Assert.Equal("none found", card.CandidateVersion);
        Assert.Equal("-", card.CandidateSize);
    }

    [Fact]
    public void NamesHowManyCandidatesWereRejectedForTheOneShown()
    {
        // Otherwise the pane implies the shown candidate was the only option.
        var card = DetailCard.From(
            Assessment(Device(), candidates: [Candidate("33.0.1.0"), Candidate("32.9.0.0")]));

        Assert.Equal("Best candidate (of 2)", card.CandidateHeader);
    }

    [Fact]
    public void ProblemCodeIsSurfacedAsAWarning()
    {
        var card = DetailCard.From(Assessment(Device(problemCode: 28), DeviceStatus.Problem));

        Assert.True(card.HasWarnings);
        Assert.Contains("28", card.Warnings);
    }

    [Fact]
    public void AmbiguityIsReportedFromTheEngineNoteNotRestatedHere()
    {
        // Matching sets the flag and its note together; duplicating the wording
        // in the GUI would leave two copies to keep in sync.
        var assessment = Assessment(Device(), DeviceStatus.UpToDate, ambiguous: true);
        assessment.Notes.Add("Hardware ID shared with another detected device.");

        var card = DetailCard.From(assessment);

        Assert.Equal("Hardware ID shared with another detected device.", card.Warnings);
    }

    [Fact]
    public void AHealthyDeviceShowsNoWarningBox()
    {
        Assert.False(DetailCard.From(Assessment(Device(), DeviceStatus.UpToDate)).HasWarnings);
    }

    [Fact]
    public void HardwareIdsKeepMostSpecificFirst()
    {
        // The order the matcher uses; re-sorting would misrepresent the match.
        var card = DetailCard.From(Assessment(Device()));

        Assert.StartsWith("PCI\\VEN_10DE&DEV_2504" + Environment.NewLine, card.Hwids);
    }

    [Fact]
    public void EmptyCardRendersNothingSelected()
    {
        Assert.False(DetailCard.Empty.HasSelection);
    }
}
