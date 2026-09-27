using System.Text.Json;
using CorsiDuplicate.Core.Models;

namespace CorsiDuplicate.Infrastructure.Persistence;

/// <summary>
/// Persists the list of folders the user has added (path, enabled state, last-scanned
/// timestamp, item count) to %LocalAppData%\CorsiDuplicate\folders.json, so they're still
/// there next time the app launches without re-browsing.
/// </summary>
public sealed class ManagedFolderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public ManagedFolderStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CorsiDuplicate");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "folders.json");
    }

    public List<ManagedFolder> Load()
    {
        if (!File.Exists(_filePath))
        {
            return new List<ManagedFolder>();
        }

        try
        {
            using var stream = File.OpenRead(_filePath);
            return JsonSerializer.Deserialize<List<ManagedFolder>>(stream, JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            return new List<ManagedFolder>();
        }
    }

    public void Save(IEnumerable<ManagedFolder> folders)
    {
        using var stream = File.Create(_filePath);
        JsonSerializer.Serialize(stream, folders.ToList(), JsonOptions);
    }
}
