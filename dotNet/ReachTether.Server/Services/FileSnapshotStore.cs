using System.Collections.Concurrent;
using System.Text.Json;
using ReachTether.Server.Models;

namespace ReachTether.Server.Services;

public sealed class FileSnapshotStore : ISnapshotStore
{
    private readonly ConcurrentDictionary<string, SnapshotArtifact> artifactsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SnapshotArtifact> artifacts = [];
    private readonly Lock gate = new();
    private readonly ILogger<FileSnapshotStore> logger;
    private readonly string storageRoot;
    private readonly string manifestPath;
    private readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public FileSnapshotStore(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<FileSnapshotStore> logger)
    {
        this.logger = logger;
        storageRoot = Path.GetFullPath(Path.Combine(
            environment.ContentRootPath,
            configuration["Snapshots:StoragePath"] ?? "data/snapshots"));
        manifestPath = Path.GetFullPath(Path.Combine(
            environment.ContentRootPath,
            configuration["Snapshots:ManifestPath"] ?? "data/snapshots/index.json"));

        LoadExistingArtifacts();
    }

    public async Task<SnapshotArtifact> SaveAsync(SnapshotUploadRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(storageRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);

        var artifactId = Guid.NewGuid().ToString("n");
        var safeFileName = string.IsNullOrWhiteSpace(request.FileName)
            ? $"{artifactId}.bin"
            : $"{artifactId}-{Path.GetFileName(request.FileName)}";
        var filePath = Path.Combine(storageRoot, safeFileName);
        var bytes = Convert.FromBase64String(request.Base64Content);
        await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);

        var artifact = new SnapshotArtifact(
            artifactId,
            request.SessionId,
            request.TurnId,
            request.ToolCallId,
            request.ToolName,
            request.Source,
            request.Question,
            request.ContentType,
            request.CapturedAt,
            DateTimeOffset.UtcNow,
            filePath,
            request.Metadata ?? new Dictionary<string, string>());

        artifactsById[artifactId] = artifact;
        lock (gate)
        {
            artifacts.Insert(0, artifact);
        }

        await PersistManifestAsync(cancellationToken);

        logger.LogInformation(
            "Stored snapshot artifact {ArtifactId} for tool {ToolName} at {FilePath}.",
            artifact.ArtifactId,
            artifact.ToolName,
            artifact.FilePath);

        return artifact;
    }

    public IReadOnlyList<SnapshotArtifact> GetRecent(int count = 50)
    {
        lock (gate)
        {
            return artifacts.Take(count).ToArray();
        }
    }

    public bool TryGet(string artifactId, out SnapshotArtifact artifact)
        => artifactsById.TryGetValue(artifactId, out artifact!);

    private void LoadExistingArtifacts()
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return;
            }

            var json = File.ReadAllText(manifestPath);
            var entries = JsonSerializer.Deserialize<List<SnapshotArtifact>>(json, serializerOptions);
            if (entries is null)
            {
                return;
            }

            foreach (var artifact in entries
                .Where(static artifact => File.Exists(artifact.FilePath))
                .OrderByDescending(static artifact => artifact.CreatedAt))
            {
                artifactsById[artifact.ArtifactId] = artifact;
                artifacts.Add(artifact);
            }

            logger.LogInformation("Loaded {Count} snapshot artifacts from {ManifestPath}.", artifacts.Count, manifestPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load snapshot manifest from {ManifestPath}.", manifestPath);
        }
    }

    private async Task PersistManifestAsync(CancellationToken cancellationToken)
    {
        SnapshotArtifact[] snapshot;
        lock (gate)
        {
            snapshot = artifacts.ToArray();
        }

        await using var stream = File.Create(manifestPath);
        await JsonSerializer.SerializeAsync(stream, snapshot, serializerOptions, cancellationToken);
    }
}
