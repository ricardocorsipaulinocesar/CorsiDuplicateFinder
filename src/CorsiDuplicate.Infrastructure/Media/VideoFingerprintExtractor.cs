using System.Diagnostics;
using CorsiDuplicate.Core.Matching;
using FFMpegCore;

namespace CorsiDuplicate.Infrastructure.Media;

/// <summary>
/// Decodes a whole video at 1 frame per second, downscaled to 32×32 grayscale and streamed
/// from ffmpeg's stdout (no temp files), and turns each frame into a 64-bit DCT perceptual
/// hash — the dense, time-ordered signature <see cref="ContainmentMatcher"/> aligns.
/// </summary>
public static class VideoFingerprintExtractor
{
    private const int Side = 32;
    private const int FrameBytes = Side * Side;

    // Same idea as FfmpegFrameExtractor.MinFrameStdDev: near-solid frames hash to generic
    // patterns that would match any other video's blank moments.
    private const double MinFrameStdDev = 10.0;

    private static readonly double[,] Cos = BuildCosTable();

    public static async Task<VideoFingerprint?> ExtractAsync(string path, CancellationToken ct = default)
    {
        await FfmpegBinaryProvisioner.EnsureAvailableAsync(ct);

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(GlobalFFOptions.Current.BinaryFolder, "ffmpeg.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin",
                     "-i", path, "-an", "-sn", "-dn",
                     "-vf", $"fps=1,scale={Side}:{Side}:flags=area,format=gray",
                     "-f", "rawvideo", "pipe:1",
                 })
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
        var usable = new List<bool>();
        var buffer = new byte[FrameBytes];
        var stdout = process.StandardOutput.BaseStream;

        while (true)
        {
            var read = 0;
            while (read < FrameBytes)
            {
                var n = await stdout.ReadAsync(buffer.AsMemory(read, FrameBytes - read), ct);
                if (n == 0)
                {
                    break;
                }
                read += n;
            }

            if (read < FrameBytes)
            {
                break;
            }

            usable.Add(StdDev(buffer) >= MinFrameStdDev);
            hashes.Add(PerceptualHash(buffer));
        }

        await process.WaitForExitAsync(ct);
        await stderrTask;

        return hashes.Count > 0 ? new VideoFingerprint(hashes.ToArray(), usable.ToArray()) : null;
    }

    private static double StdDev(byte[] pixels)
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
    private static ulong PerceptualHash(byte[] pixels)
    {
        var low = new double[64];
        for (var u = 0; u < 8; u++)
        {
            for (var v = 0; v < 8; v++)
            {
                double sum = 0;
                for (var x = 0; x < Side; x++)
                {
                    var rowCos = Cos[u, x];
                    var rowOffset = x * Side;
                    for (var y = 0; y < Side; y++)
                    {
                        sum += pixels[rowOffset + y] * rowCos * Cos[v, y];
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
        var table = new double[8, Side];
        for (var u = 0; u < 8; u++)
        {
            for (var x = 0; x < Side; x++)
            {
                table[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / (2 * Side));
            }
        }
        return table;
    }
}
