using CorsiDuplicate.Infrastructure.Matching;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CorsiDuplicate.Tests.Matching;

public class OrbStructuralMatcherTests
{
    private static Image<Rgba32> MakeTexturedImage(int size, int seed)
    {
        var image = new Image<Rgba32>(size, size);
        var rng = new Random(seed);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    // Blocky pseudo-random pattern (not per-pixel noise) so ORB finds
                    // stable corner-like features instead of noise that washes out
                    // under JPEG-like resampling.
                    var block = (x / 6, y / 6);
                    var v = (byte)((block.Item1 * 37 + block.Item2 * 59 + seed * 13) % 256);
                    row[x] = new Rgba32(v, (byte)(255 - v), (byte)((v * 3) % 256));
                }
            }
        });
        return image;
    }

    [Fact]
    public void Cropped_region_of_the_same_image_matches_structurally()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd_orb_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            using var original = MakeTexturedImage(256, seed: 1);
            var originalPath = Path.Combine(dir, "original.png");
            original.SaveAsPng(originalPath);

            using var cropped = original.Clone(ctx => ctx.Crop(new Rectangle(40, 40, 160, 160)));
            var croppedPath = Path.Combine(dir, "cropped.png");
            cropped.SaveAsPng(croppedPath);

            using var unrelated = MakeTexturedImage(256, seed: 99);
            var unrelatedPath = Path.Combine(dir, "unrelated.png");
            unrelated.SaveAsPng(unrelatedPath);

            using var matcher = new OrbStructuralMatcher();
            var croppedScore = matcher.Match(originalPath, croppedPath);
            var unrelatedScore = matcher.Match(originalPath, unrelatedPath);

            Assert.True(croppedScore > 0, $"expected a cropped region of the same image to score > 0, got {croppedScore}");
            Assert.True(croppedScore > unrelatedScore, $"expected cropped ({croppedScore}) to score higher than unrelated ({unrelatedScore})");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
