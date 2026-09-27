using CorsiDuplicate.Core.Grouping;
using CorsiDuplicate.Infrastructure.Matching;
using CorsiDuplicate.Infrastructure.Media;
using CorsiDuplicate.Infrastructure.Scanning;
using FFMpegCore;
using FFMpegCore.Enums;

namespace CorsiDuplicate.Tests.Scanning;

public class VideoScanPipelineIntegrationTests
{
    private static async Task<string> CreateTestVideoAsync(string dir, string name, string colorSpec, int seconds = 3)
    {
        await FfmpegBinaryProvisioner.EnsureAvailableAsync();
        var path = Path.Combine(dir, name);
        await FFMpegArguments
            .FromFileInput($"color=c={colorSpec}:s=320x240:d={seconds}", verifyExists: false, opt => opt.WithCustomArgument("-f lavfi"))
            .OutputToFile(path, overwrite: true, opt => opt
                .WithVideoCodec(VideoCodec.LibX264)
                .WithCustomArgument("-pix_fmt yuv420p"))
            .ProcessAsynchronously();
        return path;
    }

    [Fact(Timeout = 120_000)]
    public async Task Scans_a_generated_video_and_extracts_metadata_and_frame_hashes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd_video_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            await CreateTestVideoAsync(dir, "red.mp4", "red");

            var pipeline = new ScanPipeline();
            var items = await pipeline.ScanFolderAsync(dir);

            Assert.Single(items);
            var item = items[0];
            Assert.True(item.Duration > TimeSpan.Zero, "expected a positive duration from ffprobe");
            Assert.True(item.Width > 0 && item.Height > 0, "expected resolution from ffprobe");
            Assert.NotNull(item.FrameOrientationHashes);
            Assert.True(item.FrameOrientationHashes!.Count > 0, "expected at least one sampled keyframe hash");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task Two_re_encoded_copies_of_the_same_video_group_together()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd_video_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            await CreateTestVideoAsync(dir, "blue_a.mp4", "blue");
            await CreateTestVideoAsync(dir, "blue_b.mp4", "blue"); // same content, independently re-encoded
            await CreateTestVideoAsync(dir, "green.mp4", "green"); // unrelated

            var pipeline = new ScanPipeline();
            var items = await pipeline.ScanFolderAsync(dir);
            Assert.Equal(3, items.Count);

            var grouper = new HammingClusterGrouper(new HsvHistogramComparer());
            var groups = grouper.Group(items, thresholdPercent: 85);

            Assert.Single(groups);
            Assert.Equal(2, groups[0].Items.Count);
            Assert.DoesNotContain(groups[0].Items, i => i.FileName == "green.mp4");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
