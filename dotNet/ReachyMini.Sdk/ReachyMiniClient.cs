using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ReachyMini.Sdk.Clients;
using ReachyMini.Sdk.Configuration;

namespace ReachyMini.Sdk;

/// <summary>
/// Main client for interacting with Reachy Mini Robot API.
/// Provides access to all API endpoints through specialized clients.
/// </summary>
public class ReachyMiniClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<ReachyMiniOptions> _options;

    /// <summary>
    /// Client for managing apps.
    /// </summary>
    public AppsClient Apps { get; }

    /// <summary>
    /// Client for managing the daemon.
    /// </summary>
    public DaemonClient Daemon { get; }

    /// <summary>
    /// Client for motor control.
    /// </summary>
    public MotorsClient Motors { get; }

    /// <summary>
    /// Client for robot movements.
    /// </summary>
    public MoveClient Move { get; }

    /// <summary>
    /// Client for robot state information.
    /// </summary>
    public StateClient State { get; }

    /// <summary>
    /// Client for volume and audio settings.
    /// </summary>
    public VolumeClient Volume { get; }

    /// <summary>
    /// Client for HuggingFace authentication.
    /// </summary>
    public AuthClient Auth { get; }

    /// <summary>
    /// Initializes a new instance of the ReachyMiniClient.
    /// </summary>
    public ReachyMiniClient(HttpClient httpClient, IOptions<ReachyMiniOptions> options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // Configure HttpClient
        _httpClient.BaseAddress = new Uri(_options.Value.BaseUrl);
        _httpClient.Timeout = _options.Value.Timeout;

        // Initialize clients
        Apps = new AppsClient(_httpClient, _options);
        Daemon = new DaemonClient(_httpClient, _options);
        Motors = new MotorsClient(_httpClient, _options);
        Move = new MoveClient(_httpClient, _options);
        State = new StateClient(_httpClient, _options);
        Volume = new VolumeClient(_httpClient, _options);
        Auth = new AuthClient(_httpClient, _options);
    }

    /// <summary>
    /// Health check endpoint to reset the health check timer.
    /// </summary>
    public async Task<Dictionary<string, string>> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync("/health-check", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken) 
            ?? new Dictionary<string, string>();
    }
}
