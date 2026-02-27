namespace ReachTether.WebRtc.Models;

public sealed class ReachyIceServerOptions
{
    public string Url { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Credential { get; set; }
}
