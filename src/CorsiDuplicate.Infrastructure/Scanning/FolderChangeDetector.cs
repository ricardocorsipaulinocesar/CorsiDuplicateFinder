namespace CorsiDuplicate.Infrastructure.Scanning;

public static class FolderChangeDetector
{
    /// <summary>Latest write time of the folder itself or anything inside it, recursively.
    /// Directory write times are included because deleting or renaming a file only touches
    /// its parent directory's timestamp, not any surviving file's. Null if the folder is gone.</summary>
    public static DateTime? GetLastChangeUtc(string path, CancellationToken ct = default)
    {
        var root = new DirectoryInfo(path);
        if (!root.Exists)
        {
            return null;
        }

        var latest = root.LastWriteTimeUtc;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var entry in root.EnumerateFileSystemInfos("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.LastWriteTimeUtc > latest)
            {
                latest = entry.LastWriteTimeUtc;
            }
        }

        return latest;
    }
}
