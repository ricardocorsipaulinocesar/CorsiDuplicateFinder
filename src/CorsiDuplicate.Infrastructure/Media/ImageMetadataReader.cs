using CorsiDuplicate.Core.Hashing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CorsiDuplicate.Infrastructure.Media;

public sealed record PhotoSignals(int Width, int Height, ulong[] OrientationHashes);

/// <summary>
/// Loads a photo once and extracts both its dimensions and its 8-orientation perceptual
/// hash set from that single decode, instead of decoding the file twice.
/// </summary>
public sealed class ImageMetadataReader
{
    private readonly OrientationInvariantHasher _hasher = new();

    public PhotoSignals? TryRead(string path)
    {
        try
        {
            using var image = Image.Load<Rgba32>(path);
            var hashes = _hasher.HashAllOrientations(image);
            return new PhotoSignals(image.Width, image.Height, hashes);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
