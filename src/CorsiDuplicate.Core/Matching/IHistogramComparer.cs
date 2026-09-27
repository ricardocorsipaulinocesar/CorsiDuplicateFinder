namespace CorsiDuplicate.Core.Matching;

/// <summary>
/// Color histogram signal: catches same-photo-different-exposure/filter/color-grade
/// edits that hash-based methods can miss, since it ignores spatial layout entirely.
/// </summary>
public interface IHistogramComparer
{
    float[] ComputeHistogram(string imagePath);

    /// <summary>Returns a similarity in [0, 1], where 1 means identical color distribution.</summary>
    double Compare(float[] a, float[] b);
}
