// Every release so far is a prerelease and tags carry suffixes the MSI
// version can't, so the comparison rules are pinned here.

using Waypoint.Gui;

namespace Waypoint.Gui.Tests;

public class UpdateCheckerTests
{
    private const string Releases = """
        [
          { "tag_name": "v0.2.1-alpha.1", "draft": false, "prerelease": true },
          { "tag_name": "v0.3.0", "draft": true, "prerelease": false },
          { "tag_name": "not-a-version", "draft": false, "prerelease": false },
          { "tag_name": "v0.2.0-alpha.1", "draft": false, "prerelease": true }
        ]
        """;

    [Theory]
    [InlineData("v0.2.1-alpha.1", "0.2.1")]
    [InlineData("0.2.1", "0.2.1")]
    [InlineData("v1.4.0+build.7", "1.4.0")]
    public void ParseTag_TakesNumericCore(string tag, string expected)
        => Assert.Equal(Version.Parse(expected), UpdateChecker.ParseTag(tag));

    [Theory]
    [InlineData("latest")]
    [InlineData("v1.2")]
    public void ParseTag_RejectsNonReleaseTags(string tag)
        => Assert.Null(UpdateChecker.ParseTag(tag));

    [Fact]
    public void PickLatest_CountsPrereleases_SkipsDraftsAndJunk()
    {
        var latest = UpdateChecker.PickLatest(Releases);
        Assert.Equal("v0.2.1-alpha.1", latest?.Tag);
    }

    [Fact]
    public void PickLatest_EmptyList()
        => Assert.Null(UpdateChecker.PickLatest("[]"));

    [Theory]
    [InlineData("0.2.0", "Available")]
    [InlineData("0.2.1", "UpToDate")]
    [InlineData("0.3.0", "UpToDate")]
    [InlineData("0.0.0", "DevBuild")]
    public void Evaluate(string current, string expected)
    {
        var latest = new ReleaseInfo(new Version(0, 2, 1), "v0.2.1-alpha.1");
        Assert.Equal(Enum.Parse<UpdateState>(expected), UpdateChecker.Evaluate(Version.Parse(current), latest).State);
    }

    [Fact]
    public void ReleasePage_StaysOnOurRepo()
    {
        var page = new ReleaseInfo(new Version(0, 2, 1), "v0.2.1-alpha.1").Page;
        Assert.Equal("https://github.com/TrashPanda2481/Waypoint-Driver-Manager/releases/tag/v0.2.1-alpha.1",
            page.AbsoluteUri);
    }
}
