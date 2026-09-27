using CorsiDuplicate.Core.Persons;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace CorsiDuplicate.Infrastructure.Persons;

/// <summary>
/// Face detection via YuNet (wrapped natively by OpenCvSharp's FaceDetectorYN) and
/// identity embedding via SFace, run directly through ONNX Runtime — OpenCvSharp's
/// managed binding doesn't expose OpenCV's own FaceRecognizerSF wrapper, so this
/// aligns each detected face using YuNet's 5-point landmarks (the same reference
/// alignment SFace was trained on) and feeds the 112x112 crop to the ONNX model
/// directly. Both models are small, free, Apache-2.0-licensed OpenCV Zoo assets,
/// run entirely on-device — no cloud calls. Degrades to "no faces found" (rather
/// than throwing) if the models can't be downloaded or loaded.
/// </summary>
public sealed class OpenCvFaceRecognizer : IFaceDetector, IDisposable
{
    // Standard ArcFace/SFace 112x112 alignment reference points (left eye, right eye,
    // nose tip, left mouth corner, right mouth corner) that the embedding model expects.
    private static readonly Point2f[] ReferenceLandmarks =
    {
        new(38.2946f, 51.6963f),
        new(73.5318f, 51.5014f),
        new(56.0252f, 71.7366f),
        new(41.5493f, 92.3655f),
        new(70.7299f, 92.2041f),
    };

    private InferenceSession? _embeddingSession;
    private bool _modelsReady;

    public bool TryEnsureModelsLoaded()
    {
        if (_modelsReady)
        {
            return true;
        }

        if (!ModelProvisioner.EnsureAvailableAsync().GetAwaiter().GetResult())
        {
            return false;
        }

        try
        {
            _embeddingSession = new InferenceSession(ModelProvisioner.SFacePath);
            _modelsReady = true;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<FaceDetection> DetectFaces(string imagePath)
    {
        if (!TryEnsureModelsLoaded())
        {
            return Array.Empty<FaceDetection>();
        }

        using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (image.Empty())
        {
            return Array.Empty<FaceDetection>();
        }

        // The managed binding only exposes Create+Detect (no dynamic resize setter),
        // so a fresh detector sized to this image is created per call.
        using var detector = FaceDetectorYN.Create(ModelProvisioner.YuNetPath, "", new Size(image.Width, image.Height));

        using var facesMat = new Mat();
        detector.Detect(image, facesMat);

        if (facesMat.Empty())
        {
            return Array.Empty<FaceDetection>();
        }

        // YuNet's output row layout: x, y, w, h, then 5 landmark (x, y) pairs, then score.
        var results = new List<FaceDetection>();
        for (var i = 0; i < facesMat.Rows; i++)
        {
            var landmarks = new Point2f[5];
            for (var p = 0; p < 5; p++)
            {
                landmarks[p] = new Point2f(facesMat.At<float>(i, 4 + p * 2), facesMat.At<float>(i, 5 + p * 2));
            }

            using var aligned = AlignFace(image, landmarks);
            var embedding = ComputeEmbedding(aligned);
            if (embedding is not null)
            {
                results.Add(new FaceDetection { Embedding = embedding });
            }
        }

        return results;
    }

    private static Mat AlignFace(Mat image, Point2f[] landmarks)
    {
        // A similarity transform (rotation + uniform scale + translation) estimated
        // from the detected landmarks to the reference template, same approach
        // OpenCV's own FaceRecognizerSF::alignCrop uses internally.
        using var src = Mat.FromPixelData(5, 1, MatType.CV_32FC2, landmarks);
        using var dst = Mat.FromPixelData(5, 1, MatType.CV_32FC2, ReferenceLandmarks);
        using var transform = Cv2.EstimateAffinePartial2D(src, dst);

        var aligned = new Mat();
        Cv2.WarpAffine(image, aligned, transform, new Size(112, 112));
        return aligned;
    }

    private float[]? ComputeEmbedding(Mat alignedFaceBgr)
    {
        try
        {
            // Raw BGR pixel values, NCHW, no normalization — matches OpenCV's own
            // FaceRecognizerSF preprocessing (blobFromImage with scale=1, mean=0).
            var tensor = new DenseTensor<float>(new[] { 1, 3, 112, 112 });
            for (var y = 0; y < 112; y++)
            {
                for (var x = 0; x < 112; x++)
                {
                    var pixel = alignedFaceBgr.At<Vec3b>(y, x);
                    tensor[0, 0, y, x] = pixel.Item0;
                    tensor[0, 1, y, x] = pixel.Item1;
                    tensor[0, 2, y, x] = pixel.Item2;
                }
            }

            var inputName = _embeddingSession!.InputMetadata.Keys.First();
            using var results = _embeddingSession.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
            var output = results.First().AsEnumerable<float>().ToArray();
            return output.Length > 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _embeddingSession?.Dispose();
    }
}
