using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PConTRoL.Core.Updates;

namespace PConTRoL.Tests;

public class UpdateFeedTests
{
    private const string Feed = """
        {"Assets":[
          {"PackageId":"PConTRoL","Version":"1.9.0","Type":"Full","FileName":"PConTRoL-1.9.0-full.nupkg","SHA256":"AA","Size":10},
          {"PackageId":"PConTRoL","Version":"1.11.0","Type":"Full","FileName":"PConTRoL-1.11.0-full.nupkg","SHA256":"BB","Size":20,"NotesMarkdown":"neu"},
          {"PackageId":"PConTRoL","Version":"1.11.0","Type":"Delta","FileName":"PConTRoL-1.11.0-delta.nupkg","SHA256":"CC","Size":5},
          {"PackageId":"PConTRoL","Version":"9.9.9","Type":"Full","FileName":"..\\boese.nupkg","SHA256":"DD","Size":1}
        ]}
        """;

    [Fact]
    public void Latest_full_package_is_picked_and_feed_contains_only_it()
    {
        var p = UpdateFeed.LatestFull(Feed)!;

        Assert.Equal(new Version(1, 11, 0), p.Version);
        Assert.Equal("PConTRoL-1.11.0-full.nupkg", p.FileName);
        Assert.Equal(20, p.Size);
        var assets = (JsonArray)JsonNode.Parse(p.FeedJson)!["Assets"]!;
        var only = Assert.Single(assets);
        Assert.Equal("BB", (string?)only!["SHA256"]);
        Assert.Equal("neu", (string?)only["NotesMarkdown"]); // Änderungsnotiz bleibt erhalten
    }

    [Fact]
    public void Feed_without_full_package_gives_nothing()
    {
        Assert.Null(UpdateFeed.LatestFull("""{"Assets":[{"Version":"1.0.0","Type":"Delta","FileName":"x.nupkg","SHA256":"A"}]}"""));
        Assert.Null(UpdateFeed.LatestFull("""{"nix":1}"""));
    }

    [Fact]
    public void Checksum_must_match()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, [1, 2, 3]);
            var sha = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 }));
            Assert.True(UpdateFeed.Matches(file, new FeedPackage(new Version(1, 0, 0), "x", sha.ToLowerInvariant(), 3, "")));
            Assert.False(UpdateFeed.Matches(file, new FeedPackage(new Version(1, 0, 0), "x", sha, 4, "")));
            Assert.False(UpdateFeed.Matches(file, new FeedPackage(new Version(1, 0, 0), "x", "00", 3, "")));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
