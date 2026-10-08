namespace CorsiDuplicate.Infrastructure.Persistence;

/// <summary>General small app preferences that should survive across launches.</summary>
public sealed class AppSettings
{
    public int ThumbnailsPerVideo { get; set; } = 1;
    public string FolderSortKey { get; set; } = "LastModified";
    public bool FolderSortDescending { get; set; } = true;
    // Legacy (before ScanMode existed): true meant today's "VideosContained".
    public bool ScanVideosOnly { get; set; }

    // "All", "Videos" or "VideosContained"; null until first saved.
    public string? ScanMode { get; set; }
}
