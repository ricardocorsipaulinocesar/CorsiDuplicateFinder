namespace CorsiDuplicate.Core.Persons;

/// <summary>A detected face, with its identity embedding and (once matched) which
/// per-folder person cluster it belongs to.</summary>
public sealed class FaceDetection
{
    public required float[] Embedding { get; init; }
    public Guid? MatchedPersonClusterId { get; set; }
}

/// <summary>A detected full body/person region, used only as a fallback signal when
/// no face was detected in one or both items being compared — coarser and far less
/// discriminating between different people than a face embedding.</summary>
public sealed class PersonDetection
{
    public required float[] Embedding { get; init; }
    public Guid? MatchedPersonClusterId { get; set; }
}
