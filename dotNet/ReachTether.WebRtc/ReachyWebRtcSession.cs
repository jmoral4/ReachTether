using System.Collections.Concurrent;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReachTether.Audio;
using ReachTether.WebRtc.Abstractions;
using ReachTether.WebRtc.Models;
using ReachTether.WebRtc.Signaling;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using ReachyAudioFormat = ReachTether.Audio.AudioFormat;

namespace ReachTether.WebRtc;

public sealed class ReachyWebRtcSession : IReachySession
{
    private const string DefaultStunServerUrl = "stun:stun.l.google.com:19302";
    private const int PreferredOpusPayloadType = 111;
    private const int SecondaryOpusPayloadType = 112;
    private const int PreferredH264PayloadType = 102;
    private const string H264ConstrainedBaselineFmtp =
        "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f";

    private readonly ReachyWebRtcOptions _options;
    private readonly ISignalingClient _signalingClient;
    private readonly BoundedAudioFrameQueue _inboundFrames;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> _pendingCommands = new();
    private readonly ConcurrentDictionary<string, int> _signalingMessageTypeCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _recentSignalingTypes = new();
    private readonly ConcurrentQueue<string> _debugEvents = new();

    private readonly object _messageSync = new();
    private readonly Dictionary<string, Queue<JsonObject>> _messageBacklog = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<SignalingWaiter>> _messageWaiters = new(StringComparer.OrdinalIgnoreCase);

    private readonly AudioEncoder _audioEncoder = new();
    private readonly SemaphoreSlim _peerSdpLock = new(1, 1);

    private RTCPeerConnection? _peerConnection;
    private ReachySessionState _state = ReachySessionState.Disconnected;
    private DateTimeOffset? _lastInboundAudioFrameUtc;
    private DateTimeOffset? _lastOutboundAudioFrameUtc;

    private string? _localPeerId;
    private string? _producerPeerId;
    private string? _sessionId;
    private string _lastSignalingState = "new";
    private string _lastIceState = "new";
    private string _lastPeerConnectionState = "new";
    private string? _lastFailureReason;
    private string? _lastSignalingError;
    private int _remoteSdpCount;
    private int _localSdpCount;
    private int _remoteIceCount;
    private int _localIceCount;

    private ReachyAudioFormat _inboundAudioFormat = ReachyAudioFormat.Pcm16Mono24k;
    private SIPSorceryMedia.Abstractions.AudioFormat _outboundAudioFormat =
        new(AudioCodecsEnum.OPUS, PreferredOpusPayloadType, 48000, 1, "minptime=10;useinbandfec=1");

    private long _inboundEncodedFrames;
    private long _inboundPcmFrames;
    private long _outboundEncodedFrames;
    private long _outboundPcmFrames;

    private TaskCompletionSource<bool>? _streamingReadyTcs;

    public ReachyWebRtcSession(ReachyWebRtcOptions options)
        : this(options, new WebSocketSignalingClient(options))
    {
    }

    public ReachyWebRtcSession(ReachyWebRtcOptions options, ISignalingClient signalingClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _signalingClient = signalingClient ?? throw new ArgumentNullException(nameof(signalingClient));

        // Ensure we have at least one STUN server to guarantee local candidate gathering works
        if (_options.IceServers.Count == 0)
        {
            _options.IceServers.Add(new ReachyIceServerOptions
            {
                Url = DefaultStunServerUrl
            });
        }

        var maxFrames = Math.Max(10, _options.JitterBufferMs / Math.Max(10, _options.AudioFrameDurationMs));
        _inboundFrames = new BoundedAudioFrameQueue(maxFrames * 4);

        _signalingClient.MessageReceived += OnSignalingMessage;
    }

    public ReachySessionState State => _state;

    public string CorrelationId { get; } = Guid.NewGuid().ToString("N");

    public event Action<ReachySessionState>? StateChanged;

    public string GetDiagnosticsSummary()
    {
        var lastAudioIn = _lastInboundAudioFrameUtc?.ToString("O") ?? "never";
        var lastAudioOut = _lastOutboundAudioFrameUtc?.ToString("O") ?? "never";
        var types = string.Join(
            ", ",
            _signalingMessageTypeCounts
                .ToArray()
                .OrderByDescending(kvp => kvp.Value)
                .Take(8)
                .Select(kvp => $"{kvp.Key}:{kvp.Value}"));

        if (string.IsNullOrWhiteSpace(types))
        {
            types = "none";
        }

        var recentTypes = string.Join(" -> ", _recentSignalingTypes.ToArray().TakeLast(10));
        if (string.IsNullOrWhiteSpace(recentTypes))
        {
            recentTypes = "none";
        }

        var debugTrail = string.Join(" || ", _debugEvents.ToArray().TakeLast(14));
        if (string.IsNullOrWhiteSpace(debugTrail))
        {
            debugTrail = "none";
        }

        return string.Join(
            ", ",
            [
                $"state={_state}",
                $"peerState={_lastPeerConnectionState}",
                $"iceState={_lastIceState}",
                $"signalState={_lastSignalingState}",
                $"sessionId={_sessionId ?? "none"}",
                $"localPeerId={_localPeerId ?? "none"}",
                $"producerPeerId={_producerPeerId ?? "none"}",
                $"localSdp={_localSdpCount}",
                $"remoteSdp={_remoteSdpCount}",
                $"localIce={_localIceCount}",
                $"remoteIce={_remoteIceCount}",
                $"inboundQueue={_inboundFrames.Count}",
                $"inboundDropped={_inboundFrames.DroppedFrames}",
                $"inboundEncodedFrames={_inboundEncodedFrames}",
                $"inboundPcmFrames={_inboundPcmFrames}",
                $"outboundEncodedFrames={_outboundEncodedFrames}",
                $"outboundPcmFrames={_outboundPcmFrames}",
                $"lastAudioIn={lastAudioIn}",
                $"lastAudioOut={lastAudioOut}",
                $"lastFailure={_lastFailureReason ?? "none"}",
                $"lastSignalError={_lastSignalingError ?? "none"}",
                $"signalTypes=[{types}]",
                $"recentSignalFlow=[{recentTypes}]",
                $"debugTrail=[{debugTrail}]"
            ]);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state is ReachySessionState.Streaming or ReachySessionState.SignalingConnected or ReachySessionState.SessionNegotiating)
        {
            return;
        }

        ValidateConfiguration();

        _streamingReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastFailureReason = null;
        _remoteSdpCount = 0;
        _localSdpCount = 0;
        _remoteIceCount = 0;
        _localIceCount = 0;

        await _signalingClient.ConnectAsync(cancellationToken);
        SetState(ReachySessionState.SignalingConnected);
        AddDebugEvent("Connected signaling socket.");

        var welcome = await WaitForSignalingMessageAsync(
            "welcome",
            _options.SignalingHandshakeTimeoutMs,
            cancellationToken: cancellationToken);

        _localPeerId = welcome["peerId"]?.GetValue<string>();
        AddDebugEvent($"Received welcome. localPeerId={_localPeerId ?? "none"}.");

        await SendMessageAsync(
            "setPeerStatus",
            new JsonObject
            {
                ["roles"] = new JsonArray("listener"),
                ["meta"] = new JsonObject
                {
                    ["name"] = "reachtether-dotnet"
                }
            },
            cancellationToken);

        _producerPeerId = await ResolveProducerPeerIdAsync(cancellationToken);
        AddDebugEvent($"Resolved producerPeerId={_producerPeerId ?? "none"}.");

        CreatePeerConnection();

        await SendMessageAsync(
            "startSession",
            new JsonObject
            {
                ["peerId"] = _producerPeerId
            },
            cancellationToken);

        SetState(ReachySessionState.SessionNegotiating);

        var sessionStarted = await WaitForSignalingMessageAsync(
            "sessionStarted",
            _options.SessionStartTimeoutMs,
            cancellationToken: cancellationToken);

        _sessionId = sessionStarted["sessionId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            throw new InvalidOperationException("Signaling server returned sessionStarted without sessionId.");
        }
        AddDebugEvent($"Session started. sessionId={_sessionId}.");

        AddDebugEvent("Waiting for remote producer SDP offer.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, _options.StreamingReadyTimeoutMs)));

        try
        {
            await (_streamingReadyTcs?.Task ?? Task.CompletedTask).WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _lastFailureReason = "Timed out waiting for WebRTC peer connection to reach connected state.";
            throw new TimeoutException(
                $"Timed out waiting for WebRTC streaming readiness. {GetDiagnosticsSummary()}");
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state == ReachySessionState.Disconnected)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_sessionId) && _signalingClient.IsConnected)
        {
            try
            {
                await SendMessageAsync("endSession", new JsonObject { ["sessionId"] = _sessionId }, cancellationToken);
            }
            catch
            {
                // Ignore best-effort endSession errors during shutdown.
            }
        }

        _peerConnection?.close();
        _peerConnection?.Dispose();
        _peerConnection = null;

        await _signalingClient.DisconnectAsync(cancellationToken);

        _inboundFrames.Clear();
        _sessionId = null;
        _producerPeerId = null;
        _localPeerId = null;

        SetState(ReachySessionState.Disconnected);
    }

    public async Task SendCommandAsync(JsonObject command, CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[correlationId] = tcs;

        command["correlation_id"] = correlationId;

        await SendMessageAsync(
            "data_channel.command",
            command,
            cancellationToken,
            correlationId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, _options.CommandTimeoutMs)));

        try
        {
            await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        finally
        {
            _pendingCommands.TryRemove(correlationId, out _);
        }
    }

    public async Task<AudioFrame[]> CaptureFramesAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            return [];
        }

        var deadline = DateTimeOffset.UtcNow.Add(duration);
        var frames = new List<AudioFrame>();

        while (DateTimeOffset.UtcNow < deadline)
        {
            while (_inboundFrames.TryDequeue(out var frame))
            {
                if (frame is not null)
                {
                    frames.Add(frame);
                }
            }

            var delayMs = Math.Min(20, Math.Max(5, _options.AudioFrameDurationMs / 2));
            await Task.Delay(delayMs, cancellationToken);
        }

        while (_inboundFrames.TryDequeue(out var frame))
        {
            if (frame is not null)
            {
                frames.Add(frame);
            }
        }

        if (frames.Count == 0 && _state != ReachySessionState.Streaming)
        {
            _lastFailureReason =
                $"Capture returned zero frames while session state was '{_state}' (peer={_lastPeerConnectionState}, ice={_lastIceState}).";
        }

        return [.. frames];
    }

    public async Task PlayWaveAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        if (_peerConnection is null || _state != ReachySessionState.Streaming)
        {
            throw new InvalidOperationException(
                $"Cannot play audio while WebRTC session is not streaming. {GetDiagnosticsSummary()}");
        }

        var decoded = WavePcm16.DecodeView(wavBytes);
        var format = decoded.Format;
        var sourceSamples = MemoryMarshal.Cast<byte, short>(decoded.Pcm16Bytes).ToArray();

        var targetRate = Math.Max(8000, _outboundAudioFormat.ClockRate);
        var sourceChannels = Math.Max(1, (int)format.Channels);
        var targetChannels = Math.Max(1, _outboundAudioFormat.ChannelCount);

        short[]? rentedChannelAdjusted = null;
        var channelAdjustedSamples = AdjustChannelCount(
            sourceSamples,
            sourceChannels,
            targetChannels,
            out var channelAdjustedLength,
            out rentedChannelAdjusted);

        var sourceRate = Math.Max(1, format.SampleRateHz);
        short[] resampled;
        var resampledLength = channelAdjustedLength;
        if (sourceRate == targetRate)
        {
            resampled = channelAdjustedSamples;
        }
        else
        {
            var resampleInput = channelAdjustedLength == channelAdjustedSamples.Length
                ? channelAdjustedSamples
                : channelAdjustedSamples.AsSpan(0, channelAdjustedLength).ToArray();
            resampled = _audioEncoder.Resample(resampleInput, sourceRate, targetRate);
            resampledLength = resampled.Length;
        }

        var samplesPerChannelPerFrame = Math.Max(1, targetRate * Math.Max(10, _options.AudioFrameDurationMs) / 1000);
        var samplesPerFrame = samplesPerChannelPerFrame * targetChannels;
        var durationRtpUnits = (uint)samplesPerChannelPerFrame;
        var frame = GC.AllocateUninitializedArray<short>(samplesPerFrame);

        try
        {
            for (var offset = 0; offset < resampledLength; offset += samplesPerFrame)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var copyLength = Math.Min(samplesPerFrame, resampledLength - offset);
                resampled.AsSpan(offset, copyLength).CopyTo(frame);
                if (copyLength < samplesPerFrame)
                {
                    frame.AsSpan(copyLength).Clear();
                }

                var encoded = _audioEncoder.EncodeAudio(frame, _outboundAudioFormat);
                _peerConnection.SendAudio(durationRtpUnits, encoded);

                _outboundEncodedFrames++;
                _outboundPcmFrames++;
                _lastOutboundAudioFrameUtc = DateTimeOffset.UtcNow;

                await Task.Delay(Math.Max(5, _options.AudioFrameDurationMs), cancellationToken);
            }
        }
        finally
        {
            if (rentedChannelAdjusted is not null)
            {
                ArrayPool<short>.Shared.Return(rentedChannelAdjusted, clearArray: false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _signalingClient.MessageReceived -= OnSignalingMessage;
        await DisconnectAsync();
        await _signalingClient.DisposeAsync();
        _peerSdpLock.Dispose();
        _audioEncoder.Dispose();
    }

    private void CreatePeerConnection()
    {
        var rtcConfig = new RTCConfiguration
        {
            bundlePolicy = RTCBundlePolicy.max_bundle
        };

        if (_options.IceServers.Count > 0)
        {
            rtcConfig.iceServers =
            [
                .. _options.IceServers
                    .Where(x => !string.IsNullOrWhiteSpace(x.Url))
                    .Select(x => new RTCIceServer
                    {
                        urls = x.Url,
                        username = x.Username,
                        credential = x.Credential
                    })
            ];
        }

        _peerConnection = new RTCPeerConnection(rtcConfig);

        var videoTrack = new MediaStreamTrack(
            [
                new VideoFormat(VideoCodecsEnum.H264, PreferredH264PayloadType, 90000, H264ConstrainedBaselineFmtp)
            ],
            MediaStreamStatusEnum.RecvOnly);
        _peerConnection.addTrack(videoTrack);

        var localAudioTrack = new MediaStreamTrack(
            [
                // Keep negotiation Opus-only for robot pipeline compatibility.
                new SIPSorceryMedia.Abstractions.AudioFormat(AudioCodecsEnum.OPUS, PreferredOpusPayloadType, 48000, 1, "minptime=10;useinbandfec=1"),
                new SIPSorceryMedia.Abstractions.AudioFormat(AudioCodecsEnum.OPUS, SecondaryOpusPayloadType, 48000, 2, "minptime=10;useinbandfec=1")
            ],
            MediaStreamStatusEnum.SendRecv);

        _peerConnection.addTrack(localAudioTrack);

        _peerConnection.onicecandidate += candidate =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (candidate is null || string.IsNullOrWhiteSpace(_sessionId)) return;

                    await SendMessageAsync("peer", new JsonObject
                    {
                        ["sessionId"] = _sessionId,
                        ["ice"] = JsonSerializer.SerializeToNode(new
                        {
                            candidate = candidate.candidate,
                            sdpMid = candidate.sdpMid,
                            sdpMLineIndex = candidate.sdpMLineIndex,
                            usernameFragment = candidate.usernameFragment
                        })
                    });
                    Interlocked.Increment(ref _localIceCount);
                    if (_localIceCount <= 3) AddDebugEvent($"Sent local ICE candidate: {candidate.candidate[..Math.Min(candidate.candidate.Length, 30)]}...");
                }
                catch (Exception ex) { AddDebugEvent($"Local ICE send failed: {ex.Message}"); }
            });
        };

        _peerConnection.ondatachannel += dc =>
        {
            AddDebugEvent($"Remote data channel opened: {dc.label}");
        };

        _peerConnection.oniceconnectionstatechange += state =>
        {
            _lastIceState = state.ToString();
            AddDebugEvent($"ICE state -> {state}.");
            if (state == RTCIceConnectionState.failed || state == RTCIceConnectionState.disconnected)
            {
                SetState(ReachySessionState.Recovering);
                _lastFailureReason = $"ICE state changed to {state}.";
            }
        };

        _peerConnection.onsignalingstatechange += () =>
        {
            _lastSignalingState = _peerConnection.signalingState.ToString();
            AddDebugEvent($"PC signaling state -> {_lastSignalingState}.");
        };

        _peerConnection.onconnectionstatechange += state =>
        {
            _lastPeerConnectionState = state.ToString();
            AddDebugEvent($"PC connection state -> {state}.");

            if (state == RTCPeerConnectionState.connected)
            {
                SetState(ReachySessionState.Streaming);
                _streamingReadyTcs?.TrySetResult(true);
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                _lastFailureReason = "Peer connection entered failed state (DTLS/Handshake failure).";
                _streamingReadyTcs?.TrySetException(new InvalidOperationException(_lastFailureReason));
            }
            else if (state == RTCPeerConnectionState.closed && _state != ReachySessionState.Disconnected)
            {
                SetState(ReachySessionState.Stopped);
            }
        };

        _peerConnection.OnAudioFormatsNegotiated += formats =>
        {
            var preferred = formats.FirstOrDefault(x => x.Codec == AudioCodecsEnum.OPUS);
            if (!preferred.IsEmpty())
            {
                _outboundAudioFormat = preferred;
                AddDebugEvent($"Negotiated outbound audio codec={preferred.Codec} rate={preferred.ClockRate}.");
            }
        };

        _peerConnection.OnAudioFrameReceived += encoded =>
        {
            try
            {
                var decoded = _audioEncoder.DecodeAudio(encoded.EncodedAudio, encoded.AudioFormat);
                var pcm16 = Int16ToBytes(decoded);
                _inboundAudioFormat = new ReachyAudioFormat(Math.Max(8000, encoded.AudioFormat.ClockRate), (short)Math.Max(1, encoded.AudioFormat.ChannelCount), 16);
                _inboundFrames.Enqueue(new AudioFrame(pcm16, _inboundAudioFormat, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                _inboundEncodedFrames++; _inboundPcmFrames++;
                _lastInboundAudioFrameUtc = DateTimeOffset.UtcNow;
            }
            catch { /* Decode error */ }
        };
    }

    private async Task<string> ResolveProducerPeerIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.ProducerPeerId))
        {
            return _options.ProducerPeerId;
        }

        await SendMessageAsync("list", new JsonObject(), cancellationToken);

        var listMessage = await WaitForSignalingMessageAsync(
            "list",
            _options.SignalingHandshakeTimeoutMs,
            cancellationToken: cancellationToken);

        var producers = listMessage["producers"] as JsonArray;
        if (producers is null || producers.Count == 0)
        {
            throw new InvalidOperationException(
                "No producers were returned by signaling server. Ensure Reachy WebRTC producer is running.");
        }

        var preferredName = _options.ProducerName.Trim();
        JsonObject? selectedProducer = null;

        foreach (var producerNode in producers)
        {
            if (producerNode is not JsonObject producer)
            {
                continue;
            }

            if (selectedProducer is null)
            {
                selectedProducer = producer;
            }

            var name = producer["meta"]?["name"]?.GetValue<string>();
            if (string.Equals(name, preferredName, StringComparison.OrdinalIgnoreCase))
            {
                selectedProducer = producer;
                break;
            }
        }

        var producerPeerId = selectedProducer?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(producerPeerId))
        {
            throw new InvalidOperationException(
                "No producer id was found in signaling list response.");
        }

        return producerPeerId;
    }

    private async Task HandlePeerMessageAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        if (_peerConnection is null)
        {
            return;
        }

        var peerEnvelope = message;
        var depth = 0;
        while (depth < 4)
        {
            if (TryGetJsonObject(peerEnvelope, "payload", out var payloadObject))
            {
                peerEnvelope = payloadObject;
                depth++;
                continue;
            }

            if (TryGetJsonObject(peerEnvelope, "peer", out var wrappedPeer))
            {
                peerEnvelope = wrappedPeer;
                depth++;
                continue;
            }

            if (TryGetJsonObject(peerEnvelope, "peerMessage", out var peerMessageObject))
            {
                peerEnvelope = peerMessageObject;
                depth++;
                continue;
            }

            if (TryGetJsonObject(peerEnvelope, "message", out var messageObject))
            {
                peerEnvelope = messageObject;
                depth++;
                continue;
            }

            break;
        }

        var messageSessionId = GetString(peerEnvelope, "sessionId")
                               ?? GetString(message, "sessionId")
                               ?? GetString(peerEnvelope, "session_id")
                               ?? GetString(message, "session_id");

        if (!string.IsNullOrWhiteSpace(messageSessionId) && string.IsNullOrWhiteSpace(_sessionId))
        {
            _sessionId = messageSessionId;
            AddDebugEvent($"Captured sessionId from incoming message: {_sessionId}");
        }

        if (!string.IsNullOrWhiteSpace(_sessionId) &&
            !string.IsNullOrWhiteSpace(messageSessionId) &&
            !string.Equals(_sessionId, messageSessionId, StringComparison.Ordinal))
        {
            AddDebugEvent($"Skipping peer message with mismatched sessionId: expected={_sessionId}, received={messageSessionId}");
            return;
        }

        var sdp = ExtractSdpObject(peerEnvelope) ?? ExtractSdpObject(message);
        var sdpFound = false;

        if (sdp is not null)
        {
            await _peerSdpLock.WaitAsync(cancellationToken);
            try
            {
                var sdpTypeText = sdp["type"]?.GetValue<string>();
                var sdpBody = sdp["sdp"]?.GetValue<string>();

                if (string.IsNullOrWhiteSpace(sdpTypeText) || string.IsNullOrWhiteSpace(sdpBody))
                {
                    AddDebugEvent("Peer SDP message is missing required type/sdp fields.");
                    throw new InvalidOperationException("Peer SDP message is missing required type/sdp fields.");
                }

                var remoteDescription = new RTCSessionDescriptionInit
                {
                    type = ParseSdpType(sdpTypeText),
                    sdp = sdpBody
                };

                var remoteSetResult = _peerConnection.setRemoteDescription(remoteDescription);
                if (remoteSetResult != SetDescriptionResultEnum.OK)
                {
                    var sdpLines = sdpBody.Split('\n').Take(10);
                    var sdpPreview = string.Join(" | ", sdpLines.Select(x => x.Trim())).Replace("\r", "");
                    if (sdpPreview.Length > 200) sdpPreview = sdpPreview[..200] + "...";

                    _lastFailureReason = $"setRemoteDescription failed: {remoteSetResult}. SDP prefix: {sdpPreview}";
                    AddDebugEvent(_lastFailureReason);
                    throw new InvalidOperationException($"setRemoteDescription failed: {remoteSetResult}.");
                }

                Interlocked.Increment(ref _remoteSdpCount);
                AddDebugEvent($"Applied remote SDP ({remoteDescription.type}).");
                sdpFound = true;

                if (remoteDescription.type == RTCSdpType.offer)
                {
                    var answer = _peerConnection.createAnswer(null);
                    await _peerConnection.setLocalDescription(answer);

                    await SendMessageAsync(
                        "peer",
                        new JsonObject
                        {
                            ["sessionId"] = _sessionId ?? messageSessionId,
                            ["sdp"] = JsonSerializer.SerializeToNode(new
                            {
                                type = answer.type.ToString(),
                                sdp = answer.sdp
                            })
                        },
                        cancellationToken);

                    Interlocked.Increment(ref _localSdpCount);
                    AddDebugEvent("Sent local SDP answer.");
                }
            }
            catch (Exception ex)
            {
                _lastFailureReason = $"Failed to process SDP: {ex.Message}";
                AddDebugEvent(_lastFailureReason);
                throw;
            }
            finally
            {
                _peerSdpLock.Release();
            }
        }

        var iceCandidates = ExtractIceCandidates(peerEnvelope);
        if (iceCandidates.Count == 0)
        {
            if (!sdpFound)
            {
                var keys = string.Join(", ", peerEnvelope.Select(x => x.Key).Take(12));
                if (!string.IsNullOrWhiteSpace(keys))
                {
                    var payloadPreview = peerEnvelope.ToJsonString();
                    if (payloadPreview.Length > 240)
                    {
                        payloadPreview = payloadPreview[..240] + "...";
                    }

                    AddDebugEvent($"Peer message did not contain parseable SDP/ICE. keys=[{keys}] payload={payloadPreview}");
                }
            }

            return;
        }

        foreach (var ice in iceCandidates)
        {
            var candidateText = GetString(ice, "candidate");
            if (string.IsNullOrWhiteSpace(candidateText))
            {
                continue;
            }

            ushort sdpMLineIndex = 0;
            var mlineNode = ice["sdpMLineIndex"] ?? ice["sdp_m_line_index"];
            if (mlineNode is not null)
            {
                if (mlineNode is JsonValue mlineValueInt && mlineValueInt.TryGetValue<int>(out var mlineInt))
                {
                    sdpMLineIndex = (ushort)Math.Max(0, mlineInt);
                }
                else if (mlineNode is JsonValue mlineValueString &&
                         mlineValueString.TryGetValue<string>(out var mlineText) &&
                         int.TryParse(mlineText, out var parsed))
                {
                    sdpMLineIndex = (ushort)Math.Max(0, parsed);
                }
            }

            _peerConnection.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidateText,
                sdpMid = GetString(ice, "sdpMid") ?? GetString(ice, "sdp_mid"),
                sdpMLineIndex = sdpMLineIndex,
                usernameFragment = GetString(ice, "usernameFragment") ?? GetString(ice, "username_fragment")
            });

            Interlocked.Increment(ref _remoteIceCount);
            if (_remoteIceCount <= 3)
            {
                AddDebugEvent("Applied remote ICE candidate.");
            }
        }
    }

    private async Task<JsonObject> WaitForSignalingMessageAsync(
        string type,
        int timeoutMs,
        Func<JsonObject, bool>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        predicate ??= static _ => true;
        SignalingWaiter? waiter = null;

        lock (_messageSync)
        {
            if (_messageBacklog.TryGetValue(type, out var buffered))
            {
                while (buffered.Count > 0)
                {
                    var candidate = buffered.Dequeue();
                    if (predicate(candidate))
                    {
                        return candidate;
                    }
                }
            }

            waiter = new SignalingWaiter(predicate);
            if (!_messageWaiters.TryGetValue(type, out var waiters))
            {
                waiters = [];
                _messageWaiters[type] = waiters;
            }

            waiters.Add(waiter);
        }

        return await WaitForWaiterAsync(waiter, type, timeoutMs, cancellationToken);
    }

    private async Task<JsonObject> WaitForWaiterAsync(
        SignalingWaiter waiter,
        string type,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs)));

        try
        {
            var node = await waiter.Task.WaitAsync(timeoutCts.Token);
            return node;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _lastFailureReason = $"Timed out waiting for signaling message '{type}'.";
            throw new TimeoutException(
                $"Timed out waiting for signaling message '{type}'. {GetDiagnosticsSummary()}");
        }
        finally
        {
            lock (_messageSync)
            {
                if (_messageWaiters.TryGetValue(type, out var waiters))
                {
                    waiters.Remove(waiter);
                    if (waiters.Count == 0)
                    {
                        _messageWaiters.Remove(type);
                    }
                }
            }
        }
    }

    private Task SendMessageAsync(
        string type,
        JsonObject payload,
        CancellationToken cancellationToken = default,
        string? correlationId = null)
    {
        return _signalingClient.SendAsync(
            new WebRtcSignalingMessage
            {
                Type = type,
                Payload = JsonSerializer.SerializeToElement(payload),
                CorrelationId = correlationId
            },
            cancellationToken);
    }

    private void OnSignalingMessage(WebRtcSignalingMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Type))
        {
            return;
        }

        _signalingMessageTypeCounts.AddOrUpdate(message.Type, 1, (_, count) => count + 1);

        _recentSignalingTypes.Enqueue(message.Type);
        while (_recentSignalingTypes.Count > 25 && _recentSignalingTypes.TryDequeue(out _))
        {
            // Keep a bounded rolling window for diagnostics.
        }

        JsonObject? payload = null;
        try
        {
            payload = JsonNode.Parse(message.Payload.GetRawText())?.AsObject();
        }
        catch
        {
            // Ignore malformed messages.
        }

        if (payload is null)
        {
            return;
        }

        DeliverOrBufferMessage(message.Type, payload);

        if (string.Equals(message.Type, "data_channel.response", StringComparison.OrdinalIgnoreCase))
        {
            var correlationId = payload["correlation_id"]?.GetValue<string>()
                                ?? payload["correlationId"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(correlationId) && _pendingCommands.TryGetValue(correlationId, out var pending))
            {
                pending.TrySetResult(payload);
            }

            return;
        }

        if (string.Equals(message.Type, "peer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Type, "sdp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Type, "offer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Type, "answer", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandlePeerMessageAsync(payload);
                }
                catch (Exception ex)
                {
                    _lastFailureReason = $"Failed to process peer signaling message ({message.Type}): {ex.Message}";
                    AddDebugEvent(_lastFailureReason);
                }
            });
            return;
        }

        if (string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
        {
            _lastSignalingError = payload.ToJsonString();
            _lastFailureReason = $"Received signaling error: {_lastSignalingError}";
            AddDebugEvent($"Signaling error: {_lastSignalingError}");
            _streamingReadyTcs?.TrySetException(new InvalidOperationException(_lastFailureReason));
            return;
        }

        if (string.Equals(message.Type, "endSession", StringComparison.OrdinalIgnoreCase))
        {
            _lastFailureReason = "Received endSession from signaling server.";
            SetState(ReachySessionState.Stopped);
        }
    }

    private void DeliverOrBufferMessage(string type, JsonObject payload)
    {
        lock (_messageSync)
        {
            if (_messageWaiters.TryGetValue(type, out var waiters))
            {
                for (var i = 0; i < waiters.Count; i++)
                {
                    var waiter = waiters[i];
                    if (waiter.Predicate(payload))
                    {
                        waiter.TrySetResult(payload);
                        waiters.RemoveAt(i);
                        return;
                    }
                }
            }

            if (!_messageBacklog.TryGetValue(type, out var queue))
            {
                queue = new Queue<JsonObject>();
                _messageBacklog[type] = queue;
            }

            queue.Enqueue(payload);
            while (queue.Count > 40)
            {
                queue.Dequeue();
            }
        }
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.SignalingUrl))
        {
            throw new InvalidOperationException("ReachyMini:SignalingUrl must be configured.");
        }

        if (_options.AudioFrameDurationMs is < 10 or > 200)
        {
            throw new InvalidOperationException(
                $"Audio:FrameDurationMs must be between 10 and 200 ms (current={_options.AudioFrameDurationMs}).");
        }

        if (_options.SignalingHandshakeTimeoutMs <= 0 ||
            _options.SessionStartTimeoutMs <= 0 ||
            _options.StreamingReadyTimeoutMs <= 0)
        {
            throw new InvalidOperationException(
                "Signaling/session timeout values must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(_options.ProducerPeerId) && string.IsNullOrWhiteSpace(_options.ProducerName))
        {
            throw new InvalidOperationException(
                "Either ReachyMini:ProducerPeerId or ReachyMini:ProducerName must be configured.");
        }

        var invalidIceServer = _options.IceServers.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Url));
        if (invalidIceServer is not null)
        {
            throw new InvalidOperationException("ReachyMini:IceServers contains an entry with an empty Url.");
        }
    }

    private void AddDebugEvent(string message)
    {
        var entry = $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {message}";
        _debugEvents.Enqueue(entry);
        while (_debugEvents.Count > 80 && _debugEvents.TryDequeue(out _))
        {
            // Keep debug log bounded.
        }
    }

    private void SetState(ReachySessionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(state);
    }

    private static RTCSdpType ParseSdpType(string sdpType)
    {
        return sdpType.Trim().ToLowerInvariant() switch
        {
            "offer" => RTCSdpType.offer,
            "answer" => RTCSdpType.answer,
            "pranswer" => RTCSdpType.pranswer,
            "rollback" => RTCSdpType.rollback,
            _ => throw new InvalidOperationException($"Unsupported SDP type '{sdpType}'.")
        };
    }

    private static JsonObject? ExtractSdpObject(JsonObject message, int depth = 0)
    {
        if (depth > 5)
        {
            return null;
        }

        if (TryGetJsonObject(message, "sdp", out var sdpObject))
        {
            if (sdpObject.TryGetPropertyValue("sdp", out _) || sdpObject.TryGetPropertyValue("type", out _))
            {
                return sdpObject;
            }
        }

        if (TryGetJsonObject(message, "description", out var descriptionObject))
        {
            if (descriptionObject.TryGetPropertyValue("sdp", out _) || descriptionObject.TryGetPropertyValue("type", out _))
            {
                return descriptionObject;
            }
        }

        if (TryGetJsonObject(message, "offer", out var offerObject))
        {
            if (offerObject["type"] is null)
            {
                offerObject["type"] = "offer";
            }

            return offerObject;
        }

        if (TryGetJsonObject(message, "answer", out var answerObject))
        {
            if (answerObject["type"] is null)
            {
                answerObject["type"] = "answer";
            }

            return answerObject;
        }

        if (message["offer"] is JsonValue offerValue &&
            offerValue.TryGetValue<string>(out var offerText) &&
            !string.IsNullOrWhiteSpace(offerText))
        {
            return new JsonObject
            {
                ["type"] = "offer",
                ["sdp"] = offerText
            };
        }

        if (message["answer"] is JsonValue answerValue &&
            answerValue.TryGetValue<string>(out var answerText) &&
            !string.IsNullOrWhiteSpace(answerText))
        {
            return new JsonObject
            {
                ["type"] = "answer",
                ["sdp"] = answerText
            };
        }

        var sdpRawText = GetString(message, "sdp") ?? GetString(message, "description");
        if (!string.IsNullOrWhiteSpace(sdpRawText) && sdpRawText.Trim().StartsWith("v=0"))
        {
            var sdpType = GetString(message, "sdpType")
                          ?? GetString(message, "sdp_type")
                          ?? GetString(message, "descriptionType")
                          ?? GetString(message, "description_type")
                          ?? GetString(message, "type");

            if (string.IsNullOrWhiteSpace(sdpType) ||
                string.Equals(sdpType, "peer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sdpType, "peerMessage", StringComparison.OrdinalIgnoreCase))
            {
                sdpType = "offer";
            }

            return new JsonObject
            {
                ["type"] = sdpType,
                ["sdp"] = sdpRawText
            };
        }

        if (TryGetJsonObject(message, "payload", out var payloadObject))
        {
            var nested = ExtractSdpObject(payloadObject, depth + 1);
            if (nested is not null)
            {
                return nested;
            }
        }

        if (TryGetJsonObject(message, "peer", out var peerObject))
        {
            var nested = ExtractSdpObject(peerObject, depth + 1);
            if (nested is not null)
            {
                return nested;
            }
        }

        if (TryGetJsonObject(message, "peerMessage", out var peerMessageObject))
        {
            var nested = ExtractSdpObject(peerMessageObject, depth + 1);
            if (nested is not null)
            {
                return nested;
            }
        }

        if (TryGetJsonObject(message, "message", out var messageObject))
        {
            var nested = ExtractSdpObject(messageObject, depth + 1);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static List<JsonObject> ExtractIceCandidates(JsonObject message, int depth = 0)
    {
        var result = new List<JsonObject>();
        if (depth > 5)
        {
            return result;
        }

        if (TryGetJsonObject(message, "ice", out var iceObject))
        {
            if (iceObject.TryGetPropertyValue("candidate", out _))
            {
                result.Add(iceObject);
            }
        }

        if (TryGetJsonObject(message, "candidate", out var candidateObject))
        {
            if (candidateObject.TryGetPropertyValue("candidate", out _))
            {
                result.Add(candidateObject);
            }
        }

        if (message["ice"] is JsonArray iceArray)
        {
            foreach (var item in iceArray)
            {
                if (item is JsonObject candidate)
                {
                    result.Add(candidate);
                }
            }
        }

        if (message["candidates"] is JsonArray candidatesArray)
        {
            foreach (var item in candidatesArray)
            {
                if (item is JsonObject candidate)
                {
                    result.Add(candidate);
                }
            }
        }

        var candidateRawText = GetString(message, "candidate");
        if (!string.IsNullOrWhiteSpace(candidateRawText) && (candidateRawText.StartsWith("candidate:") || candidateRawText.Contains("typ host")))
        {
            result.Add(new JsonObject
            {
                ["candidate"] = candidateRawText,
                ["sdpMid"] = GetString(message, "sdpMid") ?? GetString(message, "sdp_mid"),
                ["sdpMLineIndex"] = message["sdpMLineIndex"] ?? message["sdp_m_line_index"],
                ["usernameFragment"] = GetString(message, "usernameFragment") ?? GetString(message, "username_fragment")
            });
        }

        if (TryGetJsonObject(message, "payload", out var payloadObjectRec))
        {
            result.AddRange(ExtractIceCandidates(payloadObjectRec, depth + 1));
        }

        if (TryGetJsonObject(message, "peer", out var peerObjectRec))
        {
            result.AddRange(ExtractIceCandidates(peerObjectRec, depth + 1));
        }

        if (TryGetJsonObject(message, "peerMessage", out var peerMessageObjectRec))
        {
            result.AddRange(ExtractIceCandidates(peerMessageObjectRec, depth + 1));
        }

        if (TryGetJsonObject(message, "message", out var messageObjectRec))
        {
            result.AddRange(ExtractIceCandidates(messageObjectRec, depth + 1));
        }

        return result;
    }

    private static bool TryGetJsonObject(JsonObject source, string key, out JsonObject value)
    {
        value = null!;

        if (!source.TryGetPropertyValue(key, out var node) || node is null)
        {
            return false;
        }

        if (node is JsonObject objectNode)
        {
            value = objectNode;
            return true;
        }

        if (node is JsonValue textNode &&
            textNode.TryGetValue<string>(out var jsonText) &&
            !string.IsNullOrWhiteSpace(jsonText))
        {
            try
            {
                var parsed = JsonNode.Parse(jsonText) as JsonObject;
                if (parsed is not null)
                {
                    value = parsed;
                    return true;
                }
            }
            catch
            {
                // Ignore malformed nested JSON.
            }
        }

        return false;
    }

    private static string? GetString(JsonObject source, string key)
    {
        if (!source.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return null;
    }

    private static byte[] Int16ToBytes(short[] input)
    {
        var output = GC.AllocateUninitializedArray<byte>(input.Length * 2);
        Buffer.BlockCopy(input, 0, output, 0, output.Length);
        return output;
    }

    private static short[] AdjustChannelCount(
        short[] samples,
        int sourceChannels,
        int targetChannels,
        out int adjustedLength,
        out short[]? rentedBuffer)
    {
        rentedBuffer = null;
        sourceChannels = Math.Max(1, sourceChannels);
        targetChannels = Math.Max(1, targetChannels);

        if (sourceChannels == targetChannels)
        {
            adjustedLength = samples.Length;
            return samples;
        }

        var frames = samples.Length / sourceChannels;
        adjustedLength = frames * targetChannels;
        rentedBuffer = ArrayPool<short>.Shared.Rent(adjustedLength);
        var adjusted = rentedBuffer.AsSpan(0, adjustedLength);

        if (sourceChannels == 2 && targetChannels == 1)
        {
            for (var i = 0; i < frames; i++)
            {
                var left = samples[i * 2];
                var right = samples[i * 2 + 1];
                adjusted[i] = (short)((left + right) / 2);
            }

            return rentedBuffer;
        }

        if (sourceChannels == 1 && targetChannels == 2)
        {
            for (var i = 0; i < frames; i++)
            {
                var mono = samples[i];
                adjusted[i * 2] = mono;
                adjusted[i * 2 + 1] = mono;
            }

            return rentedBuffer;
        }

        // Fallback: copy first available source channel to all target channels.
        for (var frame = 0; frame < frames; frame++)
        {
            var first = samples[frame * sourceChannels];
            for (var channel = 0; channel < targetChannels; channel++)
            {
                adjusted[frame * targetChannels + channel] = first;
            }
        }

        return rentedBuffer;
    }

    private sealed class SignalingWaiter
    {
        private readonly TaskCompletionSource<JsonObject> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SignalingWaiter(Func<JsonObject, bool> predicate)
        {
            Predicate = predicate;
        }

        public Func<JsonObject, bool> Predicate { get; }

        public Task<JsonObject> Task => _tcs.Task;

        public bool TrySetResult(JsonObject message) => _tcs.TrySetResult(message);
    }
}
