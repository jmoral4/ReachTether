using System.Text.Json;
using System.Text.Json.Nodes;
using ReachTether.WebRtc.Abstractions;
using ReachTether.WebRtc.Models;

namespace ReachTether.WebRtc.Internal;

internal sealed class JsonDataChannelClient : IDataChannelClient
{
    private readonly ISignalingClient _signalingClient;

    public JsonDataChannelClient(ISignalingClient signalingClient)
    {
        _signalingClient = signalingClient;
        _signalingClient.MessageReceived += OnMessageReceived;
    }

    public event Action<JsonNode>? CommandReceived;

    public Task SendCommandAsync(JsonObject command, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToElement(command);

        var message = new WebRtcSignalingMessage
        {
            Type = "data_channel.command",
            Payload = payload,
            CorrelationId = Guid.NewGuid().ToString("N")
        };

        return _signalingClient.SendAsync(message, cancellationToken);
    }

    private void OnMessageReceived(WebRtcSignalingMessage message)
    {
        if (!string.Equals(message.Type, "data_channel.command", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(message.Type, "data_channel.response", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var node = JsonNode.Parse(message.Payload.GetRawText());
        if (node is not null)
        {
            CommandReceived?.Invoke(node);
        }
    }
}
