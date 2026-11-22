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

    // When the app was last opened (UTC), updated at every launch; the "changed since last open"
    // folder selection compares folder changes against the value read BEFORE this launch.
    public DateTime? LastOpenUtc { get; set; }

    // Folder manager: on launch, tick only the folders changed since the previous launch.
    public bool AutoSelectChangedOnOpen { get; set; }
}
