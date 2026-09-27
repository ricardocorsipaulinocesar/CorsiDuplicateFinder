using CoenM.ImageHash;
using CoenM.ImageHash.HashAlgorithms;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CorsiDuplicate.Core.Hashing;

/// <summary>
/// Wraps CoenM's perceptual hash (pHash) algorithm and additionally hashes all 8
/// dihedral-group orientation variants of an image, so a horizontally/vertically
/// flipped or 90/180/270-rotated copy still produces a matching hash under
/// HammingDistance.MinAcrossOrientations.
/// </summary>
public sealed class OrientationInvariantHasher : IPerceptualHasher
{
    private readonly IImageHash _algorithm = new PerceptualHash();

    public ulong Hash(string imagePath)
    {
        using var image = Image.Load<Rgba32>(imagePath);
        return _algorithm.Hash(image);
    }

    public ulong[] HashAllOrientations(string imagePath)
    {
        using var original = Image.Load<Rgba32>(imagePath);
        return HashAllOrientations(original);
    }

    public ulong[] HashAllOrientations(Image<Rgba32> original)
    {
        var hashes = new ulong[8];
        var index = 0;

        foreach (var flip in new[] { false, true })
        {
            using var flipped = original.Clone(ctx =>
            {
                if (flip)
                {
                    ctx.Flip(FlipMode.Horizontal);
                }
            });

            foreach (var rotation in new[] { RotateMode.None, RotateMode.Rotate90, RotateMode.Rotate180, RotateMode.Rotate270 })
            {
                using var rotated = flipped.Clone(ctx => ctx.Rotate(rotation));
                hashes[index++] = _algorithm.Hash(rotated);
            }
        }

        return hashes;
    }
}
