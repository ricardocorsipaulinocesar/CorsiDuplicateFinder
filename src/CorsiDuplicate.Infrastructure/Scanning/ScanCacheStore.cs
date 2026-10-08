using System.Text.Json;
using CorsiDuplicate.Core.Models;

namespace CorsiDuplicate.Infrastructure.Scanning;

/// <summary>
/// Persists extracted MediaItem signals (hashes, metadata) per scanned folder, keyed by
/// (path, size, lastWriteUtc). A cache hit skips re-extraction entirely, which is what
/// lets the similarity slider re-group in-memory without ever touching disk/FFmpeg again.
/// </summary>
public sealed class ScanCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    // Bump this whenever an extraction-signal change (a hashing/frame-sampling fix)
    // must invalidate every previously cached entry — old cache files are simply
    // never looked up again under the new suffix, so every file re-extracts once
    // with the fixed logic, with no manual cache-clearing required.
    private const string SchemaVersion = "v2";

    private readonly string _cacheDirectory;

    public ScanCacheStore()
    {
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CorsiDuplicate", "cache");
        Directory.CreateDirectory(_cacheDirectory);
    }

    private string CacheFilePath(string folderPath)
    {
        var folderHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(folderPath.ToLowerInvariant())));
        return Path.Combine(_cacheDirectory, folderHash + "." + SchemaVersion + ".json");
    }

    public Dictionary<string, CachedMediaItem> Load(string folderPath)
    {
        var file = CacheFilePath(folderPath);
        if (!File.Exists(file))
        {
            return new Dictionary<string, CachedMediaItem>();
        }

        try
        {
            using var stream = File.OpenRead(file);
            var entries = JsonSerializer.Deserialize<List<CachedMediaItem>>(stream, JsonOptions) ?? new();
            return entries.ToDictionary(e => CacheKey(e.FullPath, e.SizeBytes, e.LastWriteUtc));
        }
        catch (JsonException)
        {
            return new Dictionary<string, CachedMediaItem>();
        }
    }

    public void Save(string folderPath, IEnumerable<MediaItem> items, IEnumerable<CachedMediaItem>? alsoKeep = null)
    {
        var entries = items.Select(CachedMediaItem.FromMediaItem).ToList();
        if (alsoKeep is not null)
        {
            entries.AddRange(alsoKeep);
        }
        var file = CacheFilePath(folderPath);
        using var stream = File.Create(file);
        JsonSerializer.Serialize(stream, entries, JsonOptions);
    }

    public static string CacheKey(string path, long size, DateTime lastWriteUtc) =>
        $"{path.ToLowerInvariant()}|{size}|{lastWriteUtc.Ticks}";
}

public sealed class CachedMediaItem
{
    public required string FullPath { get; init; }
    public required string FileName { get; init; }
    public required MediaKind Kind { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastWriteUtc { get; init; }
    public required string Sha256 { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public ulong[]? OrientationHashes { get; init; }
    public float[]? HsvHistogram { get; init; }
    public string? Codec { get; init; }
    public long? BitRateBps { get; init; }
    public long? DurationTicks { get; init; }
    public int? AudioSampleRateHz { get; init; }
    public long? AudioBitRateBps { get; init; }
    public List<ulong[]>? FrameOrientationHashes { get; init; }
    public List<float[]>? FaceEmbeddings { get; init; }
    public List<float[]>? BodyEmbeddings { get; init; }

    public MediaItem ToMediaItem() => new()
    {
        FullPath = FullPath,
        FileName = FileName,
        Kind = Kind,
        SizeBytes = SizeBytes,
        LastWriteUtc = LastWriteUtc,
        Sha256 = Sha256,
        Width = Width,
        Height = Height,
        OrientationHashes = OrientationHashes,
        HsvHistogram = HsvHistogram,
        Codec = Codec,
        BitRateBps = BitRateBps,
        Duration = DurationTicks is { } t ? TimeSpan.FromTicks(t) : null,
        AudioSampleRateHz = AudioSampleRateHz,
        AudioBitRateBps = AudioBitRateBps,
        FrameOrientationHashes = FrameOrientationHashes,
        Faces = FaceEmbeddings?.Select(e => new CorsiDuplicate.Core.Persons.FaceDetection { Embedding = e }).ToList(),
        Bodies = BodyEmbeddings?.Select(e => new CorsiDuplicate.Core.Persons.PersonDetection { Embedding = e }).ToList()
    };

    public static CachedMediaItem FromMediaItem(MediaItem item) => new()
    {
        FullPath = item.FullPath,
        FileName = item.FileName,
        Kind = item.Kind,
        SizeBytes = item.SizeBytes,
        LastWriteUtc = item.LastWriteUtc,
        Sha256 = item.Sha256,
        Width = item.Width,
        Height = item.Height,
        OrientationHashes = item.OrientationHashes,
        HsvHistogram = item.HsvHistogram,
        Codec = item.Codec,
        BitRateBps = item.BitRateBps,
        DurationTicks = item.Duration?.Ticks,
        AudioSampleRateHz = item.AudioSampleRateHz,
        AudioBitRateBps = item.AudioBitRateBps,
        FrameOrientationHashes = item.FrameOrientationHashes?.ToList(),
        FaceEmbeddings = item.Faces?.Select(f => f.Embedding).ToList(),
        BodyEmbeddings = item.Bodies?.Select(b => b.Embedding).ToList()
    };
}
