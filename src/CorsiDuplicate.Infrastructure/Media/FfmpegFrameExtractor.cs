using CorsiDuplicate.Core.Hashing;
using CorsiDuplicate.Infrastructure.Logging;
using CorsiDuplicate.Infrastructure.Matching;
using FFMpegCore;
using OpenCvSharp;

namespace CorsiDuplicate.Infrastructure.Media;

public sealed record VideoSignals(IReadOnlyList<ulong[]> FrameOrientationHashes, float[]? RepresentativeHistogram);

/// <summary>
/// Samples a handful of evenly-spaced keyframes from a video and runs each one through
/// the same orientation-invariant perceptual hashing used for photos, so a flipped,
/// rotated, cropped, or re-encoded copy of a video still matches. A trimmed video still
/// shares most of its sampled frames' content with the original, since duration is
/// intentionally never used to gate the match — only as a soft, non-blocking signal.
/// </summary>
public sealed class FfmpegFrameExtractor
{
    private const int SampleCount = 5;

    // A near-solid-color frame (a black frame from a fade, a blank title card, or one
    // FFmpeg's fast seek occasionally lands on slightly off-target) carries almost no
    // information, so its perceptual hash collapses to a generic, low-detail pattern
    // that coincidentally matches other videos' equally blank frames — this was the
    // main remaining source of two unrelated videos scoring as near-duplicates even at
    // a 96% threshold, since it isn't about one lucky coincidence but entire frames
    // that any video's blank moment would hash almost the same way. Grayscale pixel
    // standard deviation below this is treated as "no usable content".
    private const double MinFrameStdDev = 10.0;

    private readonly OrientationInvariantHasher _hasher = new();

    public async Task<VideoSignals?> ExtractAsync(string path, TimeSpan duration, CancellationToken ct = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            return null;
        }

        await FfmpegBinaryProvisioner.EnsureAvailableAsync(ct);

        var tempDir = Path.Combine(Path.GetTempPath(), "CorsiDuplicate_frames_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var frameHashes = new List<ulong[]>();
            float[]? representativeHistogram = null;

            for (var i = 0; i < SampleCount; i++)
            {
                // Evenly spaced samples, inset from the very start/end where title
                // cards or fades often make the frame unrepresentative.
                var fraction = (i + 1) / (double)(SampleCount + 1);
                var timestamp = TimeSpan.FromSeconds(duration.TotalSeconds * fraction);
                var framePath = Path.Combine(tempDir, $"frame_{i}.jpg");

                var ok = await TryExtractUsableFrameAsync(path, timestamp, framePath, duration, ct);
                if (!ok || !File.Exists(framePath))
                {
                    continue;
                }

                var hashes = _hasher.HashAllOrientations(framePath);
                frameHashes.Add(hashes);

                if (i == SampleCount / 2)
                {
                    representativeHistogram = TryComputeHistogram(framePath);
                }
            }

            return frameHashes.Count > 0 ? new VideoSignals(frameHashes, representativeHistogram) : null;
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a locked temp file here isn't worth failing the scan over.
            }
        }
    }

    /// <summary>
    /// Extracts a frame at <paramref name="baseTimestamp"/>, and if it turns out to be
    /// near-solid-color (a fade, a blank card, an off-target seek), retries at a few
    /// small offsets around it looking for one with real content. If every attempt is
    /// low-variance (a genuinely flat/solid-color video isn't a hypothetical — those
    /// exist too), the least-flat one found is used rather than giving up on this
    /// sample slot entirely: preferring a textured frame fixes the false-positive case
    /// without ever leaving a video with zero usable signal.
    /// </summary>
    private static async Task<bool> TryExtractUsableFrameAsync(
        string inputPath, TimeSpan baseTimestamp, string outputPath, TimeSpan duration, CancellationToken ct)
    {
        string? bestPath = null;
        var bestStdDev = -1.0;

        foreach (var offsetSeconds in new[] { 0.0, 0.5, 1.0, -0.5, -1.0 })
        {
            var timestamp = baseTimestamp + TimeSpan.FromSeconds(offsetSeconds);
            if (timestamp < TimeSpan.Zero || timestamp > duration)
            {
                continue;
            }

            var candidatePath = outputPath + $".{offsetSeconds}.candidate.jpg";
            if (!await TryExtractFrameAsync(inputPath, timestamp, candidatePath, ct) || !File.Exists(candidatePath))
            {
                continue;
            }

            var stdDev = FrameStdDev(candidatePath);
            if (stdDev > bestStdDev)
            {
                if (bestPath is not null)
                {
                    TryDeleteFile(bestPath);
                }
                bestStdDev = stdDev;
                bestPath = candidatePath;
            }
            else
            {
                TryDeleteFile(candidatePath);
            }

            if (stdDev >= MinFrameStdDev)
            {
                break;
            }
        }

        if (bestPath is null)
        {
            return false;
        }

        File.Move(bestPath, outputPath, overwrite: true);
        return true;
    }

    private static double FrameStdDev(string framePath)
    {
        try
        {
            using var gray = Cv2.ImRead(framePath, ImreadModes.Grayscale);
            if (gray.Empty())
            {
                return 0.0;
            }

            Cv2.MeanStdDev(gray, out _, out var stdDev);
            return stdDev.Val0;
        }
        catch (Exception)
        {
            // Unreadable — treat the same as an empty frame so it's never preferred
            // over an actually-decodable candidate.
            return 0.0;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<bool> TryExtractFrameAsync(string inputPath, TimeSpan timestamp, string outputPath, CancellationToken ct)
    {
        try
        {
            await FFMpegArguments
                .FromFileInput(inputPath, verifyExists: false, options => options.Seek(timestamp))
                .OutputToFile(outputPath, overwrite: true, options => options
                    .WithFrameOutputCount(1)
                    .WithCustomArgument("-q:v 3"))
                .CancellableThrough(ct)
                .ProcessAsynchronously();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warning(nameof(FfmpegFrameExtractor), nameof(TryExtractFrameAsync),
                $"Frame extraction failed for '{inputPath}' at {timestamp}: {ex.Message}");
            return false;
        }
    }

    private static float[]? TryComputeHistogram(string framePath)
    {
        try
        {
            return new HsvHistogramComparer().ComputeHistogram(framePath);
        }
        catch (Exception ex)
        {
            AppLogger.Warning(nameof(FfmpegFrameExtractor), nameof(TryComputeHistogram),
                $"Histogram computation failed for frame '{framePath}': {ex.Message}");
            return null;
        }
    }
}
