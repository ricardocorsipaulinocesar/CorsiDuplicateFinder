using System.Text.Json;

namespace CorsiDuplicate.Infrastructure.Persistence;

/// <summary>
/// Persists small app-wide preferences (currently just the thumbnails-per-video count)
/// to %LocalAppData%\CorsiDuplicate\settings.json — same location and format as
/// <see cref="ManagedFolderStore"/>'s folders.json, so they're still there next launch.
/// </summary>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public AppSettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CorsiDuplicate");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = File.OpenRead(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        using var stream = File.Create(_filePath);
        JsonSerializer.Serialize(stream, settings, JsonOptions);
    }
}
