using System.Text.Json.Serialization;

namespace ReachyMini.Sdk.Models;

/// <summary>
/// Status of the Reachy Mini daemon.
/// </summary>
public class DaemonStatus
{
    [JsonPropertyName("robot_name")]
    public required string RobotName { get; set; }

    [JsonPropertyName("state")]
    public required DaemonState State { get; set; }

    [JsonPropertyName("wireless_version")]
    public required bool WirelessVersion { get; set; }

    [JsonPropertyName("desktop_app_daemon")]
    public required bool DesktopAppDaemon { get; set; }

    [JsonPropertyName("simulation_enabled")]
    public required bool? SimulationEnabled { get; set; }

    [JsonPropertyName("backend_status")]
    public object? BackendStatus { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("wlan_ip")]
    public string? WlanIp { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary>
/// Represents the status of the motors.
/// </summary>
public class MotorStatus
{
    [JsonPropertyName("mode")]
    public required MotorControlMode Mode { get; set; }
}
