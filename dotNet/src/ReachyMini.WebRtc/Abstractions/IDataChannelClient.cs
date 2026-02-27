using System.Text.Json.Nodes;

namespace ReachyMini.WebRtc.Abstractions;

public interface IDataChannelClient
{
    event Action<JsonNode>? CommandReceived;
    Task SendCommandAsync(JsonObject command, CancellationToken cancellationToken = default);
}
