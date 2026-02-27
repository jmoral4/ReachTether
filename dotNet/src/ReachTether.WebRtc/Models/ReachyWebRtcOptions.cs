namespace ReachTether.WebRtc.Models;

public sealed class ReachyWebRtcOptions
{
    public string SignalingUrl { get; set; } = string.Empty;
    public string? AccessToken { get; set; }
    public string? RobotId { get; set; }

    // Preferred producer metadata name from signaling list messages.
    public string ProducerName { get; set; } = "reachymini";

    // Explicit producer peer ID; when set it takes precedence over ProducerName.
    public string? ProducerPeerId { get; set; }

    public int AudioFrameDurationMs { get; set; } = 20;
    public int JitterBufferMs { get; set; } = 250;
    public int CommandTimeoutMs { get; set; } = 3000;

    public int SignalingHandshakeTimeoutMs { get; set; } = 5000;
    public int SessionStartTimeoutMs { get; set; } = 10000;
    public int StreamingReadyTimeoutMs { get; set; } = 12000;

    public List<ReachyIceServerOptions> IceServers { get; set; } = [];
}
