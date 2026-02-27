using System.Text.Json.Nodes;

namespace ReachTether.WebRtc.Abstractions;

public interface IDataChannelClient
{
    event Action<JsonNode>? CommandReceived;
    Task SendCommandAsync(JsonObject command, CancellationToken cancellationToken = default);
}
