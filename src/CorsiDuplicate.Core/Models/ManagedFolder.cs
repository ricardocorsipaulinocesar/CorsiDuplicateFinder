namespace CorsiDuplicate.Core.Models;

public sealed class ManagedFolder
{
    public required string Path { get; init; }
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastScannedUtc { get; set; }
    public int ItemCount { get; set; }
}
