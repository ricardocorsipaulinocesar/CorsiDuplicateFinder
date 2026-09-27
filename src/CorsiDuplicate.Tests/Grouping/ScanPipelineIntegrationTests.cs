using CorsiDuplicate.Core.Grouping;
using CorsiDuplicate.Infrastructure.Scanning;

namespace CorsiDuplicate.Tests.Grouping;

public class ScanPipelineIntegrationTests
{
    [Fact]
    public async Task Scans_real_folder_and_groups_byte_identical_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd_pipeline_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.jpg"), "identical-content-blob");
            await File.WriteAllTextAsync(Path.Combine(dir, "b.jpg"), "identical-content-blob");
            await File.WriteAllTextAsync(Path.Combine(dir, "c.jpg"), "different-content-here");

            var pipeline = new ScanPipeline();
            var items = await pipeline.ScanFolderAsync(dir);
            Assert.Equal(3, items.Count);

            var grouper = new ExactHashGrouper();
            var groups = grouper.Group(items, threshold: 0);

            Assert.Single(groups);
            Assert.Equal(2, groups[0].Items.Count);
            Assert.Contains(groups[0].Items, i => i.FileName == "a.jpg");
            Assert.Contains(groups[0].Items, i => i.FileName == "b.jpg");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
