using CorsiDuplicate.Infrastructure.Persons;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CorsiDuplicate.Tests.Persons;

public class OpenCvFaceRecognizerTests
{
    [Fact(Timeout = 120_000)]
    public async Task Models_download_and_load_successfully()
    {
        // Verifies the end-to-end plumbing (model download, YuNet detector creation,
        // ONNX Runtime session creation for SFace) works without throwing. A real
        // photo of a person isn't used here to avoid depending on / bundling anyone's
        // likeness just for infrastructure testing.
        var recognizer = new OpenCvFaceRecognizer();
        var ready = await Task.Run(recognizer.TryEnsureModelsLoaded);
        Assert.True(ready, "expected YuNet + SFace models to download and load");
    }

    [Fact(Timeout = 120_000)]
    public async Task Detecting_faces_on_a_faceless_image_returns_empty_without_throwing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd_face_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            using var image = new Image<Rgba32>(200, 200);
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        row[x] = new Rgba32(120, 120, 120);
                    }
                }
            });
            var path = Path.Combine(dir, "plain.png");
            await image.SaveAsPngAsync(path);

            var recognizer = new OpenCvFaceRecognizer();
            var faces = await Task.Run(() => recognizer.DetectFaces(path));

            Assert.Empty(faces);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
