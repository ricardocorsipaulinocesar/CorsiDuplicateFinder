using CorsiDuplicate.Core.Persons;
using Microsoft.Data.Sqlite;

namespace CorsiDuplicate.Infrastructure.Persons;

/// <summary>
/// One SQLite database per scanned folder (%LocalAppData%\CorsiDuplicate\persons\<hash>.db),
/// holding every distinct face/body embedding cluster discovered in that folder. Matching
/// is a linear cosine-similarity scan against each cluster's running-average centroid,
/// which is fine at the scale of one folder's cluster count. The library persists and
/// grows across repeated scans, which is what makes later scans of the same folder
/// recognize recurring people faster (fewer new clusters) and more accurately (centroids
/// stabilize as more samples accumulate).
/// </summary>
public sealed class SqlitePersonClusterStore : IPersonClusterStore, IDisposable
{
    private readonly string _directory;
    private readonly Dictionary<string, SqliteConnection> _connections = new();
    private readonly object _lock = new();

    public SqlitePersonClusterStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CorsiDuplicate", "persons");
        Directory.CreateDirectory(_directory);
    }

    public Guid MatchOrCreateCluster(string folderPath, float[] embedding, double similarityThreshold = 0.6)
    {
        // The read-then-write match/update/insert isn't atomic, and ScanPipeline
        // processes many files in the same folder concurrently, so this whole
        // operation is serialized per store instance to avoid racing duplicate
        // clusters for the same person detected in two photos at once.
        lock (_lock)
        {
            return MatchOrCreateClusterCore(folderPath, embedding, similarityThreshold);
        }
    }

    private Guid MatchOrCreateClusterCore(string folderPath, float[] embedding, double similarityThreshold)
    {
        var conn = GetConnection(folderPath);

        Guid? bestId = null;
        var bestSimilarity = 0.0;
        float[]? bestCentroid = null;
        var bestSampleCount = 0;

        using (var select = conn.CreateCommand())
        {
            select.CommandText = "SELECT Id, Centroid, SampleCount FROM PersonClusters";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var id = Guid.Parse(reader.GetString(0));
                var centroid = DeserializeEmbedding((byte[])reader[1]);
                var sampleCount = reader.GetInt32(2);

                var similarity = EmbeddingComparer.CosineSimilarity(embedding, centroid);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestId = id;
                    bestCentroid = centroid;
                    bestSampleCount = sampleCount;
                }
            }
        }

        if (bestId is { } matchedId && bestSimilarity >= similarityThreshold && bestCentroid is not null)
        {
            var newSampleCount = bestSampleCount + 1;
            var newCentroid = new float[bestCentroid.Length];
            for (var i = 0; i < newCentroid.Length; i++)
            {
                // Running average: each new sample nudges the centroid slightly,
                // so it stabilizes as more of this person's photos are scanned.
                newCentroid[i] = bestCentroid[i] + (embedding[i] - bestCentroid[i]) / newSampleCount;
            }

            using var update = conn.CreateCommand();
            update.CommandText = "UPDATE PersonClusters SET Centroid = $centroid, SampleCount = $count WHERE Id = $id";
            update.Parameters.AddWithValue("$centroid", SerializeEmbedding(newCentroid));
            update.Parameters.AddWithValue("$count", newSampleCount);
            update.Parameters.AddWithValue("$id", matchedId.ToString());
            update.ExecuteNonQuery();

            return matchedId;
        }

        var newId = Guid.NewGuid();
        using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO PersonClusters (Id, Centroid, SampleCount) VALUES ($id, $centroid, 1)";
        insert.Parameters.AddWithValue("$id", newId.ToString());
        insert.Parameters.AddWithValue("$centroid", SerializeEmbedding(embedding));
        insert.ExecuteNonQuery();

        return newId;
    }

    private SqliteConnection GetConnection(string folderPath)
    {
        if (_connections.TryGetValue(folderPath, out var existing))
        {
            return existing;
        }

        var folderHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(folderPath.ToLowerInvariant())));
        var dbPath = Path.Combine(_directory, folderHash + ".db");

        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS PersonClusters (
                    Id TEXT PRIMARY KEY,
                    Centroid BLOB NOT NULL,
                    SampleCount INTEGER NOT NULL
                )
                """;
            create.ExecuteNonQuery();
        }

        _connections[folderPath] = conn;
        return conn;
    }

    private static byte[] SerializeEmbedding(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DeserializeEmbedding(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    public void Dispose()
    {
        foreach (var conn in _connections.Values)
        {
            conn.Dispose();
        }
        _connections.Clear();
    }
}
