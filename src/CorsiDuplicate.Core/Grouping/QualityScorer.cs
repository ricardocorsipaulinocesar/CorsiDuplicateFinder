using CorsiDuplicate.Core.Models;

namespace CorsiDuplicate.Core.Grouping;

/// <summary>
/// Scores a MediaItem's quality for the High/Low Quality quick filters: resolution
/// dominates (50%), then bit rate (30%), then raw file size (15%), then a small codec bonus (5%).
/// </summary>
public static class QualityScorer
{
    public static double Score(MediaItem item)
    {
        double resScore = (double)item.Width * item.Height;
        double bitRateScore = item.BitRateBps ?? 0;
        double sizeScore = item.SizeBytes;
        double codecBonus = CodecRank(item.Codec);

        return resScore * 0.50 + bitRateScore * 0.30 + sizeScore * 0.15 + codecBonus * 0.05;
    }

    private static double CodecRank(string? codec) => codec?.ToLowerInvariant() switch
    {
        "hevc" or "h265" or "av1" => 1.0,
        "h264" or "avc" => 0.6,
        "png" => 0.8,
        "jpeg" or "jpg" or "mjpeg" => 0.3,
        _ => 0.0
    };
}
