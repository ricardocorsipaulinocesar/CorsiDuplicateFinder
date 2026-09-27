namespace CorsiDuplicate.Core.Persons;

public interface IFaceDetector
{
    /// <summary>Detects faces in the image and returns each one's identity embedding.</summary>
    IReadOnlyList<FaceDetection> DetectFaces(string imagePath);
}
