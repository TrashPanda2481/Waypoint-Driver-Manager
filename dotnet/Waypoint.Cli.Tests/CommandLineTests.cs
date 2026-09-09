// The flag surface and exit codes are contract for RMM callers, so they get
// tests rather than being verified by hand once.

using Waypoint.Cli;
using Waypoint.Core;

namespace Waypoint.Cli.Tests;

public class CommandLineTests
{
    private static Options Parse(params string[] args)
    {
        var options = CommandLine.Parse(args);
        Assert.NotNull(options);
        return options!;
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("plan")]
    [InlineData("apply")]
    public void RecognisesEachVerb(string verb)
    {
        Assert.Equal(verb, Parse(verb).Command);
    }

    [Fact]
    public void GlobalOptionsMayPrecedeTheVerb()
    {
        // The documented shape: waypoint --oem scan --json
        var options = Parse("--oem", "scan", "--json");

        Assert.Equal("scan", options.Command);
        Assert.True(options.Oem);
        Assert.True(options.Json);
    }

    [Fact]
    public void GlobalOptionsMayAlsoFollowTheVerb()
    {
        var options = Parse("scan", "--oem", "--json");

        Assert.Equal("scan", options.Command);
        Assert.True(options.Oem);
    }

    [Fact]
    public void ValueOptionsTakeTheFollowingArgument()
    {
        var options = Parse(
            "--cache-dir", @"C:\cache",
            "--audit-log", @"C:\audit.jsonl",
            "plan",
            "--out", @"C:\plan.json");

        Assert.Equal(@"C:\cache", options.CacheDir);
        Assert.Equal(@"C:\audit.jsonl", options.AuditLogPath);
        Assert.Equal(@"C:\plan.json", options.OutPath);
    }

    [Fact]
    public void MinSignatureDefaultsToAttestation()
    {
        Assert.Equal(SignatureType.Attestation, Parse("scan").MinSignature);
    }

    [Theory]
    [InlineData("whql", SignatureType.Whql)]
    [InlineData("attestation", SignatureType.Attestation)]
    [InlineData("test_signed", SignatureType.TestSigned)]
    [InlineData("unsigned", SignatureType.Unsigned)]
    public void MinSignatureAcceptsEveryWireValue(string wire, SignatureType expected)
    {
        Assert.Equal(expected, Parse("--min-signature", wire, "scan").MinSignature);
    }

    [Fact]
    public void UnknownSignatureTierIsRejected()
    {
        // Fail closed: a typo must not silently widen the policy.
        Assert.Null(CommandLine.Parse(["--min-signature", "probably-fine", "scan"]));
    }

    [Fact]
    public void UnknownArgumentIsRejected()
    {
        Assert.Null(CommandLine.Parse(["scan", "--turbo"]));
    }

    [Fact]
    public void MissingCommandIsRejected()
    {
        Assert.Null(CommandLine.Parse(["--json"]));
    }

    [Fact]
    public void ValueOptionWithNoValueIsRejected()
    {
        Assert.Null(CommandLine.Parse(["scan", "--cache-dir"]));
    }

    [Fact]
    public void ApplyDefaultsToDryRun()
    {
        // The safety default: installing is opt-in, never implied by the verb.
        Assert.False(Parse("apply").Apply);
        Assert.True(Parse("apply", "--apply").Apply);
    }

    [Fact]
    public void ConfirmIsRepeatableAndCaseInsensitive()
    {
        var options = Parse("apply", "--confirm", "PCI\\DEV1", "--confirm", "PCI\\DEV2");

        Assert.Equal(2, options.Confirmed.Count);
        Assert.Contains("pci\\dev1", options.Confirmed);
    }

    [Fact]
    public void ConfirmAllIsSeparateFromIndividualConfirmations()
    {
        Assert.True(Parse("apply", "--confirm-all").ConfirmAll);
        Assert.Empty(Parse("apply", "--confirm-all").Confirmed);
    }

    [Fact]
    public void ForceOemRefreshWithoutOemStillParses()
    {
        // Warns on stderr rather than failing, so a scheduled job that always
        // passes the flag is not broken by it.
        var options = Parse("scan", "--force-oem-refresh");

        Assert.True(options.ForceOemRefresh);
        Assert.False(options.Oem);
    }

    [Fact]
    public void ExitCodesMatchTheDocumentedContract()
    {
        Assert.Equal(0, Commands.ExitClean);
        Assert.Equal(1, Commands.ExitActionNeeded);
        Assert.Equal(2, Commands.ExitError);
    }
}
