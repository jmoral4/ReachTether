using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReachyMini.Sdk.Configuration;
using ReachyMini.Sdk.Exceptions;

namespace ReachyMini.Sdk.Clients;

/// <summary>
/// Base client for Reachy Mini API with common HTTP operations.
/// </summary>
public abstract class BaseClient
{
    protected readonly HttpClient HttpClient;
    protected readonly ReachyMiniOptions Options;
    protected readonly JsonSerializerOptions JsonOptions;

    protected BaseClient(HttpClient httpClient, IOptions<ReachyMiniOptions> options)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        
        JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Converters =
            {
                new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)
            }
        };
    }

    protected async Task<TResponse> GetAsync<TResponse>(string endpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await HttpClient.GetAsync(endpoint, cancellationToken);
            return await HandleResponseAsync<TResponse>(response, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new RobotNotAvailableException($"Failed to connect to Reachy Mini at {HttpClient.BaseAddress}", ex);
        }
    }

    protected async Task<TResponse> PostAsync<TRequest, TResponse>(
        string endpoint, 
        TRequest request, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await HttpClient.PostAsJsonAsync(endpoint, request, JsonOptions, cancellationToken);
            return await HandleResponseAsync<TResponse>(response, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new RobotNotAvailableException($"Failed to connect to Reachy Mini at {HttpClient.BaseAddress}", ex);
        }
    }

    protected async Task<TResponse> PostAsync<TResponse>(
        string endpoint, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await HttpClient.PostAsync(endpoint, null, cancellationToken);
            return await HandleResponseAsync<TResponse>(response, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new RobotNotAvailableException($"Failed to connect to Reachy Mini at {HttpClient.BaseAddress}", ex);
        }
    }

    protected async Task<TResponse> DeleteAsync<TResponse>(string endpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await HttpClient.DeleteAsync(endpoint, cancellationToken);
            return await HandleResponseAsync<TResponse>(response, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new RobotNotAvailableException($"Failed to connect to Reachy Mini at {HttpClient.BaseAddress}", ex);
        }
    }

    private async Task<TResponse> HandleResponseAsync<TResponse>(
        HttpResponseMessage response, 
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken);
            return result ?? throw new ReachyMiniApiException("Response was null");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        
        if (Options.ThrowOnError)
        {
            throw new ReachyMiniApiException(
                $"API request failed with status {response.StatusCode}: {response.ReasonPhrase}",
                (int)response.StatusCode,
                content);
        }

        return default!;
    }
}
