using Microsoft.Extensions.Logging;

internal sealed class ServerArtifactPublisher(
    IReachTetherServerClient serverClient,
    RobotAppOptions options,
    ILogger<ServerArtifactPublisher> logger) : IToolArtifactPublisher
{
    public async Task<IReadOnlyList<ToolArtifact>> PublishAsync(
        ToolExecutionRequest request,
        IReadOnlyList<ToolArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        if (!options.Server.Enabled || !options.Server.UploadSnapshots || artifacts.Count == 0)
        {
            return artifacts;
        }

        var publishedArtifacts = new List<ToolArtifact>(artifacts.Count);
        foreach (var artifact in artifacts)
        {
            if (artifact.BinaryContent is null || string.IsNullOrWhiteSpace(artifact.FileName))
            {
                publishedArtifacts.Add(artifact);
                continue;
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.Server.TimeoutSeconds));

                artifact.Metadata.TryGetValue("question", out var question);
                var response = await serverClient.UploadSnapshotAsync(
                    new SnapshotUploadRequest(
                        request.SessionId,
                        request.TurnId,
                        request.ToolCallId,
                        request.ToolName,
                        artifact.Source,
                        question,
                        artifact.ContentType,
                        artifact.CapturedAt,
                        artifact.FileName,
                        Convert.ToBase64String(artifact.BinaryContent),
                        artifact.Metadata),
                    timeoutCts.Token);

                publishedArtifacts.Add(artifact with
                {
                    RemoteArtifactId = response.ArtifactId,
                    RemoteContentUrl = response.ContentUrl
                });

                logger.LogInformation(
                    "Published artifact for tool {ToolName}: localId={LocalArtifactId}, remoteId={RemoteArtifactId}, url={ContentUrl}",
                    request.ToolName,
                    artifact.ArtifactId,
                    response.ArtifactId,
                    response.ContentUrl);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Timed out publishing artifact for tool {ToolName} to {BaseUrl}.",
                    request.ToolName,
                    options.Server.BaseUrl);
                publishedArtifacts.Add(artifact);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to publish artifact for tool {ToolName} to {BaseUrl}.",
                    request.ToolName,
                    options.Server.BaseUrl);
                publishedArtifacts.Add(artifact);
            }
        }

        return publishedArtifacts;
    }
}
