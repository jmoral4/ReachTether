using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReachyMini.WebRtc.Abstractions;
using ReachyMini.WebRtc.Models;

namespace ReachyMini.WebRtc.Signaling;

public sealed class WebSocketSignalingClient : ISignalingClient
{
    private readonly ReachyWebRtcOptions _options;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private Task? _receiveLoop;
    private CancellationTokenSource? _receiveCts;

    public WebSocketSignalingClient(ReachyWebRtcOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public event Action<WebRtcSignalingMessage>? MessageReceived;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.SignalingUrl))
        {
            throw new InvalidOperationException("ReachyMini:SignalingUrl must be configured.");
        }

        _socket = new ClientWebSocket();

        if (!string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            _socket.Options.SetRequestHeader("Authorization", $"Bearer {_options.AccessToken}");
        }

        if (!string.IsNullOrWhiteSpace(_options.RobotId))
        {
            _socket.Options.SetRequestHeader("X-Robot-Id", _options.RobotId);
        }

        await _socket.ConnectAsync(new Uri(_options.SignalingUrl), cancellationToken);

        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), _receiveCts.Token);
    }

    public async Task SendAsync(WebRtcSignalingMessage message, CancellationToken cancellationToken = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Signaling socket is not connected.");
        }

        var rawMessage = CreateOutboundMessage(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(rawMessage, SerializerOptions);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _receiveCts?.Cancel();

        if (_socket is not null && _socket.State == WebSocketState.Open)
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
        }

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _socket?.Dispose();
        _socket = null;

        _receiveCts?.Dispose();
        _receiveCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _sendLock.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_socket is null)
        {
            return;
        }

        var buffer = new byte[64 * 1024];

        while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(ms.ToArray());

            try
            {
                var node = JsonNode.Parse(json)?.AsObject();
                if (node is null)
                {
                    continue;
                }

                var type = node["type"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(type))
                {
                    continue;
                }

                var payload = JsonSerializer.SerializeToElement(node, SerializerOptions);
                MessageReceived?.Invoke(new WebRtcSignalingMessage
                {
                    Type = type,
                    Payload = payload,
                    CorrelationId = node["correlationId"]?.GetValue<string>()
                });
            }
            catch
            {
                // Ignore malformed signaling payloads to keep stream alive.
            }
        }
    }

    private static JsonObject CreateOutboundMessage(WebRtcSignalingMessage message)
    {
        JsonObject outbound;

        if (message.Payload.ValueKind is JsonValueKind.Object)
        {
            outbound = JsonNode.Parse(message.Payload.GetRawText())?.AsObject() ?? new JsonObject();
        }
        else
        {
            outbound = new JsonObject();
        }

        outbound["type"] = message.Type;

        if (!string.IsNullOrWhiteSpace(message.CorrelationId) && outbound["correlationId"] is null)
        {
            outbound["correlationId"] = message.CorrelationId;
        }

        if (message.Payload.ValueKind is not JsonValueKind.Object and not JsonValueKind.Undefined)
        {
            outbound["payload"] = JsonNode.Parse(message.Payload.GetRawText());
        }

        return outbound;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
