using Microsoft.Extensions.Options;
using ReachyMini.Sdk.Configuration;
using ReachyMini.Sdk.Models;

namespace ReachyMini.Sdk.Clients;

/// <summary>
/// Client for accessing robot state information.
/// </summary>
public class StateClient : BaseClient
{
    public StateClient(HttpClient httpClient, IOptions<ReachyMiniOptions> options) 
        : base(httpClient, options)
    {
    }

    /// <summary>
    /// Get the present head pose.
    /// </summary>
    public Task<object> GetHeadPoseAsync(
        bool usePoseMatrix = false, 
        CancellationToken cancellationToken = default)
        => GetAsync<object>($"/api/state/present_head_pose?use_pose_matrix={usePoseMatrix}", cancellationToken);

    /// <summary>
    /// Get the present body yaw (in radians).
    /// </summary>
    public Task<double> GetBodyYawAsync(CancellationToken cancellationToken = default)
        => GetAsync<double>("/api/state/present_body_yaw", cancellationToken);

    /// <summary>
    /// Get the present antenna joint positions (in radians) - (left, right).
    /// </summary>
    public Task<double[]> GetAntennaJointPositionsAsync(CancellationToken cancellationToken = default)
        => GetAsync<double[]>("/api/state/present_antenna_joint_positions", cancellationToken);

    /// <summary>
    /// Get the full robot state, with optional fields.
    /// </summary>
    public Task<FullState> GetFullStateAsync(
        bool withControlMode = true,
        bool withHeadPose = true,
        bool withTargetHeadPose = false,
        bool withHeadJoints = false,
        bool withTargetHeadJoints = false,
        bool withBodyYaw = true,
        bool withTargetBodyYaw = false,
        bool withAntennaPositions = true,
        bool withTargetAntennaPositions = false,
        bool withPassiveJoints = false,
        bool usePoseMatrix = false,
        CancellationToken cancellationToken = default)
    {
        var query = $"?with_control_mode={withControlMode}" +
                    $"&with_head_pose={withHeadPose}" +
                    $"&with_target_head_pose={withTargetHeadPose}" +
                    $"&with_head_joints={withHeadJoints}" +
                    $"&with_target_head_joints={withTargetHeadJoints}" +
                    $"&with_body_yaw={withBodyYaw}" +
                    $"&with_target_body_yaw={withTargetBodyYaw}" +
                    $"&with_antenna_positions={withAntennaPositions}" +
                    $"&with_target_antenna_positions={withTargetAntennaPositions}" +
                    $"&with_passive_joints={withPassiveJoints}" +
                    $"&use_pose_matrix={usePoseMatrix}";
        
        return GetAsync<FullState>($"/api/state/full{query}", cancellationToken);
    }
}
