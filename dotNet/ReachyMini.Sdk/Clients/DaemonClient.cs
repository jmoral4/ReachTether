using Microsoft.Extensions.Options;
using ReachyMini.Sdk.Configuration;
using ReachyMini.Sdk.Models;

namespace ReachyMini.Sdk.Clients;

/// <summary>
/// Client for managing the Reachy Mini daemon.
/// </summary>
public class DaemonClient : BaseClient
{
    public DaemonClient(HttpClient httpClient, IOptions<ReachyMiniOptions> options) 
        : base(httpClient, options)
    {
    }

    /// <summary>
    /// Start the daemon.
    /// </summary>
    public Task<Dictionary<string, string>> StartAsync(bool wakeUp, CancellationToken cancellationToken = default)
        => PostAsync<Dictionary<string, string>>($"/api/daemon/start?wake_up={wakeUp}", cancellationToken);

    /// <summary>
    /// Stop the daemon, optionally putting the robot to sleep.
    /// </summary>
    public Task<Dictionary<string, string>> StopAsync(bool gotoSleep, CancellationToken cancellationToken = default)
        => PostAsync<Dictionary<string, string>>($"/api/daemon/stop?goto_sleep={gotoSleep}", cancellationToken);

    /// <summary>
    /// Restart the daemon.
    /// </summary>
    public Task<Dictionary<string, string>> RestartAsync(CancellationToken cancellationToken = default)
        => PostAsync<Dictionary<string, string>>("/api/daemon/restart", cancellationToken);

    /// <summary>
    /// Get the current status of the daemon.
    /// </summary>
    public Task<DaemonStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => GetAsync<DaemonStatus>("/api/daemon/status", cancellationToken);
}
