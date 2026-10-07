using System.Security.Cryptography;
using System.Text;
using CorsiDuplicate.Core.Matching;
using CorsiDuplicate.Core.Models;
using CorsiDuplicate.Infrastructure.Scanning;

namespace CorsiDuplicate.Infrastructure.Media;

/// <summary>
/// One small binary file per analysed video under %LocalAppData%\CorsiDuplicate\fingerprints,
/// keyed by (path, size, last write) — decoding a whole video is the slow part, so an
/// unchanged video is only ever decoded once.
/// </summary>
public sealed class VideoFingerprintCache
{
    private const int FormatVersion = 1;
    private readonly string _directory;

    public VideoFingerprintCache()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CorsiDuplicate", "fingerprints");
        Directory.CreateDirectory(_directory);
    }

    public async Task<VideoFingerprint?> GetOrCreateAsync(MediaItem item, CancellationToken ct = default)
    {
        var file = FileFor(item);
        if (TryRead(file) is { } cached)
        {
            return cached;
        }

        var fingerprint = await VideoFingerprintExtractor.ExtractAsync(item.FullPath, ct);
        if (fingerprint is not null)
        {
            Write(file, fingerprint);
        }
        return fingerprint;
    }

    public bool Contains(MediaItem item) => File.Exists(FileFor(item));

    private string FileFor(MediaItem item)
    {
        var key = ScanCacheStore.CacheKey(item.FullPath, item.SizeBytes, item.LastWriteUtc);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(_directory, name + ".fp");
    }

    private static VideoFingerprint? TryRead(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using var reader = new BinaryReader(File.OpenRead(file));
            if (reader.ReadInt32() != FormatVersion)
            {
                return null;
            }

            var count = reader.ReadInt32();
            var hashes = new ulong[count];
            var usable = new bool[count];
            for (var i = 0; i < count; i++)
            {
                hashes[i] = reader.ReadUInt64();
                usable[i] = reader.ReadBoolean();
            }
            return new VideoFingerprint(hashes, usable);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            return null;
        }
    }

    private static void Write(string file, VideoFingerprint fingerprint)
    {
        var temp = file + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temp)))
        {
            writer.Write(FormatVersion);
            writer.Write(fingerprint.Length);
            for (var i = 0; i < fingerprint.Length; i++)
            {
                writer.Write(fingerprint.Hashes[i]);
                writer.Write(fingerprint.Usable[i]);
            }
        }
        File.Move(temp, file, overwrite: true);
    }
}
