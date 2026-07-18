# ReachTether Entity Platform: Architecture Review and Evolution Roadmap

Date: 2026-07-18

## Executive summary

ReachTether has already moved beyond a disposable robot demo. It has a hosted .NET runtime, continuous audio workers, a Realtime voice path, a Responses API path, multimodal camera tools, personalities, motion composition, and an emerging face-tracking subsystem. Those are useful foundations.

The important architectural change is conceptual:

> The product should be an enduring AI entity with multiple embodiments, not a Reachy application with a remote backend.

The entity should have a stable identity, memory, sessions, tools, model policy, and event history whether it is speaking through Reachy Mini, a desktop, a phone, or no physical device at all. Reachy Mini should become one embodiment adapter. Its process should own only the things that must be close to the hardware: audio and camera capture, playback, motion, device health, and local safety behavior.

The recommended end state is a two-process modular system:

1. **Entity Host**, normally running on a desktop or laptop
   - Owns identity, personality, durable sessions, memory, knowledge, model selection, tools, scheduling, background work, and user interfaces.
   - Holds the OpenAI API credential.
   - Is the portable source of truth.

2. **Embodiment Node**, running on Reachy, a phone, or another device
   - Publishes capabilities and sensor streams.
   - Executes bounded actions such as speak, look, gesture, display, and capture.
   - Retains local safety, interruption, and degraded/offline behavior.

This should initially remain a modular monolith on the desktop plus one small Reachy edge process. It does not need Kubernetes, a fleet of microservices, or a graph database.

The highest-value first move is to extract hardware-neutral contracts and shared tool/session logic from `ReachTether.Robot`. The highest-value product feature after that is a portable entity profile directory containing an SQLite database, durable persona files, and content-addressed artifacts, with an explicit export/import command for moving between desktop and laptop.

## Review scope

This review covered the active solution and current uncommitted face-tracking work, including:

- `dotNet/ReachTether.Robot`
- `dotNet/ReachTether.Audio` and `dotNet/ReachTether.Audio.Alsa`
- `dotNet/ReachTether.WebRtc`
- `dotNet/ReachyMini.Sdk`
- the new `dotNet/ReachTether.Tests` project
- existing architecture, personality, always-on, vision, and v1.1 notes under `Docs/`
- current OpenAI documentation for Responses, Realtime, voice agents, transcription, speech generation, vision, and file search

The working tree contains in-progress face-tracking and test changes. This review treats those files as part of the current direction but does not modify them.

## What is already good

Several current choices should be preserved.

### The runtime is already a supervised host

`Program.cs` uses the .NET Generic Host, DI, hosted services, typed HTTP client creation, configuration files, and structured logging. The audio capture, playback, motion, latest-frame camera source, and face-tracking loop are already independent workers. Older notes that describe the application as a single script-like loop are no longer current.

### Audio has useful portable primitives

`ReachTether.Audio` has small platform-neutral types such as `AudioFrame`, `AudioFormat`, `WavePcm16`, and `BoundedAudioFrameQueue`. ALSA is isolated in a separate project. This is a good starting point for supporting desktop and phone media implementations later.

### The Reachy SDK is already a distinct project

`ReachyMini.Sdk` isolates the daemon API and camera implementation from most of the application. It multi-targets .NET 8, 9, and 10 and uses a typed `HttpClient`. The SDK should remain a Reachy-specific adapter rather than moving into the future entity core.

### Both modern interaction styles exist

The application supports:

- a chained pipeline: capture -> transcription -> Responses API -> TTS -> playback
- a live Realtime speech-to-speech pipeline

Keeping both is valuable. Realtime is the right default for natural conversation. The chained path remains useful when exact transcripts, policy checks, slower reasoning, deterministic tool workflows, or non-live channels matter.

### Responses requests are now structured

`OpenAiTransport` no longer flattens the entire conversation into one string. It builds role-aware Responses API input items, carries text and images as content parts, supports custom function definitions, detects function calls, and continues tool results with `previous_response_id`. Some older review notes identify string flattening as a blocker; that finding is stale in the current checkout.

### The first capability seams are emerging

`ICameraSnapshotProvider`, `ICameraSource`, `IHeadDetector`, `ILookAtProjector`, `IMotionOrchestrator`, `IAudioCapturePipeline`, and `IAudioPlaybackPipeline` are directionally correct. The issue is mainly that these interfaces are internal to the robot executable and are not organized around a reusable embodiment contract yet.

## Main architectural findings

### 1. The entity and the Reachy embodiment are currently the same application

`ReachTether.Robot` owns all of the following:

- OpenAI credentials and client construction
- model policy and fallbacks
- conversation history
- personality selection
- tool schemas and tool loops
- camera semantics
- audio device ownership
- Reachy wake/sleep and status
- Reachy motion
- face tracking
- shutdown intent and console presentation

Both orchestrators inject `ReachyMiniClient`, `LocalAudioSession`, audio pipelines, the OpenAI transport, the personality catalog, the motion orchestrator, and `CameraTool`. That makes it difficult to run the same entity without Reachy hardware or to move its durable state between machines.

The core boundary should be inverted. The entity host should depend on an embodiment abstraction; an embodiment must not define the entity.

### 2. The orchestrators remain too large and duplicate policy

`InteractionOrchestrator.cs` is about 478 lines and `RealtimeInteractionOrchestrator.cs` is about 868 lines. Both own startup, Reachy lifecycle, personality switching, shutdown intent, motion cues, response presentation, and error handling. The Realtime class additionally owns audio conversion, session management, event dispatch, and recovery.

Recent handler extraction under `Realtime/Handlers` helps, but the application still has two partially duplicated implementations of one conceptual conversation runtime.

They should share:

- session identity and turn records
- prompt composition
- tool registration, routing, timeout, and approval policy
- personality state
- memory hydration and post-turn extraction
- entity events and artifact publication
- output intent generation

They should differ only in transport-specific turn execution and audio presentation.

### 3. OpenAI transport concerns are concentrated in one large class

`OpenAiTransport.cs` is about 944 lines and combines:

- audio aggregation and WAV encoding
- transcription and fallback policy
- Responses request serialization
- multimodal message conversion
- tool schema serialization
- response parsing
- model fallback behavior
- TTS generation
- diagnostic body logging

This is functional, but it makes newer API capabilities and non-OpenAI providers harder to add safely. Model policy, provider transport, speech I/O, and domain conversation types should be separate.

Recommended split:

- `ITextAgent` / `OpenAiResponsesAgent`
- `ILiveVoiceSessionFactory` / `OpenAiRealtimeSessionFactory`
- `ISpeechRecognizer` / `OpenAiTranscriptionService`
- `ISpeechSynthesizer` / `OpenAiSpeechService`
- `IModelRouter`
- provider-neutral `ConversationItem`, `ToolCall`, and `ToolResult` records

Do not build an overly generic “AI provider” interface that reduces every provider to plain text. Define interfaces around product capabilities and allow capability discovery.

### 4. Model selection is configuration-driven but not capability-driven

The current settings independently name chat, fallback, transcription, speech, Realtime, and face-tracking models. `VoicePipeline=auto` infers pipeline type partly from the chat model name. This will become brittle as model families and API capabilities evolve.

A model should be selected from a workload profile rather than scattered strings:

```text
Voice.FastConversation
Voice.HighQualityConversation
Reasoning.Interactive
Reasoning.DeepBackground
Vision.SceneUnderstanding
Vision.FastDetection
Speech.Transcription
Speech.DiarizedTranscription
Speech.Synthesis
Embedding.Default
```

Each configured deployment should declare capabilities such as:

- text, image, audio input/output
- Realtime support
- tool calling
- structured output
- reasoning controls
- background execution
- context and latency class
- provider and data-retention policy

The router can then select a deployment based on task, latency budget, cost budget, privacy, and availability. Model names remain configuration, not branching logic spread through the runtime.

### 5. Tool execution is still camera-specific

The Responses tool loop in `InteractionOrchestrator` and `FunctionCallHandler` in the Realtime path directly understand `CameraTool`. Adding memory lookup, a desktop camera, a scheduler, home automation, or a long-running research job would add more branches to transport-specific code.

Introduce a shared registry:

```csharp
public interface IEntityTool
{
    ToolDescriptor Descriptor { get; }
    Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionContext context,
        JsonElement arguments,
        CancellationToken cancellationToken);
}
```

The registry should support:

- local entity-host tools
- embodiment-local tools
- remote/MCP tools
- synchronous results
- accepted/background jobs
- artifacts such as images and files
- timeout and retry policy
- approval requirements
- audit records

The same registry should generate both Responses and Realtime tool definitions.

### 6. Conversation continuity is transient

Legacy history is an in-memory `List<ChatMessage>` trimmed by count. Realtime continuity lives primarily inside a live API session. There is no durable entity session, user, channel, turn, event, or memory identity.

API-managed state is useful for an active turn, but it is not the entity's memory. A portable system must persist its own normalized turn and event history, including the model/provider IDs used, tool calls, artifacts, and provenance.

The current Responses payload also does not make storage/retention policy explicit. That should become a deliberate per-profile setting rather than an API default inherited accidentally.

### 7. Personality is externalized, but identity is still Reachy-shaped

`personalities.json` is better than a prompt embedded in code, and live personality switching works. However, most persona records are one large instruction string and the default identity says it is Reachy Mini.

Stable identity should not change when the entity moves from Reachy to phone or desktop. Compose the prompt from separate layers:

1. identity: name, biography, values, relationship model
2. voice/style: tone, pacing, verbal habits
3. embodiment context: current body and available senses/actions
4. user and relationship context
5. session/channel context
6. tool and safety policy
7. retrieved memories
8. current task

The embodiment layer may say “you are currently speaking through Reachy Mini,” but the identity layer should not claim the entity *is* the hardware.

### 8. The face-tracking path should not use a cloud VLM as its control loop

The in-progress face-tracking service captures at a configurable camera rate and calls `OpenAiHeadDetector`, configured by default for five detections per second using `gpt-4o-mini`. A cloud model is appropriate for occasional semantic scene understanding, not a continuous feedback controller.

Continuous tracking should use local computer vision on Reachy or the desktop, returning bounding boxes and confidence at predictable latency. The OpenAI vision path should be reserved for questions such as “who is holding the red book?” or “what changed in the room?” This separation improves latency, cost, privacy, network resilience, and motion stability.

The current `IHeadDetector` seam makes this replacement feasible.

### 9. Media abstractions exist, but endpoint ownership is muddled

`LocalAudioSession` implements `IReachySession`, including a no-op `SendCommandAsync`, while also exposing ALSA-specific streaming methods outside that interface. `IReachySession` combines connection state, command RPC, capture, and playback. It is neither a clean media endpoint nor a clean Reachy control connection.

Split it into explicit roles:

- `IAudioInput`
- `IAudioOutput`
- `IVideoSource`
- `IEmbodimentControl`
- `IEmbodimentHealth`
- `IEmbodimentConnection`

These can be implemented locally, proxied over the network, or composed differently for a phone and desktop.

### 10. Observability and privacy controls need to mature together

The code has useful correlation IDs, rolling logs, and structured messages, but also many `Console.WriteLine` calls and a default setting that logs Responses request and response bodies. As memory and video become durable, logs can contain transcripts, personal facts, tool output, and image-derived data.

Add OpenTelemetry traces and metrics keyed by entity/session/turn/call IDs, while keeping content logging off by default. Content capture should be a separate, consent-aware diagnostic mode with redaction and retention limits.

### 11. Automated coverage is still too small for the intended system

The new test project currently covers WAV encoding, a bounded queue, and a few SDK models. The most timing-sensitive and stateful behavior remains untested.

Before large extraction work, add characterization tests around:

- prompt composition
- model profile resolution
- tool routing and tool-loop limits
- Realtime event-to-state transitions
- interruption and playback truncation behavior
- session persistence
- memory provenance and deletion
- fake embodiment behavior

A fake embodiment is especially important: the full entity host should run in tests and on a developer desktop without Reachy hardware.

## Target architecture

### Entity Host

The Entity Host is the durable “self.” It should be a headless ASP.NET Core/.NET Worker process that can also host a Blazor desktop web UI.

Core responsibilities:

- entity profile and identity
- durable sessions and transcripts
- model routing and provider integrations
- prompt composition
- shared tools and approvals
- memory ingestion, retrieval, correction, and deletion
- job queue, scheduler, heartbeat, and event inbox
- connected embodiment registry
- artifact store
- API/UI endpoints
- observability and policy

The first version can run on `localhost` and use one SQLite database. It should not require a public cloud service.

### Embodiment Node

The Reachy Embodiment Node should run on or beside Reachy and make an outbound authenticated connection to the Entity Host.

Responsibilities:

- advertise capabilities and device metadata
- own ALSA devices and camera capture
- publish audio/video/perception streams
- execute motion, look, gesture, playback, and capture commands
- enforce motion limits and emergency stop locally
- handle barge-in at low latency
- report health and command results
- provide limited degraded behavior if the host is unavailable

It should not own long-term memory, the canonical personality, background agents, or the primary OpenAI credential.

### Desktop and phone

A desktop can be both the Entity Host and an embodiment. A phone will normally be an embodiment/UI client connected to the host, though a later mobile host could run a subset of the core.

Examples:

- desktop embodiment: microphone, speakers, webcam, screen, notifications
- phone embodiment: microphone, camera, speaker, display, location with permission
- Reachy embodiment: microphones, speaker, head motion, camera, status
- text-only embodiment: terminal, web chat, messaging connector

The entity chooses an active output endpoint based on presence and policy. Multiple devices must not all speak at once.

### Suggested flow

```text
Reachy / Phone / Desktop sensors
              |
              v
       Embodiment Node(s)
              |
      authenticated streams
              |
              v
          Entity Host
   +----------+-----------+
   | sessions | model     |
   | memory   | tools     |
   | identity | scheduler |
   +----------+-----------+
              |
       action intentions
              |
              v
       Embodiment Node(s)
              |
       speech / motion / UI
```

## Contracts between host and embodiments

Do not expose raw Reachy SDK DTOs as the long-term wire contract. Define versioned, hardware-neutral messages.

Important identifiers:

- `EntityId`
- `EmbodimentId`
- `UserId`
- `SessionId`
- `TurnId`
- `EventId`
- `CommandId`
- `ToolCallId`
- `ArtifactId`

Capability advertisement examples:

```text
audio.input
audio.output
camera.snapshot
camera.stream
display.text
motion.look_at
motion.gesture
device.location
device.notification
```

Command examples:

- `SpeakAudio`
- `StopSpeaking`
- `LookAt`
- `PerformGesture`
- `CaptureImage`
- `SetAttentionTarget`
- `ShowText`

Event examples:

- `AudioStarted`
- `AudioFrame`
- `SpeechStarted`
- `SpeechStopped`
- `VideoFrameAvailable`
- `PersonTracked`
- `CommandCompleted`
- `EmbodimentHealthChanged`

Every command should have a deadline, cancellation semantics, idempotency key, and result. Motion commands should also carry a priority/lease so face tracking, conversational gestures, camera focus, and explicit user commands do not fight each other.

### Transport recommendation

Use the transport that fits the data plane:

- **gRPC bidirectional streaming** or binary WebSockets for control, events, and moderate-rate PCM/frame traffic on a trusted LAN.
- **WebRTC** for browser/mobile media and when adaptive real-time audio/video transport, NAT traversal, and built-in jitter handling matter.
- **HTTP** for configuration, artifact upload/download, queries, and job operations.
- **SignalR** for Blazor UI state updates, not as the canonical robot protocol.

Start with an outbound gRPC stream from Reachy to the desktop. Do not require the robot to expose an unauthenticated inbound API. Avoid base64 JPEGs inside JSON for a continuous camera stream.

## Portable memory and knowledge design

### One entity profile directory

All durable, user-owned state should live under one configurable profile root, for example:

```text
EntityHome/
  manifest.json
  identity/
    identity.md
    soul.md
    relationships.md
  data/
    entity.db
  artifacts/
    sha256/...
  indexes/
    vectors/...
  prompts/
  exports/
```

`manifest.json` should include profile ID, schema version, creation time, and compatible application version. Secrets do not belong in this directory; keep them in environment variables or the operating-system credential store.

### SQLite as the canonical portable store

SQLite is the right initial source of truth because it is local, transactional, inspectable, easy to back up, and easy to carry between machines.

Suggested logical tables:

- entities and identity versions
- users and relationships
- sessions, turns, and content items
- events and event deliveries
- tool calls and approvals
- jobs and scheduled tasks
- memories and memory revisions
- memory evidence/provenance
- artifacts and tags
- embodiment registrations
- model calls and usage summaries

Use SQLite FTS for exact and lexical retrieval. A vector index can be a derived cache whose records reference stable memory IDs. Do not make a hosted vector store the only copy of knowledge.

OpenAI File Search can still be offered as an optional derived index for selected document collections. The portable profile remains authoritative and must be able to recreate or replace that hosted index.

### Memory is not raw chat history

Use several layers:

1. **Event journal**: append-only record of what happened.
2. **Conversation transcript**: user/assistant/tool content with timestamps and provenance.
3. **Episodic summaries**: bounded summaries of sessions or activities.
4. **Semantic memories**: facts, preferences, commitments, and relationships extracted with evidence.
5. **Procedural memory**: user-approved habits and workflows.
6. **Working context**: small, temporary context assembled for one turn.

Every derived memory should carry:

- source event/turn references
- who asserted it
- confidence
- created and last-confirmed times
- sensitivity and retention class
- superseded/deleted state

The system must support “show me what you remember,” correction, source inspection, and deletion. A model should not silently convert guesses into durable facts.

### Moving between laptop and desktop

The first migration workflow should be explicit and reliable:

```text
reachctl profile export --output entity-profile.rte
reachctl profile import entity-profile.rte
```

Export should:

- create a consistent SQLite backup through the SQLite backup API or `VACUUM INTO`
- include content-addressed artifacts and persona files
- include a checksum manifest
- optionally encrypt the archive with a user-held passphrase/key
- exclude credentials and ephemeral caches

Do not copy a live SQLite database plus WAL files through a generic cloud-sync folder and hope conflict resolution works. For the first version, enforce one active writer and use stop/export/import/start. Later, add an append-only sync log or a real replication design if simultaneous multi-host operation becomes a requirement.

## Modern OpenAI integration recommendations

These recommendations reflect the official OpenAI documentation available on 2026-07-18. Model availability is account- and region-dependent and should be checked during deployment.

### Text and reasoning

The current config uses `gpt-5.4` with `gpt-5-mini` fallback. Current OpenAI guidance identifies the GPT-5.6 family as the latest general model family:

- `gpt-5.6` / `gpt-5.6-sol` for frontier quality
- `gpt-5.6-terra` for a quality/cost balance
- `gpt-5.6-luna` for efficient, high-volume work

Do not simply replace every string with the largest model. Add model profiles and evaluate representative tasks:

- interactive tool reasoning: `gpt-5.6-terra` or `luna`, low/medium reasoning
- hard, user-requested deep work: `gpt-5.6-sol`, higher reasoning or pro mode when justified
- background bounded work: Responses background mode plus webhook/job completion
- high-volume classification/extraction: the least expensive model that passes structured-output evals

Use the Responses API for new text/vision/tool workflows. Extend the current request model to support explicit reasoning controls, `text.verbosity`, storage policy, metadata, safety identifiers, background mode, and structured outputs as needed.

### Live voice

The current Realtime model is `gpt-realtime-1.5`. Current OpenAI guidance recommends `gpt-realtime-2.1` for a low-latency voice agent, with `gpt-realtime-2.1-mini` as the lower-cost profile.

For Reachy:

- keep server-side WebSocket transport when the Entity Host owns raw PCM
- use WebRTC for phone/browser clients that capture and play audio directly
- begin with low reasoning effort for voice latency
- keep tool execution and business policy outside the audio transport layer
- implement precise playback truncation for WebSocket barge-in
- migrate fully to the GA Realtime event/session shapes when upgrading the SDK

The project uses `OpenAI` package 2.1.0 and suppresses `OPENAI002`, while current docs describe GA Realtime shapes and newer event names. Treat the SDK/API upgrade as a small migration project with recorded-event tests, not a model-string edit.

### Chained voice

Keep the chained pipeline as a first-class option.

Recommended components:

- `gpt-4o-transcribe` for higher-quality bounded transcription
- `gpt-4o-mini-transcribe` for a faster/cheaper profile
- `gpt-4o-transcribe-diarize` for desktop meeting or multi-speaker ingestion
- `gpt-4o-mini-tts` for speech generation

For TTS, the official guide currently recommends `marin` or `cedar` for best built-in quality. The code currently stores `GeneratedSpeechVoice`, an SDK enum, and defaults to `alloy`. Replace the domain setting with a provider-neutral voice descriptor/string so new built-in and eligible custom voice IDs do not require code changes.

Use PCM or WAV streaming to begin playback before the complete speech response arrives. The current chained path waits for a complete WAV byte array before queueing playback, leaving avoidable latency.

The product must clearly disclose that generated speech is AI-generated, as required by the OpenAI TTS policy guidance.

### Vision

Use two separate vision lanes:

- **local perception lane**: face/person/object tracking and other control-loop signals
- **semantic vision lane**: on-demand OpenAI image understanding through Responses

The current structured image content path is a solid base for semantic snapshots. Default routine snapshots to `detail=low` and raise detail only when the task needs text or fine spatial information. Keep original images in the local artifact store when retention is permitted; send only the needed resized view to a provider.

### Hosted tools and Agents SDK

OpenAI's Responses API offers built-in and remote tools, including File Search and remote MCP. The Agents SDK also supplies orchestration, handoffs, tracing, and voice helpers in its supported languages.

The .NET application should not be rewritten in Python or TypeScript only to adopt the Agents SDK. Keep the domain and durable runtime provider-neutral. It can call an MCP server or a separate specialist service when a hosted capability is genuinely useful.

## Always-on behavior and a sense of continuity

The “alive” feeling should come from durable mechanisms, not pretending the model is continuously conscious.

Add these Entity Host services:

- `SessionLane`: serializes work for a user/session
- `EventInbox`: records external and background events
- `HeartbeatService`: periodically decides whether anything deserves attention
- `SchedulerService`: reminders and scheduled jobs
- `PresenceService`: knows which embodiments/users appear available
- `JobService`: tracks long-running model/tool work
- `MemoryMaintenanceService`: summarizes, embeds, expires, and compacts memories
- `NotificationRouter`: selects phone, desktop, Reachy, or silent UI delivery

A no-op heartbeat must remain silent. Proactive speech should require relevance, user preferences, quiet hours, presence, and an active embodiment lease.

Continuity also needs one stable identity across sessions. OpenAI conversation IDs and Realtime sessions are execution details, not identity keys.

## Safety, security, and privacy

This design will hold more sensitive information than the current robot demo. Build the controls with the memory system rather than adding them later.

Minimum requirements:

- outbound authenticated embodiment connections
- TLS even on LAN when practical, with device enrollment and revocation
- no OpenAI key on Reachy once the desktop host exists
- per-tool approval policy for external writes, purchases, messages, and device actions
- local motion limits and stop behavior independent of the model
- visible camera/microphone and recording state
- explicit opt-in for face recognition and person-specific memory
- retention classes for audio, images, transcripts, and derived memories
- encryption for exported profiles and sensitive artifacts
- an audit trail for model calls, tools, memory changes, and proactive notifications
- stable privacy-preserving safety identifiers for provider requests when appropriate

Avoid raw continuous audio/video retention by default. Persist references, derived events, and user-selected artifacts; retain raw media only when a defined feature needs it and the user has opted in.

## Suggested solution structure

An incremental target could be:

```text
dotNet/
  ReachTether.Contracts/
    Versioned wire DTOs and capability descriptors

  ReachTether.Core/
    Entity/session/turn/event models
    Prompt composition
    Tool registry abstractions
    Model workload profiles
    No Reachy, ALSA, OpenAI SDK, ASP.NET, or UI dependency

  ReachTether.Memory/
    SQLite persistence, FTS, artifacts, retrieval, export/import

  ReachTether.OpenAI/
    Responses, Realtime, transcription, TTS, embeddings

  ReachTether.EntityHost/
    Worker/API host, jobs, heartbeat, scheduler, embodiment registry

  ReachTether.Desktop/
    Blazor UI and desktop embodiment adapters

  ReachTether.Embodiments.Abstractions/
    Audio/video/control/health abstractions

  ReachTether.Embodiments.ReachyMini/
    Reachy SDK, motion policies, camera and device adapters

  ReachTether.ReachyNode/
    Small on-device host and connection client

  ReachTether.Audio/
  ReachTether.Audio.Alsa/
  ReachyMini.Sdk/
  ReachTether.Tests/
```

This is a dependency direction, not a requirement to create every project immediately. Begin with `Core`, `Contracts`, and the Reachy embodiment adapter. Split other projects only when code has a real boundary.

## Migration roadmap

### Phase 0: Stabilize and characterize the current runtime

- Complete or isolate the in-progress face-tracking work.
- Add tests around current prompt, tool, Realtime event, interruption, and motion-arbitration behavior.
- Record representative Realtime event streams for regression tests.
- Turn Responses body logging off by default.
- Establish model/voice smoke tests that can be run explicitly, not during every unit test.

Exit condition: existing Reachy behavior can be changed with confidence.

### Phase 1: Extract the portable core

- Add entity/session/turn/event IDs and provider-neutral records.
- Introduce the shared tool registry and move `CameraTool` behind it.
- Extract prompt composition and personality session state.
- Replace the SDK voice enum in domain configuration with a voice descriptor.
- Split `OpenAiTransport` by capability.
- Add a fake embodiment.

Exit condition: a text/voice entity can run on a developer PC without constructing `ReachyMiniClient`.

### Phase 2: Isolate Reachy as an embodiment

- Move Reachy startup, wake/sleep, status, motion, camera, and ALSA adapters behind embodiment interfaces.
- Extract a shared conversation runtime used by chained and Realtime executors.
- Separate media/control interfaces from `IReachySession`.
- Keep an in-process mode temporarily so behavior remains deployable during extraction.

Exit condition: core code has no dependency on the Reachy SDK or ALSA.

### Phase 3: Add the desktop Entity Host and network node

- Create the host service and embodiment registry.
- Add authenticated outbound Reachy-to-host control/event streaming.
- Move OpenAI credentials and provider calls to the desktop by default.
- Proxy audio and semantic snapshot operations.
- Add a minimal UI for connection, transcript, model call, tool call, and camera artifact status.

Exit condition: Reachy can act as a thin embodiment of an entity running on the desktop.

### Phase 4: Add portable memory

- Add SQLite session/event/tool/artifact persistence.
- Add FTS retrieval and evidence-backed memory extraction.
- Add user inspection, correction, and deletion.
- Add profile export/import with checksums and optional encryption.
- Treat vector indexes as rebuildable caches.

Exit condition: the entity can move desktop -> laptop -> desktop without losing identity, sessions, or memories.

### Phase 5: Modernize model and voice profiles

- Upgrade and migrate the Realtime integration to the current GA API/SDK shape.
- Add current Realtime full/mini profiles.
- Add GPT-5.6 reasoning profiles and explicit reasoning/storage settings.
- Add streaming TTS and configurable modern/custom voices.
- Add evals for latency, interruption, tool success, personality, and memory grounding.

Exit condition: models can be upgraded through profiles and evals without architectural changes.

### Phase 6: Add background agency and more embodiments

- Add jobs, scheduler, event inbox, heartbeat, and notification routing.
- Add a desktop embodiment.
- Add a phone client/embodiment.
- Add optional MCP and hosted tool integrations.
- Add multi-embodiment presence and output leases.

Exit condition: the entity remains coherent and useful when Reachy is asleep or disconnected.

## Near-term backlog, ordered by leverage

1. Create `ReachTether.Core` with entity/session/turn/event and model-profile types.
2. Create a generic tool registry and migrate the camera tool in both pipelines.
3. Add a fake embodiment and run the conversation core without Reachy.
4. Split `OpenAiTransport` into Responses, Realtime, transcription, and speech services.
5. Extract shared startup/session/personality/output policy from both orchestrators.
6. Define the portable profile directory and SQLite schema before building the desktop UI.
7. Implement profile export/import before live multi-machine sync.
8. Create a Reachy embodiment adapter and then a separate Reachy node process.
9. Upgrade Realtime using recorded-event tests.
10. Replace cloud face tracking with a local detector; retain cloud vision for semantic queries.

## Decisions to make explicitly

These choices do not block the first extraction phase, but should be recorded before their feature area is implemented:

- Is the desktop/laptop host expected to work fully offline except for model calls?
- Is only one Entity Host active at a time, or is live multi-host synchronization required?
- Which memories may be learned automatically, and which require confirmation?
- What raw audio/video retention, if any, is acceptable?
- Should Reachy retain a small local voice fallback when the host/network is unavailable?
- Is the phone initially only a client/embodiment, or must it host the full entity?
- Which external actions always require approval?

My recommended defaults are: local-first host, one active writer, no raw media retention, confirmation for sensitive durable memories, a small safe Reachy fallback, phone-as-client first, and explicit approval for externally consequential actions.

## What not to do

- Do not put the canonical memory in OpenAI conversation state or one hosted vector store.
- Do not make the Blazor UI the domain layer.
- Do not add every new tool directly to both orchestrators.
- Do not run cloud vision in a motor-control feedback loop.
- Do not expose the Reachy SDK as the cross-device protocol.
- Do not start with microservices, Kafka, a graph database, or active-active sync.
- Do not equate a model session with the entity's identity.
- Do not let remote model output bypass local actuator safety and arbitration.

## Bottom line

ReachTether's current code is a good Reachy-centered prototype with several strong local abstractions. It is not yet a portable entity platform because the durable self, AI provider integration, tools, conversation policy, and hardware lifecycle all live in the robot executable.

The right evolution is incremental:

1. extract a hardware-neutral core and shared tool/session contracts;
2. turn Reachy into an embodiment adapter and then a thin node;
3. move the enduring entity into a desktop/laptop host;
4. make its profile and memory locally owned, inspectable, exportable, and provider-independent;
5. modernize OpenAI models and voice behind workload profiles and evals;
6. add always-on behavior and other embodiments only after identity and persistence are stable.

If those boundaries are established, Reachy Mini becomes a particularly charming body for the entity rather than the place where the entity is trapped.

## Official OpenAI references

- [Latest model guidance](https://developers.openai.com/api/docs/guides/latest-model)
- [Responses API migration and capabilities](https://developers.openai.com/api/docs/guides/migrate-to-responses)
- [Realtime and audio overview](https://developers.openai.com/api/docs/guides/realtime)
- [Voice agent architectures](https://developers.openai.com/api/docs/guides/voice-agents)
- [Managing Realtime conversations](https://developers.openai.com/api/docs/guides/realtime-conversations)
- [Speech to text](https://developers.openai.com/api/docs/guides/speech-to-text)
- [Text to speech](https://developers.openai.com/api/docs/guides/text-to-speech)
- [Vision and image inputs](https://developers.openai.com/api/docs/guides/images-vision)
- [File Search](https://developers.openai.com/api/docs/guides/tools-file-search)
