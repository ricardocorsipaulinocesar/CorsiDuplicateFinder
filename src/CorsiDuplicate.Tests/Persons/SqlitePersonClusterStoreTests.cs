using CorsiDuplicate.Infrastructure.Persons;

namespace CorsiDuplicate.Tests.Persons;

public class SqlitePersonClusterStoreTests
{
    [Fact]
    public void Similar_embeddings_reuse_the_same_cluster_and_dissimilar_ones_create_new_clusters()
    {
        var folder = @"C:\test\folder_" + Guid.NewGuid();
        using var store = new SqlitePersonClusterStore();

        var personA1 = new float[] { 1f, 0f, 0f, 0f };
        var personA2 = new float[] { 0.98f, 0.02f, 0f, 0f }; // near-identical to A1
        var personB1 = new float[] { 0f, 0f, 1f, 0f }; // orthogonal -> clearly different person

        var idA1 = store.MatchOrCreateCluster(folder, personA1);
        var idA2 = store.MatchOrCreateCluster(folder, personA2);
        var idB1 = store.MatchOrCreateCluster(folder, personB1);

        Assert.Equal(idA1, idA2);
        Assert.NotEqual(idA1, idB1);
    }

    [Fact]
    public void Repeated_scans_of_an_unchanged_folder_do_not_grow_the_cluster_count_unboundedly()
    {
        var folder = @"C:\test\folder_" + Guid.NewGuid();
        using var store = new SqlitePersonClusterStore();

        var embedding = new float[] { 0.5f, 0.5f, 0.5f, 0.5f };
        var ids = new HashSet<Guid>();
        for (var i = 0; i < 10; i++)
        {
            ids.Add(store.MatchOrCreateCluster(folder, embedding));
        }

        Assert.Single(ids);
    }
}
