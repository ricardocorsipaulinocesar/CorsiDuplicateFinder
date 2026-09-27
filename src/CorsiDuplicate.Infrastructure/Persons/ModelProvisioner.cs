namespace CorsiDuplicate.Infrastructure.Persons;

/// <summary>
/// Downloads the small, Apache-2.0-licensed OpenCV Zoo ONNX models used for face
/// detection (YuNet, ~230KB) and face recognition (SFace, ~37MB) once into
/// %LocalAppData%\CorsiDuplicate\models, the same lazy-provisioning pattern used for
/// the ffmpeg binaries — no manual setup required.
/// </summary>
public static class ModelProvisioner
{
    private const string YuNetUrl =
        "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx";
    private const string SFaceUrl =
        "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new();

    public static string ModelsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CorsiDuplicate", "models");

    public static string YuNetPath => Path.Combine(ModelsDirectory, "face_detection_yunet_2023mar.onnx");
    public static string SFacePath => Path.Combine(ModelsDirectory, "face_recognition_sface_2021dec.onnx");

    /// <summary>
    /// Ensures both models exist locally, downloading whichever is missing.
    /// Returns false (without throwing) if a download fails — e.g. offline — so
    /// face/person recognition can degrade to a no-op rather than crash the scan.
    /// </summary>
    public static async Task<bool> EnsureAvailableAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelsDirectory);

        await Gate.WaitAsync(ct);
        try
        {
            return await EnsureFileAsync(YuNetUrl, YuNetPath, ct) && await EnsureFileAsync(SFaceUrl, SFacePath, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> EnsureFileAsync(string url, string destination, CancellationToken ct)
    {
        if (File.Exists(destination) && new FileInfo(destination).Length > 0)
        {
            return true;
        }

        try
        {
            var tempPath = destination + ".download";
            await using (var response = await Http.GetStreamAsync(url, ct))
            await using (var fileStream = File.Create(tempPath))
            {
                await response.CopyToAsync(fileStream, ct);
            }
            File.Move(tempPath, destination, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
