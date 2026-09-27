namespace CorsiDuplicate.Core.Persons;

public interface IPersonDetector
{
    /// <summary>Detects full-body/person regions and returns a coarse embedding per region.</summary>
    IReadOnlyList<PersonDetection> DetectPersons(string imagePath);
}
