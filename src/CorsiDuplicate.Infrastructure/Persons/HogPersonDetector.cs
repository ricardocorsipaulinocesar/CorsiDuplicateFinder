using CorsiDuplicate.Core.Persons;
using CorsiDuplicate.Infrastructure.Matching;
using OpenCvSharp;

namespace CorsiDuplicate.Infrastructure.Persons;

/// <summary>
/// Full-body detection fallback for when no face is visible (back turned, far away,
/// low light). Uses OpenCV's built-in HOG + default people-detector SVM — no model
/// download required, unlike the face pipeline. The "embedding" here is a coarse HSV
/// histogram of the detected region (same technique as HsvHistogramComparer, just
/// applied to a person crop instead of a whole photo): a same-clothing/build signal,
/// deliberately much weaker and less discriminating between different people than a
/// face embedding, so callers should only consult it when no face was found.
/// </summary>
public sealed class HogPersonDetector : IPersonDetector
{
    private readonly HOGDescriptor _hog = new();
    private readonly HsvHistogramComparer _histogram = new();
    private readonly object _hogLock = new();

    public HogPersonDetector()
    {
        _hog.SetSVMDetector(HOGDescriptor.GetDefaultPeopleDetector());
    }

    public IReadOnlyList<PersonDetection> DetectPersons(string imagePath)
    {
        using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (image.Empty())
        {
            return Array.Empty<PersonDetection>();
        }

        Rect[] rects;
        try
        {
            // HOGDescriptor isn't documented as thread-safe for concurrent DetectMultiScale
            // calls, and ScanPipeline runs many files in parallel, so this is serialized.
            lock (_hogLock)
            {
                rects = _hog.DetectMultiScale(image, winStride: new Size(8, 8), padding: new Size(8, 8), scale: 1.05);
            }
        }
        catch (OpenCVException)
        {
            return Array.Empty<PersonDetection>();
        }

        if (rects.Length == 0)
        {
            return Array.Empty<PersonDetection>();
        }

        var results = new List<PersonDetection>();
        foreach (var rect in rects)
        {
            var bounded = rect.Intersect(new Rect(0, 0, image.Width, image.Height));
            if (bounded.Width <= 0 || bounded.Height <= 0)
            {
                continue;
            }

            using var crop = new Mat(image, bounded);
            var embedding = ComputeHistogramEmbedding(crop);
            if (embedding is not null)
            {
                results.Add(new PersonDetection { Embedding = embedding });
            }
        }

        return results;
    }

    private float[]? ComputeHistogramEmbedding(Mat crop)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"cd_person_{Guid.NewGuid()}.jpg");
        try
        {
            Cv2.ImWrite(tempPath, crop);
            var hist = _histogram.ComputeHistogram(tempPath);
            return hist.Length > 0 ? hist : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try { File.Delete(tempPath); } catch (IOException) { }
        }
    }
}
