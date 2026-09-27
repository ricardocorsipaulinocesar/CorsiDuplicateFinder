using FFMpegCore;
using Xabe.FFmpeg.Downloader;

namespace CorsiDuplicate.Infrastructure.Media;

/// <summary>
/// Ensures ffmpeg.exe/ffprobe.exe exist locally (downloading the official static
/// build once into %LocalAppData%\CorsiDuplicate\ffmpeg on first use) and points
/// FFMpegCore at them, so video support works out of the box with no manual setup.
/// </summary>
public static class FfmpegBinaryProvisioner
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _configured;

    public static async Task EnsureAvailableAsync(CancellationToken ct = default)
    {
        if (_configured)
        {
            return;
        }

        await Gate.WaitAsync(ct);
        try
        {
            if (_configured)
            {
                return;
            }

            var binaryFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CorsiDuplicate", "ffmpeg");
            Directory.CreateDirectory(binaryFolder);

            var hasFfmpeg = File.Exists(Path.Combine(binaryFolder, "ffmpeg.exe"));
            var hasFfprobe = File.Exists(Path.Combine(binaryFolder, "ffprobe.exe"));

            if (!hasFfmpeg || !hasFfprobe)
            {
                Xabe.FFmpeg.FFmpeg.SetExecutablesPath(binaryFolder);
                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official);
            }

            GlobalFFOptions.Configure(new FFOptions { BinaryFolder = binaryFolder, TemporaryFilesFolder = Path.GetTempPath() });
            _configured = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
