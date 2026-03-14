using System.Net.Http.Json;
using System.Text.Json;

namespace ReachTether.Server.Services;

public sealed class SmartyModeClient(HttpClient httpClient, IConfiguration configuration) : ISmartyModeClient
{
    public async Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(httpClient.DefaultRequestHeaders.Authorization?.Parameter))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is not configured for the server.");
        }

        var model = configuration["OpenAI:SmartyModel"] ?? "gpt-5.4";
        using var response = await httpClient.PostAsJsonAsync(
            "responses",
            new
            {
                model,
                input = prompt
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (TryExtractOutputText(document.RootElement, out var text))
        {
            return text;
        }

        throw new InvalidOperationException("OpenAI response did not include output text.");
    }

    private static bool TryExtractOutputText(JsonElement root, out string text)
    {
        text = string.Empty;

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return false;
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

        text = string.Join("\n\n", parts).Trim();
        return text.Length > 0;
    }
}

