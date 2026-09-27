using CorsiDuplicate.Core.Models;

namespace CorsiDuplicate.Infrastructure.Scanning;

public sealed class FolderScanner
{
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".heic", ".heif"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".webm", ".flv"
    };

    public IEnumerable<(string Path, MediaKind Kind)> EnumerateMediaFiles(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };

        foreach (var path in Directory.EnumerateFiles(rootPath, "*", enumOptions))
        {
            var ext = System.IO.Path.GetExtension(path);
            if (PhotoExtensions.Contains(ext))
            {
                yield return (path, MediaKind.Photo);
            }
            else if (VideoExtensions.Contains(ext))
            {
                yield return (path, MediaKind.Video);
            }
        }
    }
}
