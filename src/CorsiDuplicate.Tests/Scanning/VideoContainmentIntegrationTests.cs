using CorsiDuplicate.Core.Matching;
using CorsiDuplicate.Infrastructure.Media;
using FFMpegCore;
using FFMpegCore.Enums;

namespace CorsiDuplicate.Tests.Scanning;

public class VideoContainmentIntegrationTests
{
    private static async Task GenerateAsync(string lavfiSource, string path)
    {
        await FFMpegArguments
            .FromFileInput(lavfiSource, verifyExists: false, opt => opt.WithCustomArgument("-f lavfi"))
            .OutputToFile(path, overwrite: true, opt => opt
                .WithVideoCodec(VideoCodec.LibX264)
                .WithCustomArgument("-pix_fmt yuv420p"))
            .ProcessAsynchronously();
    }

    [Fact(Timeout = 300_000)]
    public async Task A_re_encoded_excerpt_is_found_inside_its_source_video()
    {
        await FfmpegBinaryProvisioner.EnsureAvailableAsync();
        var dir = Path.Combine(Path.GetTempPath(), "cd_contained_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            // The Mandelbrot zoom changes every second, so each second has its own signature.
            var source = Path.Combine(dir, "source.mp4");
            await GenerateAsync("mandelbrot=s=320x240:r=10,trim=duration=90", source);

            // 20 s cut from 0:40, downscaled and re-encoded like a typical shared clip.
            var clip = Path.Combine(dir, "clip.mp4");
            await FFMpegArguments
                .FromFileInput(source, verifyExists: true, opt => opt.Seek(TimeSpan.FromSeconds(40)))
                .OutputToFile(clip, overwrite: true, opt => opt
                    .WithDuration(TimeSpan.FromSeconds(20))
                    .WithVideoCodec(VideoCodec.LibX264)
                    .WithCustomArgument("-vf scale=240:180 -crf 30 -pix_fmt yuv420p"))
                .ProcessAsynchronously();

            var unrelated = Path.Combine(dir, "unrelated.mp4");
            await GenerateAsync("testsrc2=s=320x240:r=10:d=20", unrelated);

            var sourceFp = await VideoFingerprintExtractor.ExtractAsync(source);
            var clipFp = await VideoFingerprintExtractor.ExtractAsync(clip);
            var unrelatedFp = await VideoFingerprintExtractor.ExtractAsync(unrelated);
            Assert.NotNull(sourceFp);
            Assert.NotNull(clipFp);
            Assert.NotNull(unrelatedFp);

            var match = ContainmentMatcher.Find(clipFp!, sourceFp!);
            Assert.NotNull(match);
            Assert.InRange(match!.StartSeconds, 38, 42);

            Assert.Null(ContainmentMatcher.Find(unrelatedFp!, sourceFp!));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
