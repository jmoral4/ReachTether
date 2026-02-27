using Microsoft.Extensions.Options;
using ReachyMini.Sdk.Configuration;
using ReachyMini.Sdk.Models;

namespace ReachyMini.Sdk.Clients;

/// <summary>
/// Client for managing volume and audio settings.
/// </summary>
public class VolumeClient : BaseClient
{
    public VolumeClient(HttpClient httpClient, IOptions<ReachyMiniOptions> options) 
        : base(httpClient, options)
    {
    }

    /// <summary>
    /// Get the current volume level.
    /// </summary>
    public Task<VolumeResponse> GetVolumeAsync(CancellationToken cancellationToken = default)
        => GetAsync<VolumeResponse>("/api/volume/current", cancellationToken);

    /// <summary>
    /// Set the volume level and play a test sound.
    /// </summary>
    public Task<VolumeResponse> SetVolumeAsync(
        VolumeRequest request, 
        CancellationToken cancellationToken = default)
        => PostAsync<VolumeRequest, VolumeResponse>("/api/volume/set", request, cancellationToken);

    /// <summary>
    /// Play a test sound.
    /// </summary>
    public Task<TestSoundResponse> PlayTestSoundAsync(CancellationToken cancellationToken = default)
        => PostAsync<TestSoundResponse>("/api/volume/test-sound", cancellationToken);

    /// <summary>
    /// Get the current microphone input volume level.
    /// </summary>
    public Task<VolumeResponse> GetMicrophoneVolumeAsync(CancellationToken cancellationToken = default)
        => GetAsync<VolumeResponse>("/api/volume/microphone/current", cancellationToken);

    /// <summary>
    /// Set the microphone input volume level.
    /// </summary>
    public Task<VolumeResponse> SetMicrophoneVolumeAsync(
        VolumeRequest request, 
        CancellationToken cancellationToken = default)
        => PostAsync<VolumeRequest, VolumeResponse>("/api/volume/microphone/set", request, cancellationToken);
}
