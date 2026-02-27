using System.Text.Json;

namespace ReachTether.WebRtc.Models;

public sealed class WebRtcSignalingMessage
{
    public required string Type { get; init; }
    public JsonElement Payload { get; init; }
    public string? CorrelationId { get; init; }
}
