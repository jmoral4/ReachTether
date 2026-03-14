using ReachTether.Server.Models;

namespace ReachTether.Server.Services;

public interface ISnapshotStore
{
    Task<SnapshotArtifact> SaveAsync(SnapshotUploadRequest request, CancellationToken cancellationToken);
    IReadOnlyList<SnapshotArtifact> GetRecent(int count = 50);
    bool TryGet(string artifactId, out SnapshotArtifact artifact);
}

