using CorsiDuplicate.Core.Persons;

namespace CorsiDuplicate.Core.Models;

public sealed class MediaItem
{
    public required string FullPath { get; init; }
    public required string FileName { get; init; }
    public required MediaKind Kind { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastWriteUtc { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }
    public string? Codec { get; init; }
    public long? BitRateBps { get; init; }

    public TimeSpan? Duration { get; init; }
    public int? AudioSampleRateHz { get; init; }
    public long? AudioBitRateBps { get; init; }

    // Exact-match fast path (Phase 1)
    public required string Sha256 { get; init; }

    // Populated in later phases (orientation-invariant perceptual hashing, histograms, ORB, faces/bodies)
    public ulong[]? OrientationHashes { get; init; }
    public IReadOnlyList<ulong[]>? FrameOrientationHashes { get; init; }
    public float[]? HsvHistogram { get; init; }
    public byte[]? OrbDescriptors { get; init; }

    // Person recognition (Phase 7): face embeddings are the strong identity signal;
    // body embeddings are only consulted as a fallback when no face was detected.
    public IReadOnlyList<FaceDetection>? Faces { get; init; }
    public IReadOnlyList<PersonDetection>? Bodies { get; init; }

    // Populated at grouping time
    public double SimilarityToReferencePercent { get; set; }

    public string HashHex => Sha256.Length >= 16 ? Sha256[..16].ToUpperInvariant() : Sha256.ToUpperInvariant();
}
