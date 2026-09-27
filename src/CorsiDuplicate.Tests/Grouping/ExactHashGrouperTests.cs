using CorsiDuplicate.Core.Grouping;
using CorsiDuplicate.Core.Models;

namespace CorsiDuplicate.Tests.Grouping;

public class ExactHashGrouperTests
{
    private static MediaItem MakeItem(string name, string sha256, long size = 1000) => new()
    {
        FullPath = $@"C:\test\{name}",
        FileName = name,
        Kind = MediaKind.Photo,
        SizeBytes = size,
        LastWriteUtc = DateTime.UtcNow,
        Sha256 = sha256
    };

    [Fact]
    public void Groups_two_identical_files_and_ignores_unique_file()
    {
        var a = MakeItem("a.jpg", "HASH1");
        var b = MakeItem("b.jpg", "HASH1");
        var c = MakeItem("c.jpg", "HASH2");

        var grouper = new ExactHashGrouper();
        var groups = grouper.Group(new[] { a, b, c }, threshold: 0);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Items.Count);
        Assert.Contains(a, groups[0].Items);
        Assert.Contains(b, groups[0].Items);
        Assert.All(groups[0].Items, i => Assert.Equal(100.0, i.SimilarityToReferencePercent));
    }

    [Fact]
    public void Produces_no_groups_when_all_files_unique()
    {
        var a = MakeItem("a.jpg", "HASH1");
        var b = MakeItem("b.jpg", "HASH2");

        var grouper = new ExactHashGrouper();
        var groups = grouper.Group(new[] { a, b }, threshold: 0);

        Assert.Empty(groups);
    }
}
