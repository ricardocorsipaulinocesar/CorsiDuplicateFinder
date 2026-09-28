using System.Security.Cryptography;
using System.Text;
using CorsiDuplicate.Core.Models;
using CorsiDuplicate.Infrastructure.Scanning;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CorsiDuplicate.Infrastructure.Media;

/// <summary>
/// Generates and caches small (max 256px side) JPEG thumbnails per scanned file under
/// %LocalAppData%\CorsiDuplicate\thumbs\, keyed by content (path+size+lastWriteUtc) so
/// unchanged files never regenerate their thumbnails on a later scan. A photo always
/// gets exactly one thumbnail; a video gets a user-configurable count, sampled at evenly
/// spaced fractions of its duration — 1 thumbnail is always the very start of the video,
/// 2+ always include both the start and the end, with any remaining ones spread evenly
/// between — the same fractions for every video, so equivalent scenes line up across
/// results and are easy to compare at a glance.
/// </summary>
public sealed class ThumbnailService
{
    private const int MaxSide = 256;
    private readonly string _thumbDirectory;

    public ThumbnailService()
    {
        _thumbDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CorsiDuplicate", "thumbs");
        Directory.CreateDirectory(_thumbDirectory);
    }

    /// <summary>The single canonical thumbnail path for an item — stable regardless of
    /// the user's display thumbnail count, used as the fixed identity for signals like
    /// the "sort by thumbnail similarity" feature.</summary>
    public string PathFor(MediaItem item) => PathForFrame(item, index: 0, count: 1);

    private string PathForFrame(MediaItem item, int index, int count)
    {
        var key = ScanCacheStore.CacheKey(item.FullPath, item.SizeBytes, item.LastWriteUtc) + $"|{count}|{index}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_thumbDirectory, hash + ".jpg");
    }

    /// <summary>Generates the single canonical thumbnail if it doesn't already exist. Returns the cache path, or null on failure.</summary>
    public Task<string?> EnsureThumbnailAsync(MediaItem item, CancellationToken ct = default) =>
        EnsureFrameAsync(item, index: 0, count: 1, ct);

    /// <summary>
    /// Generates the requested number of display thumbnails for an item (photos always
    /// return exactly one, ignoring <paramref name="count"/>) and returns whichever paths
    /// were produced successfully, in order.
    /// </summary>
    public async Task<IReadOnlyList<string>> EnsureThumbnailsAsync(MediaItem item, int count, CancellationToken ct = default)
    {
        if (item.Kind == MediaKind.Photo)
        {
            var single = await EnsureFrameAsync(item, index: 0, count: 1, ct);
            return single is null ? Array.Empty<string>() : new[] { single };
        }

        var frameCount = Math.Max(1, count);
        var paths = new List<string>(frameCount);
        for (var i = 0; i < frameCount; i++)
        {
            var path = await EnsureFrameAsync(item, i, frameCount, ct);
            if (path is not null)
            {
                paths.Add(path);
            }
        }
        return paths;
    }

    private async Task<string?> EnsureFrameAsync(MediaItem item, int index, int count, CancellationToken ct)
    {
        var outPath = PathForFrame(item, index, count);
        if (File.Exists(outPath))
        {
            return outPath;
        }

        try
        {
            if (item.Kind == MediaKind.Photo)
            {
                using var image = await Image.LoadAsync<Rgba32>(item.FullPath, ct);
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(MaxSide, MaxSide)
                }));
                await image.SaveAsync(outPath, new JpegEncoder { Quality = 80 }, ct);
                return outPath;
            }

            if (item.Duration is { } duration && duration > TimeSpan.Zero)
            {
                await FfmpegBinaryProvisioner.EnsureAvailableAsync(ct);
                var fraction = FractionFor(index, count);
                var framePath = Path.Combine(Path.GetTempPath(), $"cd_thumb_{Guid.NewGuid()}.jpg");
                try
                {
                    await FFMpegCore.FFMpegArguments
                        .FromFileInput(item.FullPath, verifyExists: false, opt => opt.Seek(TimeSpan.FromSeconds(duration.TotalSeconds * fraction)))
                        .OutputToFile(framePath, overwrite: true, opt => opt.WithFrameOutputCount(1))
                        .CancellableThrough(ct)
                        .ProcessAsynchronously();

                    if (!File.Exists(framePath))
                    {
                        return null;
                    }

                    using var frame = await Image.LoadAsync<Rgba32>(framePath, ct);
                    frame.Mutate(ctx => ctx.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(MaxSide, MaxSide)
                    }));
                    await frame.SaveAsync(outPath, new JpegEncoder { Quality = 80 }, ct);
                    return outPath;
                }
                finally
                {
                    try { File.Delete(framePath); } catch (IOException) { }
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Kept just off the literal 0%/100% marks — seeking to the exact first/last frame
    // often lands on a black fade or title-card frame, which isn't a representative
    // "start"/"end" thumbnail.
    private const double EdgeInset = 0.02;

    /// <summary>
    /// 1 thumbnail: always the start. 2+: evenly spaced from start to end inclusive, so
    /// every video's Nth thumbnail lands at the same proportional point, no matter how
    /// long the video is — the same fractions for every result, to make comparing
    /// equivalent scenes across videos straightforward.
    /// </summary>
    private static double FractionFor(int index, int count)
    {
        if (count <= 1)
        {
            return EdgeInset;
        }

        var span = 1.0 - 2 * EdgeInset;
        return EdgeInset + span * index / (count - 1);
    }
}
