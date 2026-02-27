using Microsoft.Extensions.Options;
using ReachyMini.Sdk.Configuration;
using ReachyMini.Sdk.Models;

namespace ReachyMini.Sdk.Clients;

/// <summary>
/// Client for controlling robot motors.
/// </summary>
public class MotorsClient : BaseClient
{
    public MotorsClient(HttpClient httpClient, IOptions<ReachyMiniOptions> options) 
        : base(httpClient, options)
    {
    }

    /// <summary>
    /// Get the current status of the motors.
    /// </summary>
    public Task<MotorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => GetAsync<MotorStatus>("/api/motors/status", cancellationToken);

    /// <summary>
    /// Set the motor control mode.
    /// </summary>
    public Task<Dictionary<string, string>> SetModeAsync(
        MotorControlMode mode, 
        CancellationToken cancellationToken = default)
        => PostAsync<Dictionary<string, string>>($"/api/motors/set_mode/{mode}", cancellationToken);
}
