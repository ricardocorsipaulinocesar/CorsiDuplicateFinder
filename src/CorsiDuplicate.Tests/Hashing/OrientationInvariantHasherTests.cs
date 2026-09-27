using CorsiDuplicate.Core.Hashing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CorsiDuplicate.Tests.Hashing;

public class OrientationInvariantHasherTests
{
    private static string CreateAsymmetricTestImage(string dir)
    {
        // An asymmetric quadrant pattern: distinguishable under every flip/rotation,
        // so a real orientation-invariance bug (e.g. wrong axis) would show up as a
        // large residual Hamming distance instead of accidentally matching by symmetry.
        using var image = new Image<Rgba32>(64, 64);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = x < 32 && y < 32 ? new Rgba32(0, 0, 0)
                        : x >= 32 && y < 16 ? new Rgba32(255, 0, 0)
                        : x < 16 && y >= 48 ? new Rgba32(0, 0, 255)
                        : new Rgba32(255, 255, 255);
                }
            }
        });

        var path = Path.Combine(dir, "asymmetric.png");
        image.SaveAsPng(path);
        return path;
    }

    [Fact]
    public void Flipped_and_rotated_copies_hash_within_a_small_distance_of_the_original()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd_hash_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var originalPath = CreateAsymmetricTestImage(dir);
            var hasher = new OrientationInvariantHasher();
            var originalOrientations = hasher.HashAllOrientations(originalPath);

            using var original = Image.Load<Rgba32>(originalPath);

            foreach (var (label, transform) in new (string, Action<IImageProcessingContext>)[]
            {
                ("horizontal flip", ctx => ctx.Flip(FlipMode.Horizontal)),
                ("vertical flip", ctx => ctx.Flip(FlipMode.Vertical)),
                ("rotate 90", ctx => ctx.Rotate(RotateMode.Rotate90)),
                ("rotate 180", ctx => ctx.Rotate(RotateMode.Rotate180)),
                ("rotate 270", ctx => ctx.Rotate(RotateMode.Rotate270)),
            })
            {
                using var variant = original.Clone(transform);
                var variantPath = Path.Combine(dir, $"variant_{label.Replace(" ", "_")}.png");
                variant.SaveAsPng(variantPath);

                var variantHash = hasher.Hash(variantPath);
                var distance = HammingDistance.MinAcrossOrientations(originalOrientations, new[] { variantHash });

                Assert.True(distance <= 4, $"{label}: expected near-zero distance, got {distance}");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Unrelated_image_is_far_from_the_original_even_across_all_orientations()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd_hash_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var originalPath = CreateAsymmetricTestImage(dir);
            using var unrelated = new Image<Rgba32>(64, 64);
            unrelated.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        row[x] = new Rgba32(50, 205, 50);
                    }
                }
            });
            var unrelatedPath = Path.Combine(dir, "unrelated.png");
            unrelated.SaveAsPng(unrelatedPath);

            var hasher = new OrientationInvariantHasher();
            var originalOrientations = hasher.HashAllOrientations(originalPath);
            var unrelatedHash = hasher.Hash(unrelatedPath);

            var distance = HammingDistance.MinAcrossOrientations(originalOrientations, new[] { unrelatedHash });
            Assert.True(distance > 10, $"expected clearly different images to be far apart, got {distance}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
