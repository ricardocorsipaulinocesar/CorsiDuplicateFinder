namespace CorsiDuplicate.Core.Persons;

/// <summary>
/// A per-folder library of recognized face/body embedding clusters that grows across
/// repeated scans of the same folder: a close match reuses an existing cluster (and
/// refines its centroid), a non-match creates a new one. This is the concrete
/// mechanism behind "learn by folder, get more intelligent with time" — later scans
/// recognize recurring people faster and more accurately as the library accumulates.
/// </summary>
public interface IPersonClusterStore
{
    /// <summary>Finds or creates the cluster this embedding belongs to, updating that
    /// cluster's centroid, and returns its id.</summary>
    Guid MatchOrCreateCluster(string folderPath, float[] embedding, double similarityThreshold = 0.6);
}
