using CorsiDuplicate.Infrastructure.Matching;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CorsiDuplicate.Tests.Matching;

public class HsvHistogramComparerTests
{
    private static void FillCheckerboard(Image<Rgba32> image, double brightnessScale)
    {
        // Fixed hue (orange-ish), varying only brightness by scaling all channels by
        // the same factor — a true HSV "value" change, unlike shifting channels
        // independently (which would rotate hue and defeat the point of this test).
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var on = (x / 8 + y / 8) % 2 == 0;
                    var (r, g, b) = on ? (200.0, 120.0, 40.0) : (90.0, 55.0, 20.0);
                    row[x] = new Rgba32(
                        (byte)Math.Clamp(r * brightnessScale, 0, 255),
                        (byte)Math.Clamp(g * brightnessScale, 0, 255),
                        (byte)Math.Clamp(b * brightnessScale, 0, 255));
                }
            }
        });
    }

    [Fact]
    public void Brightness_adjusted_copy_still_scores_highly_similar()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd_hist_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            using var original = new Image<Rgba32>(64, 64);
            FillCheckerboard(original, brightnessScale: 1.0);
            var originalPath = Path.Combine(dir, "original.png");
            original.SaveAsPng(originalPath);

            using var brightened = new Image<Rgba32>(64, 64);
            FillCheckerboard(brightened, brightnessScale: 1.2);
            var brightenedPath = Path.Combine(dir, "brightened.png");
            brightened.SaveAsPng(brightenedPath);

            using var unrelated = new Image<Rgba32>(64, 64);
            unrelated.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        row[x] = new Rgba32(20, 200, 20);
                    }
                }
            });
            var unrelatedPath = Path.Combine(dir, "unrelated.png");
            unrelated.SaveAsPng(unrelatedPath);

            var comparer = new HsvHistogramComparer();
            var histOriginal = comparer.ComputeHistogram(originalPath);
            var histBrightened = comparer.ComputeHistogram(brightenedPath);
            var histUnrelated = comparer.ComputeHistogram(unrelatedPath);

            var similarSimilarity = comparer.Compare(histOriginal, histBrightened);
            var unrelatedSimilarity = comparer.Compare(histOriginal, histUnrelated);

            Assert.True(similarSimilarity > 0.6, $"expected brightened copy to score highly similar, got {similarSimilarity}");
            Assert.True(unrelatedSimilarity < similarSimilarity, "expected unrelated image to score lower than the brightened copy");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
