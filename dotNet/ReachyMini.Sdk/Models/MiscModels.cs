using System.Text.Json.Serialization;

namespace ReachyMini.Sdk.Models;

/// <summary>
/// Request model for saving a HuggingFace token.
/// </summary>
public class TokenRequest
{
    [JsonPropertyName("token")]
    public required string Token { get; set; }
}

/// <summary>
/// Response model for token operations.
/// </summary>
public class TokenResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Request model for setting volume.
/// </summary>
public class VolumeRequest
{
    [JsonPropertyName("volume")]
    public required int Volume { get; set; } // 0-100
}

/// <summary>
/// Response model for volume operations.
/// </summary>
public class VolumeResponse
{
    [JsonPropertyName("volume")]
    public required int Volume { get; set; }

    [JsonPropertyName("device")]
    public required string Device { get; set; }

    [JsonPropertyName("platform")]
    public required string Platform { get; set; }
}

/// <summary>
/// Response model for test sound operations.
/// </summary>
public class TestSoundResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }
}
