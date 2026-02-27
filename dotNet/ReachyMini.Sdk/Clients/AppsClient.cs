using Microsoft.Extensions.Options;
using ReachyMini.Sdk.Configuration;
using ReachyMini.Sdk.Models;

namespace ReachyMini.Sdk.Clients;

/// <summary>
/// Client for managing Reachy Mini apps.
/// </summary>
public class AppsClient : BaseClient
{
    public AppsClient(HttpClient httpClient, IOptions<ReachyMiniOptions> options) 
        : base(httpClient, options)
    {
    }

    /// <summary>
    /// List available apps by source kind.
    /// </summary>
    public Task<List<AppInfo>> ListAvailableAppsAsync(SourceKind sourceKind, CancellationToken cancellationToken = default)
        => GetAsync<List<AppInfo>>($"/api/apps/list-available/{sourceKind}", cancellationToken);

    /// <summary>
    /// List all available apps (including not installed).
    /// </summary>
    public Task<List<AppInfo>> ListAllAvailableAppsAsync(CancellationToken cancellationToken = default)
        => GetAsync<List<AppInfo>>("/api/apps/list-available", cancellationToken);

    /// <summary>
    /// Install a new app by its info (background, returns job_id).
    /// </summary>
    public Task<Dictionary<string, string>> InstallAppAsync(AppInfo appInfo, CancellationToken cancellationToken = default)
        => PostAsync<AppInfo, Dictionary<string, string>>("/api/apps/install", appInfo, cancellationToken);

    /// <summary>
    /// Remove an installed app by its name (background, returns job_id).
    /// </summary>
    public Task<Dictionary<string, string>> RemoveAppAsync(string appName, CancellationToken cancellationToken = default)
        => PostAsync<Dictionary<string, string>>($"/api/apps/remove/{appName}", cancellationToken);

    /// <summary>
    /// Get status/logs for a job.
    /// </summary>
    public Task<JobInfo> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
        => GetAsync<JobInfo>($"/api/apps/job-status/{jobId}", cancellationToken);

    /// <summary>
    /// Start an app by its name.
    /// </summary>
    public Task<AppStatus> StartAppAsync(string appName, CancellationToken cancellationToken = default)
        => PostAsync<AppStatus>($"/api/apps/start-app/{appName}", cancellationToken);

    /// <summary>
    /// Restart the currently running app.
    /// </summary>
    public Task<AppStatus> RestartCurrentAppAsync(CancellationToken cancellationToken = default)
        => PostAsync<AppStatus>("/api/apps/restart-current-app", cancellationToken);

    /// <summary>
    /// Stop the currently running app.
    /// </summary>
    public Task<object> StopCurrentAppAsync(CancellationToken cancellationToken = default)
        => PostAsync<object>("/api/apps/stop-current-app", cancellationToken);

    /// <summary>
    /// Get the status of the currently running app, if any.
    /// </summary>
    public Task<AppStatus?> GetCurrentAppStatusAsync(CancellationToken cancellationToken = default)
        => GetAsync<AppStatus?>("/api/apps/current-app-status", cancellationToken);

    /// <summary>
    /// Install a private HuggingFace space (requires HF token).
    /// </summary>
    public Task<Dictionary<string, string>> InstallPrivateSpaceAsync(
        PrivateSpaceInstallRequest request, 
        CancellationToken cancellationToken = default)
        => PostAsync<PrivateSpaceInstallRequest, Dictionary<string, string>>(
            "/api/apps/install-private-space", request, cancellationToken);
}
