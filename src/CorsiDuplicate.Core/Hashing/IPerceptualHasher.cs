namespace CorsiDuplicate.Core.Hashing;

public interface IPerceptualHasher
{
    /// <summary>
    /// Computes a 64-bit perceptual hash for the image at the given path.
    /// </summary>
    ulong Hash(string imagePath);

    /// <summary>
    /// Computes the perceptual hash for all 8 dihedral orientation variants
    /// (identity, horizontal flip, vertical flip, 180° rotation, and each of
    /// those composed with a 90° rotation) so flipped/rotated copies still match.
    /// </summary>
    ulong[] HashAllOrientations(string imagePath);
}
