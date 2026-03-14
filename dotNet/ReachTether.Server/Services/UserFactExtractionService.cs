using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReachTether.Server.Services;

public sealed class UserFactExtractionService(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<UserFactExtractionService> logger) : IUserFactExtractionService
{
    public async Task<UserFactExtractionResult> ExtractAsync(
        PersistSessionTurnRequest request,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var payload = BuildExtractionRequest(configuration["OpenAI:FastExtractionModel"] ?? "gpt-5-mini", request);

        using var response = await httpClient.PostAsJsonAsync("responses", payload, cancellationToken);
        if (!response.IsSuccessStatusCode && IsMiniUnavailable(response.StatusCode))
        {
            logger.LogInformation("Fast extraction model unavailable; retrying with fallback model gpt-5-nano.");
            using var retry = await httpClient.PostAsJsonAsync(
                "responses",
                BuildExtractionRequest(configuration["OpenAI:FastExtractionFallbackModel"] ?? "gpt-5-nano", request),
                cancellationToken);
            retry.EnsureSuccessStatusCode();
            return await ParseExtractionResponseAsync(retry, cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        return await ParseExtractionResponseAsync(response, cancellationToken);
    }

    private object BuildExtractionRequest(string model, PersistSessionTurnRequest request)
    {
        return new
        {
            model,
            input = BuildExtractionPrompt(request),
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "user_fact_extraction",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            facts = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    additionalProperties = false,
                                    properties = new
                                    {
                                        kind = new { type = "string" },
                                        attribute = new { type = "string" },
                                        value = new { type = "string" },
                                        normalizedValue = new { type = new[] { "string", "null" } },
                                        stability = new { type = "string" },
                                        confidence = new { type = "number" },
                                        evidence = new { type = "string" },
                                        supersedesAttribute = new { type = new[] { "string", "null" } },
                                        scopeHint = new { type = new[] { "string", "null" } }
                                    },
                                    required = new[] { "kind", "attribute", "value", "normalizedValue", "stability", "confidence", "evidence", "supersedesAttribute", "scopeHint" }
                                }
                            },
                            sessionSummary = new { type = new[] { "string", "null" } }
                        },
                        required = new[] { "facts", "sessionSummary" }
                    }
                }
            }
        };
    }

    public async Task<string> SummarizeSessionAsync(
        IReadOnlyList<PromptRecentTurn> recentTurns,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var transcript = string.Join("\n", recentTurns.Select(static turn => $"{turn.Role}: {turn.Text}"));
        using var response = await httpClient.PostAsJsonAsync(
            "responses",
            new
            {
                model = configuration["OpenAI:FastExtractionModel"] ?? "gpt-5-mini",
                input = $"""
                    Write a compact conversation memory summary in 2-3 short sentences.
                    Prioritize:
                    - who the user is
                    - durable facts like job, location, and preferences
                    - the current topic or open thread
                    
                    Transcript:
                    {transcript}
                    """
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ExtractOutputTextAsync(response, cancellationToken);
    }

    private static async Task<UserFactExtractionResult> ParseExtractionResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await ExtractOutputTextAsync(response, cancellationToken);
        var parsed = JsonSerializer.Deserialize<ExtractionPayload>(text, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Fact extraction returned an empty payload.");
        return new UserFactExtractionResult(
            parsed.Facts?.Select(static fact => new ExtractedFact(
                fact.Kind ?? "user_fact",
                fact.Attribute ?? "unknown",
                fact.Value ?? string.Empty,
                fact.NormalizedValue,
                fact.Stability ?? "stable",
                fact.Confidence,
                fact.Evidence ?? fact.Value ?? string.Empty,
                fact.SupersedesAttribute,
                fact.ScopeHint)).ToArray() ?? [],
            parsed.SessionSummary);
    }

    private static async Task<string> ExtractOutputTextAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("OpenAI response did not contain output content.");
        }

        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var textProperty))
                {
                    var value = textProperty.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        parts.Add(value.Trim());
                    }
                }
            }
        }

        var text = string.Join("\n\n", parts).Trim();
        return text.Length > 0
            ? text
            : throw new InvalidOperationException("OpenAI response did not include output text.");
    }

    private string BuildExtractionPrompt(PersistSessionTurnRequest request)
    {
        return $"""
            Extract user facts from the conversation turn. Return only facts that are explicitly stated or strongly implied.
            Prefer durable user facts like name, location, work, family role, and long-term preferences.
            Use scopeHint=profile for stable identity facts and scopeHint=session for temporary context.
            Keep confidence conservative. Do not invent facts.

            User turn:
            {request.UserText ?? "(none)"}

            Assistant turn:
            {request.AssistantText ?? "(none)"}
            """;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(httpClient.DefaultRequestHeaders.Authorization?.Parameter))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is not configured for the server.");
        }
    }

    private static bool IsMiniUnavailable(System.Net.HttpStatusCode statusCode)
        => statusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound;

    private sealed record ExtractionPayload(
        [property: JsonPropertyName("facts")] IReadOnlyList<ExtractionFact>? Facts,
        [property: JsonPropertyName("sessionSummary")] string? SessionSummary);

    private sealed record ExtractionFact(
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("attribute")] string? Attribute,
        [property: JsonPropertyName("value")] string? Value,
        [property: JsonPropertyName("normalizedValue")] string? NormalizedValue,
        [property: JsonPropertyName("stability")] string? Stability,
        [property: JsonPropertyName("confidence")] double Confidence,
        [property: JsonPropertyName("evidence")] string? Evidence,
        [property: JsonPropertyName("supersedesAttribute")] string? SupersedesAttribute,
        [property: JsonPropertyName("scopeHint")] string? ScopeHint);
}
