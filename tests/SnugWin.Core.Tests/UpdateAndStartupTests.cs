using System.Xml.Linq;
using SnugWin.Core.Support;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class UpdateAndStartupTests
{
    [Theory]
    [InlineData("v1.1.2", "1.1.1+8fc71d4", true)]
    [InlineData("v1.2.0", "1.1.9", true)]
    [InlineData("v1.1.1", "1.1.1+abc", false)]
    [InlineData("v1.1.0", "1.1.1", false)]
    [InlineData("invalid", "1.1.0", false)]
    public void AppVersionHelper_ComparesSemVerCleanly(string candidate, string current, bool expectedNewer)
    {
        Assert.Equal(expectedNewer, AppVersionHelper.IsNewer(candidate, current));
    }

    [Theory]
    [InlineData("v1.2.0", ReleasePackageKind.SelfContained, "SnugWin-v1.2.0-win-x64-selfcontained.zip")]
    [InlineData("1.2.0", ReleasePackageKind.FrameworkDependent, "SnugWin-v1.2.0-win-x64-frameworkdependent.zip")]
    public void AppVersionHelper_FormatsAssetName(string tag, ReleasePackageKind kind, string expected)
    {
        Assert.Equal(expected, AppVersionHelper.PreferredAssetName(tag, kind));
    }

    [Fact]
    public void ChecksumParser_FindsMatchingHashCaseInsensitive()
    {
        const string text = """
            643AD866A8BA65A214BD8AE7239EA338174BD127629013055195B418BC6E598F  SnugWin-v1.1.1-win-x64-frameworkdependent.zip
            a6e8201c5d361a05cd5003e309891c8edc64c59d6e06d1603abff7dfba7a0214  dist/SnugWin-v1.1.1-win-x64-selfcontained.zip
            """;

        Assert.True(ChecksumParser.TryFindSha256(text, "snugwin-v1.1.1-win-x64-selfcontained.zip", out string sha));
        Assert.Equal("A6E8201C5D361A05CD5003E309891C8EDC64C59D6E06D1603ABFF7DFBA7A0214", sha);
        Assert.False(ChecksumParser.TryFindSha256(text, "missing.zip", out _));
    }

    [Fact]
    public void GitHubReleaseParser_ParsesLatestReleaseJson()
    {
        const string json = """
            {
              "tag_name": "v1.2.0",
              "html_url": "https://github.com/nicocart/snug-win/releases/tag/v1.2.0",
              "body": "## Changes\n- Added auto update",
              "assets": [
                {
                  "name": "SnugWin-v1.2.0-win-x64-selfcontained.zip",
                  "browser_download_url": "https://example.invalid/self.zip",
                  "size": 76000000
                },
                {
                  "name": "checksums.txt",
                  "browser_download_url": "https://example.invalid/checksums.txt",
                  "size": 325
                }
              ]
            }
            """;

        var info = GitHubReleaseParser.ParseLatestRelease(json);
        Assert.NotNull(info);
        Assert.Equal("v1.2.0", info!.TagName);
        Assert.Equal("1.2.0", info.NormalizedVersion);
        Assert.Equal(2, info.Assets.Count);
        Assert.Equal("checksums.txt", info.Assets[1].Name);
    }

    [Fact]
    public void StartupTaskDefinition_BuildsValidHighestAvailableTaskXml()
    {
        string xml = StartupTaskDefinition.BuildTaskXml(@"C:\Tools\Snug & Win\SnugWin.App.exe");
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("HighestAvailable", doc.Root!.Element(ns + "Principals")!.Element(ns + "Principal")!.Element(ns + "RunLevel")!.Value);
        Assert.Equal("PT20S", doc.Root.Element(ns + "Triggers")!.Element(ns + "LogonTrigger")!.Element(ns + "Delay")!.Value);
        Assert.Equal("PT0S", doc.Root.Element(ns + "Settings")!.Element(ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("false", doc.Root.Element(ns + "Settings")!.Element(ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal(@"C:\Tools\Snug & Win\SnugWin.App.exe", doc.Root.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "Command")!.Value);
        Assert.Equal(StartupTaskDefinition.MinimizedArg, doc.Root.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "Arguments")!.Value);
    }
}
