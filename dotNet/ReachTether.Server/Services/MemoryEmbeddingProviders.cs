using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ReachTether.Server.Services;

public interface INamedMemoryEmbeddingProvider
{
    string Name { get; }
    bool IsAvailable { get; }
    Task<EmbeddingVectorResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken);
}

internal sealed class LocalMemoryEmbeddingProviderStub(IConfiguration configuration) : INamedMemoryEmbeddingProvider
{
    public string Name => "local";
    public bool IsAvailable => configuration.GetValue("Memory:LocalEmbeddings:Enabled", false);

    public Task<EmbeddingVectorResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException("No in-process local embedding provider is configured.");
}

internal sealed class OpenAiMemoryEmbeddingProvider(HttpClient httpClient, IConfiguration configuration) : INamedMemoryEmbeddingProvider
{
    public string Name => "openai";
    public bool IsAvailable => !string.IsNullOrWhiteSpace(configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

    public async Task<EmbeddingVectorResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
    {
        var model = configuration["OpenAI:EmbeddingsModel"] ?? "text-embedding-3-small";
        using var response = await httpClient.PostAsJsonAsync(
            "embeddings",
            new OpenAiEmbeddingRequest(model, request.Input),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<OpenAiEmbeddingResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Embeddings endpoint returned an empty payload.");
        var vector = payload.Data.FirstOrDefault()?.Embedding
            ?? throw new InvalidOperationException("Embeddings endpoint returned no embedding vector.");
        return new EmbeddingVectorResult(Name, payload.Model ?? model, vector.Count, vector);
    }

    private sealed record OpenAiEmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input);

    private sealed record OpenAiEmbeddingData(
        [property: JsonPropertyName("embedding")] IReadOnlyList<float> Embedding);

    private sealed record OpenAiEmbeddingResponse(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("data")] IReadOnlyList<OpenAiEmbeddingData> Data);
}

public sealed class ConfigurableMemoryEmbeddingProvider(
    IConfiguration configuration,
    IEnumerable<INamedMemoryEmbeddingProvider> providers,
    ILogger<ConfigurableMemoryEmbeddingProvider> logger) : IMemoryEmbeddingProvider
{
    private readonly IReadOnlyDictionary<string, INamedMemoryEmbeddingProvider> providersByName = providers
        .ToDictionary(static provider => provider.Name, StringComparer.OrdinalIgnoreCase);

    public async Task<EmbeddingVectorResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
    {
        var preferred = configuration["Memory:PreferredEmbeddingProvider"] ?? "local";
        var fallback = configuration["Memory:FallbackEmbeddingProvider"] ?? "openai";

        if (providersByName.TryGetValue(preferred, out var provider) && provider.IsAvailable)
        {
            return await provider.EmbedAsync(request, cancellationToken);
        }

        logger.LogInformation("Preferred embedding provider '{Provider}' unavailable; falling back to '{Fallback}'.", preferred, fallback);

        if (providersByName.TryGetValue(fallback, out var fallbackProvider) && fallbackProvider.IsAvailable)
        {
            return await fallbackProvider.EmbedAsync(request, cancellationToken);
        }

        throw new InvalidOperationException($"No usable embedding provider was available. Preferred='{preferred}', fallback='{fallback}'.");
    }
}
