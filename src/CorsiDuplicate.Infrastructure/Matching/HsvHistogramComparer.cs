using CorsiDuplicate.Core.Matching;
using OpenCvSharp;

namespace CorsiDuplicate.Infrastructure.Matching;

/// <summary>
/// HSV color histogram (30 hue bins x 32 saturation bins, normalized) compared via
/// OpenCV's histogram correlation — robust to brightness/contrast/color-grading edits
/// since it ignores spatial layout entirely, unlike a perceptual hash.
/// </summary>
public sealed class HsvHistogramComparer : IHistogramComparer
{
    private const int HueBins = 30;
    private const int SaturationBins = 32;

    public float[] ComputeHistogram(string imagePath)
    {
        using var bgr = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (bgr.Empty())
        {
            return Array.Empty<float>();
        }

        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        using var hist = new Mat();
        Cv2.CalcHist(
            new[] { hsv },
            new[] { 0, 1 },
            null,
            hist,
            2,
            new[] { HueBins, SaturationBins },
            new[] { new Rangef(0, 180), new Rangef(0, 256) });

        Cv2.Normalize(hist, hist, 0, 1, NormTypes.MinMax);

        var result = new float[HueBins * SaturationBins];
        var idx = 0;
        for (var h = 0; h < HueBins; h++)
        {
            for (var s = 0; s < SaturationBins; s++)
            {
                result[idx++] = hist.At<float>(h, s);
            }
        }
        return result;
    }

    public double Compare(float[] a, float[] b)
    {
        if (a.Length != HueBins * SaturationBins || b.Length != HueBins * SaturationBins)
        {
            return 0.0;
        }

        using var ma = ToMat(a);
        using var mb = ToMat(b);
        var correlation = Cv2.CompareHist(ma, mb, HistCompMethods.Correl);
        // Correlation is in [-1, 1]; clamp negative (anti-correlated) to 0 similarity.
        return Math.Max(0.0, correlation);
    }

    private static Mat ToMat(float[] flatHistogram)
    {
        var mat = new Mat(HueBins, SaturationBins, MatType.CV_32F);
        var idx = 0;
        for (var h = 0; h < HueBins; h++)
        {
            for (var s = 0; s < SaturationBins; s++)
            {
                mat.Set(h, s, flatHistogram[idx++]);
            }
        }
        return mat;
    }
}
