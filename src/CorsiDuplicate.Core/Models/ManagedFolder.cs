namespace CorsiDuplicate.Core.Models;

public sealed class ManagedFolder
{
    public required string Path { get; init; }
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastScannedUtc { get; set; }
    // Most recent change anywhere inside the folder (cached between launches, refreshed
    // in the background when the managed-folders panel opens and after each scan).
    public DateTime? LastModifiedUtc { get; set; }
    public int ItemCount { get; set; }
}
