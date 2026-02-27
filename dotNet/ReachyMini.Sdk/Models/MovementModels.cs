using System.Text.Json.Serialization;

namespace ReachyMini.Sdk.Models;

/// <summary>
/// Represent a 3D pose using position (x, y, z) in meters and orientation (roll, pitch, yaw) angles in radians.
/// </summary>
public class XYZRPYPose
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("z")]
    public double Z { get; set; }

    [JsonPropertyName("roll")]
    public double Roll { get; set; }

    [JsonPropertyName("pitch")]
    public double Pitch { get; set; }

    [JsonPropertyName("yaw")]
    public double Yaw { get; set; }
}

/// <summary>
/// Represent a 3D pose by its 4x4 transformation matrix (translation is expressed in meters).
/// </summary>
public class Matrix4x4Pose
{
    [JsonPropertyName("m")]
    public required double[] M { get; set; } // Array of 16 elements
}

/// <summary>
/// Represent the full body including the head pose and the joints for antennas.
/// </summary>
public class FullBodyTarget
{
    [JsonPropertyName("target_head_pose")]
    public object? TargetHeadPose { get; set; } // Can be XYZRPYPose or Matrix4x4Pose

    [JsonPropertyName("target_antennas")]
    public double[]? TargetAntennas { get; set; } // Array of 2 elements [left, right]

    [JsonPropertyName("target_body_yaw")]
    public double? TargetBodyYaw { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; set; }
}

/// <summary>
/// Request model for the goto endpoint.
/// </summary>
public class GotoModelRequest
{
    [JsonPropertyName("head_pose")]
    public object? HeadPose { get; set; } // Can be XYZRPYPose or Matrix4x4Pose

    [JsonPropertyName("antennas")]
    public double[]? Antennas { get; set; } // Array of 2 elements

    [JsonPropertyName("body_yaw")]
    public double? BodyYaw { get; set; }

    [JsonPropertyName("duration")]
    public required double Duration { get; set; }

    [JsonPropertyName("interpolation")]
    public InterpolationMode Interpolation { get; set; } = InterpolationMode.Minjerk;
}

/// <summary>
/// Model representing a unique identifier for a move task.
/// </summary>
public class MoveUUID
{
    [JsonPropertyName("uuid")]
    public required Guid Uuid { get; set; }
}

/// <summary>
/// Represent the full state of the robot including all joint positions and poses.
/// </summary>
public class FullState
{
    [JsonPropertyName("control_mode")]
    public MotorControlMode? ControlMode { get; set; }

    [JsonPropertyName("head_pose")]
    public object? HeadPose { get; set; } // Can be XYZRPYPose or Matrix4x4Pose

    [JsonPropertyName("head_joints")]
    public double[]? HeadJoints { get; set; }

    [JsonPropertyName("body_yaw")]
    public double? BodyYaw { get; set; }

    [JsonPropertyName("antennas_position")]
    public double[]? AntennasPosition { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; set; }

    [JsonPropertyName("passive_joints")]
    public double[]? PassiveJoints { get; set; }
}
