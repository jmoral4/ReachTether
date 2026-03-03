## Executive summary (what will block your roadmap)
The biggest “future-feature friction” isn’t ALSA/WebRTC—it’s how you talk to the model:

1. **`OpenAiTransport.CompleteChatAsync` flattens the conversation into a single plain-text string** (`BuildResponsesInput`). That makes **tool calling, RAG citations, image inputs, and structured outputs** unnecessarily hard (or impossible) to do cleanly.
2. **Realtime orchestrator currently gates streamed audio playback on `userTranscript` being non-empty**, which often isn’t available when audio deltas start arriving. This can silently break “realtime” behavior.
3. **Legacy transcription path writes every utterance to a temp WAV file**. That’s workable now, but it becomes painful once you add “audio + image snapshots every so often” (more data, higher frequency, more I/O contention).

If you fix only one thing now: **switch to a structured “turn object” and a structured Responses API request shape** (messages/content parts + tools), and treat audio/image/RAG/tool results as first-class parts of a turn.

---

## Future Roadmap
 1. "video processing", which is really an image snapshot sent with audio every so often
 2. Tool use, initially "SmartyMode" which will be a 'tool' that allows the realtime AI to call out to a smarter and slower AI model, and get a response.
 3. Retrieval Augmented Generation (RAG) Query component (connecting to an Off-device ReachTether.Server) - unclear if with every request, on demand via tool, or only when using 'SmartyMode'.
 4. Face recognition with RAG (remember people and things about them) - individualized greatings.

---

## Roadmap alignment review

### (1) “Video processing” (image snapshots with audio)
**Current state:** no multimodal message model; audio is handled as either:
- legacy: capture → WAV → transcription → text chat → TTS WAV → playback
- realtime: PCM frames → realtime session; audio deltas → playback

**Problem areas**
- **No unified “turn” object** that can carry `{audio, transcript, images, timestamps}`.
- Legacy chat requests are **string-only**, so adding image input will force a rewrite later.
- **WebRTC has a video track negotiated** (`H264` recvonly) but you do not decode or expose frames anywhere—so it currently doesn’t help with snapshots.

**Improvements to make now**
- Create a domain model for multimodal turns:
  ```csharp
  public sealed record UserTurn(
      IReadOnlyList<AudioFrame> Audio,
      string? Transcript,
      IReadOnlyList<ImageFrame> Images,
      DateTimeOffset StartedAt,
      DateTimeOffset EndedAt,
      string CorrelationId);
  ```
  Where `ImageFrame` includes `byte[] JpegBytes`, timestamp, camera id, etc.
- Introduce an abstraction **independent of WebRTC/ALSA**:
  - `ICameraSnapshotProvider.GetSnapshotAsync(...)`
  - Later you can implement it via Reachy WebRTC video decode, V4L2, RTSP, etc.
- Decide *now* how you’ll sync snapshots to audio:
  - simplest: capture **one snapshot at speech end** (or at speech start + end)
  - later: capture every N seconds during long speech

---

### (2) Tool use (“SmartyMode” tool that calls a slower model)
**Current state:** your “chat” call cannot express tools because it’s a plain input string and you parse only `output_text`.

**Problem areas**
- Tool calling needs:
  - tool schemas (name/description/parameters)
  - tool call requests from the model
  - tool result messages fed back to the model
- Your current request/response shape doesn’t preserve roles or tool messages.

**Improvements to make now**
- Define a tool execution layer *separate* from orchestrators:
  ```csharp
  public interface IToolExecutor {
      Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct);
  }
  ```
- Represent tool calls/results in your conversation state explicitly (don’t bake into strings).
- “SmartyMode” should be just another tool:
  - `smarty_mode(question, context, budget)` → returns answer + optional citations
- Add guardrails:
  - max tool depth (prevent recursion)
  - timeouts + cancellation
  - rate limiting (avoid runaway tool loops)

---

### (3) RAG query component (off-device ReachTether.Server)
**Current state:** no place to inject retrieval results cleanly.

**Key decision:** “every request vs on-demand vs only in SmartyMode”
- My suggestion: **on-demand via tool** as the default (and optionally auto-call based on heuristics).
  - This prevents constant network dependency/latency for casual turns.
  - Also maps nicely to “SmartyMode”: slow model can decide to retrieve.

**Improvements to make now**
- Add a retrieval interface:
  ```csharp
  public interface IRetrievalClient {
      Task<IReadOnlyList<RagChunk>> QueryAsync(RagQuery query, CancellationToken ct);
  }
  ```
- Standardize how RAG results enter the model context:
  - as tool results (preferred)
  - include metadata for citations (`source`, `timestamp`, `docId`)
- Add caching keyed by `(userId?, query embedding hash, time window)`.

---

### (4) Face recognition + RAG (remember people, individualized greetings)
**Current state:** no identity concept; personalities exist but no “user profile”.

**Improvements to make now**
- Introduce `IIdentityProvider` + `IUserMemoryStore`:
  - `IdentifyAsync(image)` → `personId/confidence`
  - `GetProfile(personId)` / `UpsertMemory(personId, facts)`
- Treat “greeting” as a **policy**: if confidence > threshold and consent is granted, greet by name; otherwise generic.
- Store privacy flags:
  - consent, retention policy, last-seen timestamps

---

## Critical code-level findings & improvements

## 1) OpenAI integration (largest long-term blocker)

### 1.1 `CompleteChatAsync` loses structure (roles, tool messages, multimodal)
You convert messages to:
```
user: ...
assistant: ...
```
and pass that as a single string. That causes:
- no tool calling
- no images/video
- no reliable “system vs user vs tool” separation
- harder safety controls later (you can’t reliably isolate tool outputs)

**Fix**
- Keep conversation as a **structured list of message objects** all the way to the API.
- When you move to image snapshots, you want content parts (text + image) instead of flattening.

### 1.2 Responses API wrapper is minimal and will need expansion anyway
You post `{Model, Input, Instructions}` only. For tools/multimodal/RAG you’ll need:
- `tools`
- `tool_choice`
- input as content parts (text/image/audio references)
- possibly structured outputs / JSON schemas

**Fix**
- Create a request builder class now:
  - `ResponsesRequestBuilder.Build(conversation, tools, options)`
- Make `OpenAiTransport` support:
  - `CompleteAsync(ConversationState state, Turn turn, CancellationToken ct)`

---

## 2) Realtime orchestrator: streaming playback gating bug
In `ConversationItemStreamingPartDeltaUpdate`, audio playback is gated by:

```csharp
&& !string.IsNullOrWhiteSpace(userTranscript)
```

But `userTranscript` often arrives **after** response audio deltas start. Result: the assistant may not speak until late or at all.

**Fix**
- Remove that gate for audio playback. If you need shutdown-intent suppression, gate on `suppressResponseForShutdownIntent` only.
- If you want to avoid speaking before transcription finishes, do it explicitly:
  - buffer first N ms of assistant audio until transcription finished, then decide play/drop

---

## 3) Transcription path: temp file I/O and stereo handling

### 3.1 Disk temp WAV per utterance
This will become expensive once you add periodic snapshots and/or longer sessions.

**Fix**
- Prefer in-memory multipart upload if supported by your client library.
- If you must write to disk, pool filenames and ensure async flush patterns are sane.

### 3.2 Legacy pipeline sends **stereo** WAV to transcription
Capture default is `Channels = 2`. Many speech pipelines do better with mono.

**Fix**
- Downmix to mono for transcription in legacy path (you already do it in realtime).
- Consider capturing mono at the ALSA level unless you truly need stereo.

---

## 4) Audio pipeline performance & GC pressure (important on embedded)

### 4.1 Hot-path allocations (resampling/channel conversion)
`AdjustChannels` and `ResamplePcm16` allocate new arrays every call. In streaming, that’s constant churn.

**Fix**
- Use `ArrayPool<byte>` / `ArrayPool<short>` and span-based processing.
- Consider a small DSP utility that reuses buffers:
  - `PcmConverter.Convert(pcm, srcRate, srcCh, dstRate, dstCh, pooledBuffers)`

### 4.2 ALSA read/write pins a new buffer every call
`GCHandle.Alloc(...Pinned)` each chunk can fragment/pressure GC.

**Fix**
- Keep a reusable pinned buffer per device/session, or use unmanaged buffers via `Marshal.AllocHGlobal` and copy out.
- Or use `unsafe` + `fixed` with careful lifetime control.

---

## 5) Hosting / DI / HttpClient hygiene
### 5.1 `new HttpClient()` as singleton
You register a raw singleton HttpClient and also custom clients. It works, but makes retries/policies/telemetry harder.

**Fix**
- Use `IHttpClientFactory` (typed clients):
  - `services.AddHttpClient<ReachyMiniClient>(...)`
  - `services.AddHttpClient<OpenAiResponsesClient>(...)`
- Add resilience (timeouts, retry for transient failures) where appropriate.

---

## 6) Session/command APIs: missing return values
`ReachyWebRtcSession.SendCommandAsync` waits for a response but returns `Task` (drops the response).

**Fix**
- Change to:
  ```csharp
  Task<JsonNode> SendCommandAsync(JsonObject cmd, CancellationToken ct = default);
  ```
This matters once you start using tools/RAG over the tether.

---

## 7) Logging & observability (will matter a lot with multimodal + tools)
Right now logs are split between `Console.WriteLine` and `ILogger`.

**Fix**
- Standardize on `ILogger` with structured fields:
  - correlationId, turnId, personalityId, pipelineMode, model, latencyMs
- Add basic metrics:
  - transcription latency
  - chat latency
  - TTS latency
  - dropped audio frames
  - realtime session resets and reasons

---

## Suggested “next step” refactor that unlocks all roadmap items
Do a small refactor before adding video/tools:

1. **Create a `TurnContext` and `ConversationState`** (structured, not strings).
2. **Replace `BuildResponsesInput` with structured request building** (even if you still only send text today).
3. Add a **Tool Registry + Executor** (even if only “SmartyMode” exists).
4. Add `IRetrievalClient` but stub it (so the wiring exists).
5. Add `ICameraSnapshotProvider` but stub it (returns null until implemented).

That keeps future additions additive instead of rewriting orchestrators/transports.

---

## Quick hit list (high value, low risk)
- Remove realtime audio playback dependency on `userTranscript`.
- Downmix to mono for legacy transcription (or capture mono).
- Replace temp WAV file with in-memory upload if possible (or at least make it optional/configurable).
- Introduce a `SendCommandAsync` return value for WebRTC session responses.
- Begin moving away from “string prompt concatenation” to structured messages.

---

## Clarifying questions (to target the refactor)
1. For snapshots: will the camera be **on Reachy** via WebRTC H264, or a **local camera** on the tether device?
2. Do you want **tools callable in realtime mode** (during a streaming turn), or only in turn-based/legacy?
3. For face recognition: do you require **fully on-device** recognition, or is off-device OK?

If you answer those, I can propose a concrete interface set + folder layout and show exactly how your `OpenAiTransport` and orchestrators should change with minimal disruption.