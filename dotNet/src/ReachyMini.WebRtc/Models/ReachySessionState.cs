namespace ReachyMini.WebRtc.Models;

public enum ReachySessionState
{
    Disconnected = 0,
    SignalingConnected = 1,
    SessionNegotiating = 2,
    Streaming = 3,
    Recovering = 4,
    Stopped = 5
}
