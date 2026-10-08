using CorsiDuplicate.Core.Matching;
using CorsiDuplicate.Core.Models;
using CorsiDuplicate.Infrastructure.Media;
using CorsiDuplicate.Infrastructure.Scanning;
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

    /// <summary>Cuts [start, start+seconds) out of <paramref name="source"/> and re-encodes it
    /// through <paramref name="filter"/>, like a typical re-shared clip.</summary>
    private static async Task CutAsync(string source, string output, int start, int seconds, string filter)
    {
        await FFMpegArguments
            .FromFileInput(source, verifyExists: true, opt => opt.Seek(TimeSpan.FromSeconds(start)))
            .OutputToFile(output, overwrite: true, opt => opt
                .WithDuration(TimeSpan.FromSeconds(seconds))
                .WithVideoCodec(VideoCodec.LibX264)
                .WithCustomArgument($"-vf {filter} -crf 30 -pix_fmt yuv420p"))
            .ProcessAsynchronously();
    }

    private static async Task<(string Dir, string Source)> CreateSourceAsync()
    {
        await FfmpegBinaryProvisioner.EnsureAvailableAsync();
        var dir = Path.Combine(Path.GetTempPath(), "cd_contained_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        // The Mandelbrot zoom changes every second, so each second has its own signature.
        var source = Path.Combine(dir, "source.mp4");
        await GenerateAsync("mandelbrot=s=320x240:r=10,trim=duration=90", source);
        return (dir, source);
    }

    [Fact(Timeout = 300_000)]
    public async Task A_re_encoded_excerpt_is_found_inside_its_source_video()
    {
        var (dir, source) = await CreateSourceAsync();
        try
        {
            var clip = Path.Combine(dir, "clip.mp4");
            await CutAsync(source, clip, start: 40, seconds: 20, filter: "scale=240:180");
            var unrelated = Path.Combine(dir, "unrelated.mp4");
            await GenerateAsync("testsrc2=s=320x240:r=10:d=20", unrelated);

            var sourceFp = await VideoFingerprintExtractor.ExtractAsync(source);
            var clipFp = await VideoFingerprintExtractor.ExtractAsync(clip);
            var unrelatedFp = await VideoFingerprintExtractor.ExtractAsync(unrelated);

            var match = ContainmentMatcher.Find(clipFp!, sourceFp!);
            Assert.NotNull(match);
            Assert.False(match!.IsMirrored);
            Assert.InRange(match.StartSeconds, 38, 42);
            Assert.Null(ContainmentMatcher.Find(unrelatedFp!, sourceFp!));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task Mirrored_and_letterboxed_excerpts_are_found()
    {
        var (dir, source) = await CreateSourceAsync();
        try
        {
            var mirrored = Path.Combine(dir, "mirrored.mp4");
            await CutAsync(source, mirrored, start: 30, seconds: 20, filter: "hflip");
            // Squeezed into a square frame with black bars above and below.
            var letterboxed = Path.Combine(dir, "letterboxed.mp4");
            await CutAsync(source, letterboxed, start: 55, seconds: 20, filter: "scale=320:240,pad=320:400:0:80:black");

            var sourceFp = await VideoFingerprintExtractor.ExtractAsync(source);

            var mirroredMatch = ContainmentMatcher.Find((await VideoFingerprintExtractor.ExtractAsync(mirrored))!, sourceFp!);
            Assert.NotNull(mirroredMatch);
            Assert.True(mirroredMatch!.IsMirrored);
            Assert.InRange(mirroredMatch.StartSeconds, 28, 32);

            var letterboxMatch = ContainmentMatcher.Find((await VideoFingerprintExtractor.ExtractAsync(letterboxed))!, sourceFp!);
            Assert.NotNull(letterboxMatch);
            Assert.InRange(letterboxMatch!.StartSeconds, 53, 57);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task A_compilation_of_two_videos_shares_a_segment_with_each()
    {
        var (dir, sourceA) = await CreateSourceAsync();
        try
        {
            var sourceB = Path.Combine(dir, "source_b.mp4");
            await GenerateAsync("mandelbrot=s=320x240:r=10:start_x=-0.7436438870:start_y=0.1318259042,trim=duration=60", sourceB);

            var partA = Path.Combine(dir, "part_a.mp4");
            var partB = Path.Combine(dir, "part_b.mp4");
            await CutAsync(sourceA, partA, start: 20, seconds: 15, filter: "scale=320:240");
            await CutAsync(sourceB, partB, start: 30, seconds: 15, filter: "scale=320:240");

            var list = Path.Combine(dir, "list.txt");
            await File.WriteAllTextAsync(list, $"file '{partA.Replace("\\", "/")}'\nfile '{partB.Replace("\\", "/")}'\n");
            var compilation = Path.Combine(dir, "compilation.mp4");
            await FFMpegArguments
                .FromFileInput(list, verifyExists: true, opt => opt.WithCustomArgument("-f concat -safe 0"))
                .OutputToFile(compilation, overwrite: true, opt => opt.WithVideoCodec(VideoCodec.LibX264).WithCustomArgument("-pix_fmt yuv420p"))
                .ProcessAsynchronously();

            var compilationFp = (await VideoFingerprintExtractor.ExtractAsync(compilation))!;
            var withA = ContainmentMatcher.Compare(compilationFp, (await VideoFingerprintExtractor.ExtractAsync(sourceA))!);
            var withB = ContainmentMatcher.Compare(compilationFp, (await VideoFingerprintExtractor.ExtractAsync(sourceB))!);

            var segmentA = Assert.Single(withA.Segments);
            Assert.InRange(segmentA.ClipStart, 0, 2);
            Assert.InRange(segmentA.SourceStart, 18, 22);
            var segmentB = Assert.Single(withB.Segments);
            Assert.InRange(segmentB.ClipStart, 13, 17);
            Assert.InRange(segmentB.SourceStart, 28, 32);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task A_videos_only_scan_skips_photos_but_keeps_them_cached()
    {
        await FfmpegBinaryProvisioner.EnsureAvailableAsync();
        var dir = Path.Combine(Path.GetTempPath(), "cd_videos_only_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            await GenerateAsync("testsrc2=s=320x240:r=10:d=3", Path.Combine(dir, "video.mp4"));
            await FFMpegArguments
                .FromFileInput("testsrc2=s=320x240:d=1", verifyExists: false, opt => opt.WithCustomArgument("-f lavfi"))
                .OutputToFile(Path.Combine(dir, "photo.jpg"), overwrite: true, opt => opt.WithCustomArgument("-frames:v 1"))
                .ProcessAsynchronously();

            var pipeline = new ScanPipeline();
            Assert.Equal(2, (await pipeline.ScanFolderAsync(dir)).Count);

            var videosOnly = await pipeline.ScanFolderAsync(dir, videosOnly: true);
            Assert.Equal(MediaKind.Video, Assert.Single(videosOnly).Kind);

            var cached = new ScanCacheStore().Load(dir);
            Assert.Contains(cached.Values, c => c.Kind == MediaKind.Photo);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
