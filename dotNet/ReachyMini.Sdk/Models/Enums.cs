using System.Text.Json.Serialization;

namespace ReachyMini.Sdk.Models;

/// <summary>
/// Kinds of app source.
/// </summary>
public enum SourceKind
{
    HfSpace,
    DashboardSelection,
    Local,
    Installed
}

/// <summary>
/// Status of a running app.
/// </summary>
public enum AppState
{
    Starting,
    Running,
    Done,
    Stopping,
    Error
}

/// <summary>
/// Enum representing the state of the Reachy Mini daemon.
/// </summary>
public enum DaemonState
{
    NotInitialized,
    Starting,
    Running,
    Stopping,
    Stopped,
    Error
}

/// <summary>
/// Enum for motor control modes.
/// </summary>
public enum MotorControlMode
{
    Enabled,
    Disabled,
    GravityCompensation
}

/// <summary>
/// Interpolation modes for movement.
/// </summary>
public enum InterpolationMode
{
    Linear,
    Minjerk,
    Ease,
    Cartoon
}

/// <summary>
/// Enum for job status.
/// </summary>
public enum JobStatus
{
    Pending,
    InProgress,
    Done,
    Failed
}
