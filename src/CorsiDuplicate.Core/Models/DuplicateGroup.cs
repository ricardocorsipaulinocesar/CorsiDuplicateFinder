namespace CorsiDuplicate.Core.Models;

public sealed class DuplicateGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public MediaItem? Reference { get; set; }
    public List<MediaItem> Items { get; } = new();
}
