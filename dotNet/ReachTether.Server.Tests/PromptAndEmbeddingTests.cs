using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReachTether.Server.Services;
using System.Net;
using System.Net.Http;
using System.Text;
using RobotPromptRecentTurn = global::PromptRecentTurn;
using RobotRetrievedMemoryItem = global::RetrievedMemoryItem;
using RobotSessionSummaryDescriptor = global::SessionSummaryDescriptor;

namespace ReachTether.Server.Tests;

public sealed class PromptAndEmbeddingTests
{
    [Fact]
    public void PromptBuilder_IncludesCompactMemoryBlocksWithoutDumpingTranscript()
    {
        var builder = new PromptContextBuilder();
        var prompt = builder.BuildSystemPrompt(
            "Base instructions",
            new FakeToolDefinitionSource(),
            true,
            null,
            [
                new RobotPromptRecentTurn("user", "a very long transcript line that should not become a raw dump", DateTimeOffset.UtcNow),
                new RobotPromptRecentTurn("assistant", "short reply", DateTimeOffset.UtcNow)
            ],
            [new RobotRetrievedMemoryItem("m1", "Preference", "User likes concise responses.", "user_preference", "session", "turn-1", 0.91)],
            new RobotSessionSummaryDescriptor("s1", "Current session summary", "Discussed robot battery status.", DateTimeOffset.UtcNow),
            []);

        Assert.Contains("Session Memory", prompt);
        Assert.Contains("Current Session Summary", prompt);
        Assert.DoesNotContain("raw full transcript dumps", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmbeddingSelector_FallsBackToOpenAi_WhenPreferredUnavailable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Memory:PreferredEmbeddingProvider"] = "local",
                ["Memory:FallbackEmbeddingProvider"] = "openai"
            })
            .Build();

        var selector = new ConfigurableMemoryEmbeddingProvider(
            configuration,
            [
                new FakeNamedEmbeddingProvider("local", false, new EmbeddingVectorResult("local", "unused", 2, [0f, 0f])),
                new FakeNamedEmbeddingProvider("openai", true, new EmbeddingVectorResult("openai", "test", 2, [1f, 0f]))
            ],
            NullLogger<ConfigurableMemoryEmbeddingProvider>.Instance);

        var result = await selector.EmbedAsync(new EmbeddingRequest("hello"), CancellationToken.None);

        Assert.Equal("openai", result.Provider);
    }

    [Fact]
    public async Task OpenAiEmbeddingProvider_UsesV1EmbeddingsPath()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:ApiKey"] = "test-key",
                ["OpenAI:EmbeddingsModel"] = "text-embedding-3-small"
            })
            .Build();

        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"model":"text-embedding-3-small","data":[{"embedding":[0.1,0.2]}]}
                """,
                Encoding.UTF8,
                "application/json")
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };

        var provider = new OpenAiMemoryEmbeddingProvider(httpClient, configuration);

        var result = await provider.EmbedAsync(new EmbeddingRequest("hello"), CancellationToken.None);

        Assert.Equal("https://api.openai.com/v1/embeddings", handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal(2, result.Dimensions);
    }

    private sealed class FakeToolDefinitionSource : IToolDefinitionSource
    {
        public IReadOnlyList<ToolDefinition> GetLegacyToolDefinitions() => [];
        public IReadOnlyList<RealtimeToolDefinition> GetRealtimeToolDefinitions() => [];
        public string BuildToolUsageGuidance() => "### TOOL AWARENESS\n- use tools";
    }
}
