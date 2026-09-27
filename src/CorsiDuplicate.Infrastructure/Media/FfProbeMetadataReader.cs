using FFMpegCore;

namespace CorsiDuplicate.Infrastructure.Media;

public sealed record VideoMetadata(
    TimeSpan Duration, int Width, int Height, string? Codec, long? BitRateBps,
    int? AudioSampleRateHz, long? AudioBitRateBps);

public sealed class FfProbeMetadataReader
{
    public async Task<VideoMetadata?> TryReadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await FfmpegBinaryProvisioner.EnsureAvailableAsync(ct);
            var info = await FFProbe.AnalyseAsync(path, cancellationToken: ct);

            var video = info.PrimaryVideoStream;
            var audio = info.PrimaryAudioStream;

            return new VideoMetadata(
                Duration: info.Duration,
                Width: video?.Width ?? 0,
                Height: video?.Height ?? 0,
                Codec: video?.CodecName,
                BitRateBps: video?.BitRate > 0 ? video.BitRate : (info.Format.BitRate > 0 ? (long?)info.Format.BitRate : null),
                AudioSampleRateHz: audio?.SampleRateHz,
                AudioBitRateBps: audio?.BitRate > 0 ? audio.BitRate : null);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
