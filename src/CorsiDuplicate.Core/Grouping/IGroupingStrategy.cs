using CorsiDuplicate.Core.Models;

namespace CorsiDuplicate.Core.Grouping;

public interface IGroupingStrategy
{
    /// <summary>
    /// Groups items within a single folder's item set. Pure and folder-agnostic:
    /// the "no cross-folder matching" constraint is enforced by callers invoking
    /// this once per folder, not by this method itself.
    /// </summary>
    List<DuplicateGroup> Group(IReadOnlyList<MediaItem> items, double threshold);
}
