namespace CorsiDuplicate.Core.Models;

public sealed class MediaFolder
{
    public required string Path { get; init; }
    public List<DuplicateGroup> Groups { get; } = new();
}
