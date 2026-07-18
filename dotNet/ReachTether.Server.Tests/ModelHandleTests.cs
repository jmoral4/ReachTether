using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using ReachTether.Server.Services;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ReachTether.Server.Tests;

public sealed class ModelHandleTests
{
    [Fact]
    public async Task RobotChat_SendsLunaModelWithLowReasoningEffort()
    {
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(
            """
            {"id":"resp_1","output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}]}
            """));
        var responsesHttpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };
        var openAiClient = new OpenAIClient("test-key");
        var transport = new OpenAiTransport(
            openAiClient,
            new RobotAppOptions
            {
                ChatModel = "gpt-5.6-luna@low",
                ChatFallbackModel = "gpt-5.6-luna@low"
            },
            new AudioClients(
                openAiClient.GetAudioClient("whisper-1"),
                openAiClient.GetAudioClient("gpt-4o-mini-tts")),
            new OpenAiResponsesClient(responsesHttpClient),
            NullLogger<OpenAiTransport>.Instance);

        await transport.CompleteChatAsync([new UserChatMessage("hello")]);

        AssertLunaAtLow(handler.LastRequestBody);
    }

    [Fact]
    public async Task FactExtraction_SendsLunaModelWithLowReasoningEffort()
    {
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(
            """
            {"output":[{"content":[{"text":"{\"facts\":[],\"sessionSummary\":null}"}]}]}
            """));
        var service = CreateExtractionService(handler);

        await service.ExtractAsync(
            new PersistSessionTurnRequest(
                "session-1",
                "turn-1",
                "hello",
                "hi",
                "legacy_chat",
                "gpt-5.6-luna",
                null,
                "default",
                null,
                null),
            CancellationToken.None);

        AssertLunaAtLow(handler.LastRequestBody);
    }

    [Fact]
    public async Task SessionSummary_SendsLunaModelWithLowReasoningEffort()
    {
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(
            """
            {"output":[{"content":[{"text":"summary"}]}]}
            """));
        var service = CreateExtractionService(handler);

        await service.SummarizeSessionAsync(
            [new PromptRecentTurn("user", "hello", DateTimeOffset.UtcNow)],
            CancellationToken.None);

        AssertLunaAtLow(handler.LastRequestBody);
    }

    private static UserFactExtractionService CreateExtractionService(RecordingHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        var configuration = new ConfigurationBuilder().Build();
        return new UserFactExtractionService(
            httpClient,
            configuration,
            NullLogger<UserFactExtractionService>.Instance);
    }

    private static void AssertLunaAtLow(string? requestBody)
    {
        Assert.NotNull(requestBody);
        using var payload = JsonDocument.Parse(requestBody);
        Assert.Equal("gpt-5.6-luna", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("low", payload.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
