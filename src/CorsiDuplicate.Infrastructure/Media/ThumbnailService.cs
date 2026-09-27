using CorsiDuplicate.Core.Models;
using CorsiDuplicate.Infrastructure.Scanning;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CorsiDuplicate.Infrastructure.Media;

/// <summary>
/// Generates and caches a small (max 256px side) JPEG thumbnail per scanned file under
/// %LocalAppData%\CorsiDuplicate\thumbs\, keyed by content (path+size+lastWriteUtc) so
/// unchanged files never regenerate their thumbnail on a later scan.
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

    public string PathFor(MediaItem item)
    {
        var key = ScanCacheStore.CacheKey(item.FullPath, item.SizeBytes, item.LastWriteUtc);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_thumbDirectory, hash + ".jpg");
    }

    /// <summary>Generates the thumbnail if it doesn't already exist. Returns the cache path, or null on failure.</summary>
    public async Task<string?> EnsureThumbnailAsync(MediaItem item, CancellationToken ct = default)
    {
        var outPath = PathFor(item);
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

            // Video: grab one frame partway through, then downscale the same way as a photo.
            if (item.Duration is { } duration && duration > TimeSpan.Zero)
            {
                await FfmpegBinaryProvisioner.EnsureAvailableAsync(ct);
                var framePath = Path.Combine(Path.GetTempPath(), $"cd_thumb_{Guid.NewGuid()}.jpg");
                try
                {
                    await FFMpegCore.FFMpegArguments
                        .FromFileInput(item.FullPath, verifyExists: false, opt => opt.Seek(TimeSpan.FromSeconds(duration.TotalSeconds * 0.2)))
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
}
