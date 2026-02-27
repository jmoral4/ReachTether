using Microsoft.Extensions.Options;
using ReachyMini.Sdk.Configuration;
using ReachyMini.Sdk.Models;

namespace ReachyMini.Sdk.Clients;

/// <summary>
/// Client for controlling robot movements.
/// </summary>
public class MoveClient : BaseClient
{
    public MoveClient(HttpClient httpClient, IOptions<ReachyMiniOptions> options) 
        : base(httpClient, options)
    {
    }

    /// <summary>
    /// Get a list of currently running move tasks.
    /// </summary>
    public Task<List<MoveUUID>> GetRunningMovesAsync(CancellationToken cancellationToken = default)
        => GetAsync<List<MoveUUID>>("/api/move/running", cancellationToken);

    /// <summary>
    /// Request a movement to a specific target.
    /// </summary>
    public Task<MoveUUID> GotoAsync(GotoModelRequest request, CancellationToken cancellationToken = default)
        => PostAsync<GotoModelRequest, MoveUUID>("/api/move/goto", request, cancellationToken);

    /// <summary>
    /// Request the robot to wake up.
    /// </summary>
    public Task<MoveUUID> WakeUpAsync(CancellationToken cancellationToken = default)
        => PostAsync<MoveUUID>("/api/move/play/wake_up", cancellationToken);

    /// <summary>
    /// Request the robot to go to sleep.
    /// </summary>
    public Task<MoveUUID> GotoSleepAsync(CancellationToken cancellationToken = default)
        => PostAsync<MoveUUID>("/api/move/play/goto_sleep", cancellationToken);

    /// <summary>
    /// List available recorded moves in a dataset.
    /// </summary>
    public Task<List<string>> ListRecordedMovesAsync(
        string datasetName, 
        CancellationToken cancellationToken = default)
        => GetAsync<List<string>>($"/api/move/recorded-move-datasets/list/{datasetName}", cancellationToken);

    /// <summary>
    /// Request the robot to play a predefined recorded move from a dataset.
    /// </summary>
    public Task<MoveUUID> PlayRecordedMoveAsync(
        string datasetName, 
        string moveName, 
        CancellationToken cancellationToken = default)
        => PostAsync<MoveUUID>($"/api/move/play/recorded-move-dataset/{datasetName}/{moveName}", cancellationToken);

    /// <summary>
    /// Stop a running move task.
    /// </summary>
    public Task<Dictionary<string, string>> StopMoveAsync(
        MoveUUID moveUuid, 
        CancellationToken cancellationToken = default)
        => PostAsync<MoveUUID, Dictionary<string, string>>("/api/move/stop", moveUuid, cancellationToken);

    /// <summary>
    /// Set a single FullBodyTarget.
    /// </summary>
    public Task<Dictionary<string, string>> SetTargetAsync(
        FullBodyTarget target, 
        CancellationToken cancellationToken = default)
        => PostAsync<FullBodyTarget, Dictionary<string, string>>("/api/move/set_target", target, cancellationToken);
}
