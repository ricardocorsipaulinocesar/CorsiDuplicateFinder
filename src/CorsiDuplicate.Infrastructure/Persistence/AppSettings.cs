namespace CorsiDuplicate.Infrastructure.Persistence;

/// <summary>General small app preferences that should survive across launches.</summary>
public sealed class AppSettings
{
    public int ThumbnailsPerVideo { get; set; } = 1;
}
