using CorsiDuplicate.Infrastructure.Persons;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CorsiDuplicate.Tests.Persons;

public class HogPersonDetectorTests
{
    [Fact]
    public async Task Detecting_persons_on_a_blank_image_returns_empty_without_throwing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd_person_test_" + Guid.NewGuid());
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
                        row[x] = new Rgba32(200, 200, 200);
                    }
                }
            });
            var path = Path.Combine(dir, "blank.png");
            await image.SaveAsPngAsync(path);

            var detector = new HogPersonDetector();
            var persons = detector.DetectPersons(path);

            Assert.Empty(persons);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
