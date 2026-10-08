using System.Diagnostics;
using CorsiDuplicate.Core.Matching;
using CorsiDuplicate.Infrastructure.Logging;
using FFMpegCore;

namespace CorsiDuplicate.Infrastructure.Media;

/// <summary>
/// Decodes a whole video at 1 frame per second, downscaled to 64×64 grayscale and streamed
/// from ffmpeg's stdout (no temp files). Each frame is cropped to its content (black bars and
/// a thin border trimmed), resampled to 32×32 and turned into a 64-bit DCT perceptual hash,
/// plus the hash of the same frame mirrored — the dense, time-ordered signature
/// <see cref="ContainmentMatcher"/> aligns.
/// </summary>
public static class VideoFingerprintExtractor
{
    private const int DecodeSide = 64;
    private const int DecodeBytes = DecodeSide * DecodeSide;
    private const int HashSide = 32;

    // Same idea as FfmpegFrameExtractor.MinFrameStdDev: near-solid frames hash to generic
    // patterns that would match any other video's blank moments.
    private const double MinFrameStdDev = 10.0;

    // An edge row/column darker than this on average is treated as a letterbox/pillarbox bar.
    private const double BarLuma = 24.0;

    // Trimmed from every side of the content box — edge watermarks, burned-in subtitles and
    // slight crops between re-posts mostly live there.
    private const double EdgeMargin = 0.08;

    private static readonly double[,] Cos = BuildCosTable();

    public static async Task<VideoFingerprint?> ExtractAsync(string path, CancellationToken ct = default)
    {
        await FfmpegBinaryProvisioner.EnsureAvailableAsync(ct);

        var stopwatch = Stopwatch.StartNew();
        // Fast path: skip decoding frames nothing else references (we keep 1 per second and
        // tolerate ±1 s anyway) and let ffmpeg use the GPU decoder when one is available.
        var result = await RunAsync(path, fastDecode: true, ct);
        if (result is null)
        {
            AppLogger.Info(nameof(VideoFingerprintExtractor), nameof(ExtractAsync),
                $"Fast decode produced no frames for '{Path.GetFileName(path)}'; retrying with a plain decode.");
            result = await RunAsync(path, fastDecode: false, ct);
        }

        AppLogger.Info(nameof(VideoFingerprintExtractor), nameof(ExtractAsync),
            $"Fingerprinted '{Path.GetFileName(path)}': {result?.Length ?? 0} s in {stopwatch.ElapsedMilliseconds} ms.");
        return result;
    }

    private static async Task<VideoFingerprint?> RunAsync(string path, bool fastDecode, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(GlobalFFOptions.Current.BinaryFolder, "ffmpeg.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (fastDecode)
        {
            args.AddRange(new[] { "-hwaccel", "auto", "-skip_frame", "noref", "-skip_loop_filter", "all" });
        }
        args.AddRange(new[]
        {
            "-i", path, "-an", "-sn", "-dn",
            "-vf", $"fps=1,scale={DecodeSide}:{DecodeSide}:flags=area,format=gray",
            "-f", "rawvideo", "pipe:1",
        });
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        // Drain stderr so a chatty ffmpeg can never block on a full pipe.
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });

        var hashes = new List<ulong>();
        var mirrored = new List<ulong>();
        var usable = new List<bool>();
        var frame = new byte[DecodeBytes];
        var stdout = process.StandardOutput.BaseStream;

        while (true)
        {
            var read = 0;
            while (read < DecodeBytes)
            {
                var n = await stdout.ReadAsync(frame.AsMemory(read, DecodeBytes - read), ct);
                if (n == 0)
                {
                    break;
                }
                read += n;
            }

            if (read < DecodeBytes)
            {
                break;
            }

            var content = CropToContent(frame);
            usable.Add(StdDev(content) >= MinFrameStdDev);
            hashes.Add(PerceptualHash(content));
            mirrored.Add(PerceptualHash(Mirror(content)));
        }

        await process.WaitForExitAsync(ct);
        await stderrTask;

        if (hashes.Count == 0)
        {
            return null;
        }
        return new VideoFingerprint(hashes.ToArray(), usable.ToArray(), mirrored.ToArray());
    }

    /// <summary>Finds the frame's content box (dark edge rows/columns are bars), trims
    /// <see cref="EdgeMargin"/> from each side and resamples it to 32×32.</summary>
    private static double[] CropToContent(byte[] frame)
    {
        double RowMean(int y) { double s = 0; for (var x = 0; x < DecodeSide; x++) s += frame[y * DecodeSide + x]; return s / DecodeSide; }
        double ColMean(int x) { double s = 0; for (var y = 0; y < DecodeSide; y++) s += frame[y * DecodeSide + x]; return s / DecodeSide; }

        int top = 0, bottom = DecodeSide - 1, left = 0, right = DecodeSide - 1;
        const int maxTrim = DecodeSide / 4;
        while (top < maxTrim && RowMean(top) < BarLuma) top++;
        while (bottom > DecodeSide - 1 - maxTrim && RowMean(bottom) < BarLuma) bottom--;
        while (left < maxTrim && ColMean(left) < BarLuma) left++;
        while (right > DecodeSide - 1 - maxTrim && ColMean(right) < BarLuma) right--;

        double width = right - left + 1, height = bottom - top + 1;
        double x0 = left + width * EdgeMargin, y0 = top + height * EdgeMargin;
        double w = width * (1 - 2 * EdgeMargin), h = height * (1 - 2 * EdgeMargin);

        var output = new double[HashSide * HashSide];
        for (var y = 0; y < HashSide; y++)
        {
            for (var x = 0; x < HashSide; x++)
            {
                output[y * HashSide + x] = Bilinear(frame, x0 + (x + 0.5) * w / HashSide - 0.5, y0 + (y + 0.5) * h / HashSide - 0.5);
            }
        }
        return output;
    }

    private static double Bilinear(byte[] frame, double fx, double fy)
    {
        fx = Math.Clamp(fx, 0, DecodeSide - 1);
        fy = Math.Clamp(fy, 0, DecodeSide - 1);
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, DecodeSide - 1), y1 = Math.Min(y0 + 1, DecodeSide - 1);
        double tx = fx - x0, ty = fy - y0;
        double top = frame[y0 * DecodeSide + x0] * (1 - tx) + frame[y0 * DecodeSide + x1] * tx;
        double bottom = frame[y1 * DecodeSide + x0] * (1 - tx) + frame[y1 * DecodeSide + x1] * tx;
        return top * (1 - ty) + bottom * ty;
    }

    private static double[] Mirror(double[] pixels)
    {
        var flipped = new double[pixels.Length];
        for (var y = 0; y < HashSide; y++)
        {
            for (var x = 0; x < HashSide; x++)
            {
                flipped[y * HashSide + x] = pixels[y * HashSide + (HashSide - 1 - x)];
            }
        }
        return flipped;
    }

    private static double StdDev(double[] pixels)
    {
        double sum = 0, sumSq = 0;
        foreach (var p in pixels)
        {
            sum += p;
            sumSq += p * p;
        }
        var mean = sum / pixels.Length;
        return Math.Sqrt(Math.Max(0, sumSq / pixels.Length - mean * mean));
    }

    /// <summary>Classic pHash: 2D DCT of the 32×32 frame, keep the 8×8 lowest frequencies,
    /// each bit = coefficient above the median of those 64 (DC term excluded from the median).</summary>
    private static ulong PerceptualHash(double[] pixels)
    {
        var low = new double[64];
        for (var u = 0; u < 8; u++)
        {
            for (var v = 0; v < 8; v++)
            {
                double sum = 0;
                for (var y = 0; y < HashSide; y++)
                {
                    var rowCos = Cos[u, y];
                    var rowOffset = y * HashSide;
                    for (var x = 0; x < HashSide; x++)
                    {
                        sum += pixels[rowOffset + x] * rowCos * Cos[v, x];
                    }
                }
                low[u * 8 + v] = sum;
            }
        }

        var median = low.Skip(1).OrderBy(c => c).ElementAt(31);
        ulong hash = 0;
        for (var i = 0; i < 64; i++)
        {
            if (low[i] > median)
            {
                hash |= 1UL << i;
            }
        }
        return hash;
    }

    private static double[,] BuildCosTable()
    {
        var table = new double[8, HashSide];
        for (var u = 0; u < 8; u++)
        {
            for (var x = 0; x < HashSide; x++)
            {
                table[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / (2 * HashSide));
            }
        }
        return table;
    }
}
