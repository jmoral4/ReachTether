using System.Text.Json.Serialization;

namespace ReachyMini.Sdk.Models;

/// <summary>
/// Information about an available or installed app.
/// </summary>
public class AppInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("source_kind")]
    public required SourceKind SourceKind { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("extra")]
    public Dictionary<string, object>? Extra { get; set; }
}

/// <summary>
/// Status of an app.
/// </summary>
public class AppStatus
{
    [JsonPropertyName("info")]
    public required AppInfo Info { get; set; }

    [JsonPropertyName("state")]
    public required AppState State { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Pydantic model for install job status.
/// </summary>
public class JobInfo
{
    [JsonPropertyName("command")]
    public required string Command { get; set; }

    [JsonPropertyName("status")]
    public required JobStatus Status { get; set; }

    [JsonPropertyName("logs")]
    public required List<string> Logs { get; set; }
}

/// <summary>
/// Request model for installing a private HuggingFace space.
/// </summary>
public class PrivateSpaceInstallRequest
{
    [JsonPropertyName("space_id")]
    public required string SpaceId { get; set; }
}
