# Architecture Review & Entity Roadmap — July 2026

**Scope:** full codebase review, model/API modernization (verified against the OpenAI platform as of 2026-07-18), and an architecture direction for the long-term goal: *an AI entity with persistent, growing memory, where Reachy Mini is one embodiment among several (desktop, phone, other hardware)*.

> **⚠ Status audit appended 2026-07-18 (same day, post-recovery).** This review was written against a working tree that was **missing the March 2026 laptop sprint** (`a32aac1`…`8ac3de5`: ReachTether.Server, SQLite memory + profiles, robot tool router, snapshot publishing) and **including the vision/face-tracking WIP** that has since been parked on `wip/vision-head-tracking`. Both facts invalidate specific findings below — several "missing" things exist, and several "existing" things live only on the parked branch. Corrections and a full implemented-vs-planned audit are in **§10**; read it before acting on §2 or §8. Inline notes marked *(audit)* flag the claims that changed.

**Relationship to prior docs:** this builds on the v1.1 consensus (`v1.1.md`, `v1.1-implementation-plan.md`, the four model reviews, `orchestrator-refactoring-proposal.md`, `realtime-orchestrator-refactoring-plan.md`) rather than re-deriving it. The two-runtime split, generic tool router, JPEG-over-SignalR video, layered personality, and server-side SQLite memory are treated as **decided**. This doc adds: (1) urgent findings from the current code, (2) a verified July-2026 model/API picture and migration plan, (3) the multi-embodiment abstraction the prior docs don't cover, and (4) a migratable knowledgebase design, plus recommendations for the open questions the prior docs left unresolved.

---

## 1. Executive summary

1. **The realtime voice stack is running on a removed API surface.** The robot uses the beta `RealtimeConversationClient` from OpenAI .NET SDK **2.1.0**. OpenAI removed the Realtime API **beta** on **May 12, 2026** and the GA protocol renamed the core events (`response.audio.delta` → `response.output_audio.delta`, etc.). Migrating to the GA realtime surface is the single most urgent engineering task — and it also *unlocks* several v1.1 features for free (async function calls, out-of-band responses, MCP tools, native image input). See §4.
2. **Model config has been refreshed, but `auto` retains a foot-gun.** *(audit: `VoicePipeline` is explicitly `"realtime"`, chained chat uses `gpt-5.6-luna@low`, and realtime voice uses `gpt-realtime-2.1`; however, the substring mechanism at `RobotAppOptions.cs:307-321` is unchanged. See §10.1.)* Under `VoicePipeline: "auto"`, pipeline selection still silently keys off the substring `"realtime"` in the chat model name. See §3.
3. **Face tracking uses a hosted LLM as a per-frame detector** (`OpenAiHeadDetector` → `gpt-4o-mini` via `/v1/responses`, `detail:low`, ~5 Hz target). *(audit: this entire stack lives on the parked `wip/vision-head-tracking` branch, not main — main has no head detector at all; vision is on-demand via the `camera` tool only. See §10.1.)* That's a network round-trip and billed vision request per frame for a job a local ONNX model does in <10 ms, free, offline. Swap it behind the existing `IHeadDetector` seam. See §2.3.
4. **The portable "entity core" already exists** — it's just entangled. All of `ReachTether.Audio`, the vision geometry/control math, the gesture DSP (`SwayRollRt`, `TalkingGestureSource`), personality, and the conversation logic are hardware-agnostic. Exactly three things bind the app to Reachy/Linux: the GStreamer camera fused into the SDK, the concrete ALSA audio binding, and ~50 lines in `MotionOrchestrator.SendTargetAsync`. Isolating these is a modest, well-bounded refactor, not a rewrite. See §6.
5. **The knowledgebase should be a file-first "entity home" directory** — markdown as source of truth, SQLite (FTS5 + vector) as a rebuildable index, transcripts as append-only raw material. Migration between desktop and laptop is then literally a folder copy/sync. Do **not** anchor long-term memory in OpenAI's hosted Conversations API. See §7.
6. **Stay provider-agnostic at three seams.** OpenAI has the best realtime stack today, but the design should allow swapping to (a) xAI's Grok Voice API — which is *wire-compatible* with the OpenAI Realtime API, so the realtime adapter must take its base URL/model from config, never hardcode `api.openai.com`; (b) "chat model + STT + TTS" stacks, including Anthropic's Claude (text-only API — no speech modality, so Claude support *requires* the chained pipeline) and local/on-device engines (whisper.cpp, sherpa-onnx/Piper). This promotes the chained pipeline from "legacy fallback" to the portability layer. See §5.

---

## 2. Codebase review

### 2.1 What's working well

- **The right seams already exist on the input side.** `ICameraSource` / `ICameraSnapshotProvider` / `IHeadDetector` / `ILookAtProjector` / `IMotionOrchestrator` are DI-registered interfaces, and `MotionOffsets` is a clean hardware-agnostic currency between gesture generation, face tracking, and actuation. The face-tracking stack correctly implements the four-loop decoupled design from `face-tracking.md` (camera 15 Hz → perception 5 Hz → control 50 Hz, latest-frame-wins). *(audit: the face-tracking loop, `IHeadDetector`, and `ILookAtProjector` are on the parked `wip/vision-head-tracking` branch only; on main the `FaceTrackingEnabled` config knob is a dead option with no consumer.)*
- **Audio plumbing is sound:** bounded channels with drop-oldest backpressure (`AudioCaptureService`, `AudioPlaybackService`, `BoundedAudioFrameQueue`), adaptive-noise-floor VAD, barge-in via `SpeechBoundaryHandler`, streaming playback for realtime audio. This matches the `architectural-findings-and-roadmap.md` prescriptions.
- **The realtime event-handler decomposition has started** (`IRealtimeEventHandler` + `Realtime/Handlers/*`), which is the Phase-A skeleton of `realtime-orchestrator-refactoring-plan.md`.
- **No IK anywhere** — motion works purely in head-pose offset space and delegates kinematics to the Reachy daemon. That is exactly the right abstraction level for multi-embodiment.

### 2.2 Urgent issues (fix before building v1.1 features)

| # | Issue | Where | Impact |
|---|---|---|---|
| 1 | Realtime stack targets the **removed beta** protocol (SDK 2.1.0 `RealtimeConversationClient`, beta event names) | `ReachTether.Robot.csproj`, `RealtimeInteractionOrchestrator.cs`, all of `Realtime/Handlers/` | Voice pipeline is broken or on borrowed time; blocks every realtime-dependent v1.1 feature |
| 2 | `auto` pipeline mode selects legacy for `ChatModel: "gpt-5.4"` (substring `"realtime"` check) | `RobotAppOptions.cs:297-311` | Silent selection of the less-developed pipeline; set `VoicePipeline` explicitly and key auto-detection off `Realtime:Model` presence instead |
| 3 | Per-frame LLM head detection | `Vision/OpenAiHeadDetector.cs:44-56` | Cost, 300–1000 ms latency (real rate ≪ 5 Hz), no offline operation |
| 4 | `Diagnostics.LogResponsesApiBodies: true` shipped on | `appsettings.json:60`, `OpenAiTransport.cs:433-448` | Full request bodies (incl. sanitized-but-large image payloads) into debug logs on a 16 GB-flash device |
| 5 | Stale fallback models (`whisper-1`, `gpt-4o-mini` defaults in code diverge from config) | `OpenAiTransport.cs:156-178`, `RobotAppOptions.cs` defaults | Fallback paths land on deprecated/superseded snapshots |

*(audit: #1 and #4 confirmed still true on main as of 2026-07-18 — the beta realtime SDK and `LogResponsesApiBodies: true` are both unchanged. #2's premise changed again: `VoicePipeline` is explicitly `"realtime"`, `ChatModel` is `"gpt-5.6-luna@low"`, and `Realtime:Model` is `"gpt-realtime-2.1"`; the substring check (`RobotAppOptions.cs:307-321`) remains a foot-gun only for `auto`. #3 describes the parked vision branch; main has no per-frame detector to replace. #5 now applies only to the remaining transcription/speech fallback defaults; chat and fallback-chat defaults are aligned on `gpt-5.6-luna@low`.)*

### 2.3 Structural cleanups, ranked by leverage

1. **Extract the shared turn scaffolding out of the two orchestrators.** The wake-up sequence, ALSA connect, pose choreography constants, personality-switch handling, farewell/shutdown-intent regexes, `Deg()` helper, and shutdown paths are duplicated verbatim (`InteractionOrchestrator.cs:338-477` vs `RealtimeInteractionOrchestrator.cs:803-867`). The gpt-5.4 review's framing is right: build a shared `ConversationCoordinator` / turn engine and make legacy vs realtime *transport adapters*. This also collapses issue-list items in `orchestrator-refactoring-proposal.md`.
2. **Move DSP out of the realtime orchestrator and out of the ALSA project.** `RealtimeInteractionOrchestrator.cs:647-790` (downmix/resample/level metering) and `LocalAudioSession.cs:435-537` (channel adjust/resample) are portable audio math trapped in the wrong assemblies — both belong in `ReachTether.Audio`. This is a prerequisite for any second audio backend (Windows/WASAPI).
3. **Fix the audio abstraction so it's actually used.** The realtime pipeline binds to the **concrete** `LocalAudioSession` (`Program.cs:85`; consumers `AudioCaptureService.cs:22`, `RealtimeTurnContext.cs:13/42`, `StreamingAudioHandler.cs:34-40`) because the streaming-playback methods (`BeginPlaybackStream` / `WritePlaybackPcm16Chunk` / …) are not on `IReachySession`. Extend the interface (or better: define a new `IAudioDevice` in `ReachTether.Audio` — `IReachySession` living in the WebRtc assembly is mislayered anyway) and switch consumers to it.
4. **Deduplicate the Responses API DTOs.** `ResponsesRequest`/`ResponsesInputItem`/content-part records exist twice (`OpenAiTransport.cs:918-943` and `OpenAiHeadDetector.cs:48-60`). *(audit: the `OpenAiHeadDetector` copy is branch-only; on main the dedup target is `OpenAiTransport` alone — still worth doing when the provider seams land, §5.2.)*
5. **Wire or remove the dead `"ReachTether.Server"` HttpClient** (`Program.cs:81`). *(audit: **wrong — this is done.** The March sprint wired it fully: typed `IReachTetherServerClient` with seven endpoints, `ServerSessionCoordinator`, per-turn hydration and persistence in both orchestrators, graceful degraded mode when the server is unreachable. See §10.2.)*
6. **Harden `InteractionStateMachine`** — plain non-volatile field mutated from orchestrator, handlers, and barge-in paths; no legal-transition enforcement. Cheap fix (lock or `Interlocked` + transition table), and the always-on work (`Sleeping`, `HeartbeatCheck` states per the Claude review) will need a real state machine anyway.
7. **Stop parsing config twice** (`RobotAppOptions.FromConfiguration` called at `Program.cs:24` and `:34`) and consider splitting the 35-knob `VisionSettings` into `Vision` + `FaceTracking` sections.
8. **Tests for the pure math.** `HeadTrackingController` (hysteresis, deadband, rate limiting), `PinholeLookAtProjector`, and `SwayRollRt` are pure, highly testable, and currently untested — they're also the code most likely to regress subtly during the multi-embodiment refactor. The existing `ReachTether.Tests` project (6 facts) is the place.
9. **Personality catalog hygiene:** the `example` personality advertises a nonexistent `sweep_look` tool (`personalities.json:126`). When the tool registry lands (v1.1 Phase 1), validate personality tool allowlists against registered tools at load time.

---

## 3. Model landscape and recommended matrix (verified July 2026)

### 3.1 What's current

| Model | Released | Notes |
|---|---|---|
| `gpt-realtime-2.1` / `gpt-realtime-2.1-mini` | Jul 6, 2026 | Latest speech-to-speech; ≥25% p95 latency cut, better noise/silence handling, configurable reasoning effort, tool use; mini is a fast/cheap reasoning variant |
| `gpt-realtime-2`, `gpt-realtime-translate`, `gpt-realtime-whisper` | May 2026 | Realtime GA family; `-whisper` is streaming STT with controllable latency |
| GPT-5.6 family (Sol / Terra / Luna) | Jul 9, 2026 | Frontier / balanced / efficient; programmatic tool calling, explicit prompt-cache controls |
| GPT-5.5 / 5.5 Pro | Apr 24, 2026 | 1M-token context |
| GPT-5.4 / 5.4 Pro / mini / nano | Mar 2026 | Built-in computer use + tool search; currently configured `ChatModel` |
| **Removed:** Realtime API **beta** | May 12, 2026 | The surface this codebase uses |
| Superseded: `gpt-4o-mini-tts` / `gpt-4o-mini-transcribe` undated | — | Use dated snapshots (2025-12-15 preferred) or `gpt-realtime-whisper` |

### 3.2 Recommended model matrix for ReachTether

| Role | Model | Rationale |
|---|---|---|
| Robot voice (primary) | `gpt-realtime-2.1-mini` | Latency + cost for always-on ambient use; it's a reasoning-capable mini |
| Robot voice (quality mode) | `gpt-realtime-2.1` | Personality fidelity, better tool use; make it a per-personality or config switch |
| Deep think / SmartyMode (server) | GPT-5.6 **Terra** (balanced) — Sol for explicit "research" asks | Resolves the open decision in the prior docs (`o1-preview`/`o3`/`gpt-5.4` candidates). Invoked via async function call, latency tolerated |
| Memory consolidation / summarization (server, background) | GPT-5.6 **Luna** or `gpt-5.4-mini` | High-volume, cheap, quality less critical |
| Legacy-pipeline STT | `gpt-realtime-whisper` (or dated `gpt-4o-mini-transcribe-2025-12-15`) | Streaming captions become possible for the desktop UI |
| Legacy-pipeline TTS | dated `gpt-4o-mini-tts` snapshot | Keep chained pipeline as fallback only |
| Face/head detection | **local ONNX** (YuNet via OpenCvSharp, or YoloDotNet) — *not an LLM* | `face-tracking.md` already recommends this; `IHeadDetector` is the seam |
| Face *recognition* (who is it) | server-side, local embedding model on the Mind | Resolves the improvements.md open question: off-device |

Config change to support this: per-role model selection — the long-term shape is the provider-qualified `Providers:` section in §5.4; as an interim step, `OpenAI:DeepThinkModel` / `OpenAI:ConsolidationModel` keys plus a `CompleteChatWithModelAsync` overload on `IOpenAiTransport` (already identified in the Claude review as the missing primitive) get the same capability without waiting for the seam refactor.

---

## 4. Realtime GA migration — the gateway task

Upgrade the `OpenAI` NuGet from 2.1.0 to the current 2.x and move from `OpenAI.RealtimeConversation` (beta, `#pragma OPENAI002`) to the GA realtime surface. Mechanics:

- **Session:** GA sessions are typed (`session.type: "realtime"`); ephemeral creds come from `POST /v1/realtime/client_secrets` (matters for phone/browser embodiments later); no `OpenAI-Beta` header.
- **Event renames** the handlers must absorb: `response.audio.delta` → `response.output_audio.delta`, `response.audio_transcript.delta` → `response.output_audio_transcript.delta`, `response.text.delta` → `response.output_text.delta`, etc. The `Realtime/Handlers/*` dispatch (`StreamingAudioHandler.cs:18,48`, `ResponseLifecycleHandler`, `TranscriptionHandler`, `SpeechBoundaryHandler`, `FunctionCallHandler`) maps ~1:1 onto new SDK update types — the ordered-dispatcher architecture survives intact.
- **Delete the raw-JSON image hack.** `CameraTool.BuildRealtimeImageMessageCommand` (`Vision/CameraTool.cs:97-125`) hand-crafts `conversation.item.create` with `input_image` because the beta SDK had no typed image items. GA realtime supports **image input natively**.

The GA features are not just parity — they map directly onto planned v1.1 features:

| GA realtime feature | v1.1 feature it unlocks |
|---|---|
| **Async function calling** | SmartyMode / `delegate_reasoning` / sub-agents without freezing the conversation — the model keeps talking while the server-side job runs. This replaces a chunk of the bespoke `ISessionControlService` pause/resume machinery planned in `realtime-orchestrator-refactoring-plan.md` |
| **Out-of-band responses** | Always-on heartbeats and system-event injection *without* polluting the conversation state — cleaner than the prefix-into-next-turn approach ported from OpenClaw |
| **MCP server support** | The Mind's remote tools can be exposed as an MCP server; the realtime session calls them directly. The `IToolRegistry`/allowlist layer is still wanted for governance, but transport comes free |
| **Reasoning effort** | Per-personality "thoughtfulness" knob; start `low` |
| **Context truncation/compaction** | Long always-on sessions without manual history pruning (legacy path's hand-rolled keep-last-12 at `InteractionOrchestrator.cs:230-237`) |

**Pipeline strategy:** speech-to-speech realtime is the primary embodiment pipeline for latency-sensitive voice. Keep the chained pipeline (VAD → STT → chat → TTS) as a first-class `ITurnExecutor` alongside it — not as a deprecated leftover, but as the **provider-portability layer** (§5): it is the only pipeline shape that works with providers that have no speech modality (Anthropic) and with local/on-device STT+TTS. Investment rule: the shared turn engine treats both shapes as equal transports; provider-specific code lives only in the adapters behind them.

---

## 5. Provider portability: don't marry the Realtime API

The goal is an entity that outlives any one vendor's product line. OpenAI keeps the voice loop today because its realtime models are genuinely ahead (out-of-band responses, live tool use, async function calls), but the architecture must make "swap the model provider" a config change plus one adapter, not a rewrite. Three seams accomplish that.

### 5.1 Provider landscape (verified July 2026)

| Provider | Voice capability | Chat/reasoning | Implication for ReachTether |
|---|---|---|---|
| **OpenAI** | Best-in-class S2S (`gpt-realtime-2.1`), plus STT/TTS models | GPT-5.4/5.5/5.6 via Responses API | Current default for both pipeline shapes |
| **xAI** | Grok Voice Agent API: realtime S2S over WebSockets, **compatible with the OpenAI Realtime API** — most OpenAI client libraries work by changing the base URL to `wss://api.x.ai/v1/realtime`. Models `grok-voice-think-fast-1.0` / `grok-voice-latest`; dedicated `grok-stt` / `grok-tts`; sub-second time-to-first-audio; 24+ voices (Ara, Eve, Leo + 21 added July 7, 2026) | Grok 4-class models | The realtime adapter must read base URL + model + key from config. Swapping to Grok voice is then nearly free — and is the cheapest possible *proof* that the abstraction works |
| **Anthropic** | **None** — no speech-to-speech or audio API. Claude (Opus 4.8, Sonnet 5, Haiku 4.5) is text + vision via the Messages API, with strong tool use, MCP support, and prompt caching | Frontier-tier; excellent for deep-think/consolidation roles | Claude support **requires** the chained pipeline (STT → Claude → TTS). Claude is also a drop-in candidate for the Mind-side deep-think and consolidation roles even while OpenAI keeps the voice loop |
| **Google** | Performant native audio (Gemini Live API: bidirectional streaming voice) plus STT/TTS | Gemini frontier-tier chat | **Not wire-compatible** with the OpenAI Realtime API — the Live API is its own WebSocket protocol, so voice support means writing and maintaining a dedicated `IRealtimeVoiceSession` adapter (Google's OpenAI-compatibility layer covers only part of the chat surface, not realtime voice). Capable, but the integration cost is real — deprioritized until there's a concrete reason to want Gemini specifically |
| **Local / open** | STT: whisper.cpp (Whisper.net bindings for .NET), sherpa-onnx streaming Zipformer, Vosk. TTS: Piper / Kokoro via sherpa-onnx (C# bindings, runs on ARM) | Any local or hosted chat model | The zero-marginal-cost, offline-capable stack. Pragmatically, "on-device" means **on the Mind's desktop** — robot-side only tiny models (whisper tiny/base) are feasible on the Reachy's compute |

The table implies a support tiering: **OpenAI and xAI come nearly in parallel for free** (same wire protocol, one adapter, config swap); Anthropic and local engines come with the chained pipeline; **Gemini is the only provider that would cost a whole new realtime adapter**, so it's supported-in-principle (the seam allows it) but not planned work.

Latency reality check: chained STT→chat→TTS adds roughly 1–3 s of turn latency versus sub-second S2S. That's acceptable for the desktop embodiment and for cost-sensitive ambient operation; it's noticeable on the robot. This is why both shapes stay first-class rather than one replacing the other.

### 5.2 The three seams

1. **`IRealtimeVoiceSession`** — the S2S loop (connect, send PCM, receive audio/transcript/tool events, inject items, cancel). The OpenAI GA migration (§4) should land *behind* this interface with `BaseUrl`, `Model`, `ApiKey` from config. Because xAI speaks the same wire protocol, one adapter covers both; a genuinely different future protocol gets its own adapter.
2. **Chained-pipeline seams** — `ISpeechToText` (streaming-capable), `IChatCompletion`, `ITextToSpeech`. Implementations: OpenAI, Anthropic (`IChatCompletion` only), xAI, Local (Whisper.net / sherpa-onnx). Crucially, `IChatCompletion` owns a **provider-neutral conversation model**: the planned `UserTurn`/`ConversationState` refactor (improvements.md) should define its own turn/content-part types, not mirror the Responses API shape. The hand-rolled Responses DTOs in `OpenAiTransport.cs:918-943` (and their duplicate in `OpenAiHeadDetector`) become private wire models inside the OpenAI adapter — this folds into cleanup §2.3 item 4. Message-format differences the adapter absorbs: system-prompt placement, tool-result encoding (OpenAI `function_call_output` items vs Anthropic `tool_result` content blocks), streaming event shapes, and provider caching hints (Anthropic `cache_control` breakpoints, OpenAI automatic prefix caching) — the hydration block (§7) is a stable prefix by design, so both providers' caching benefits apply.
3. **Tool-definition normalization** — the v1.1 `IToolRegistry` stores neutral definitions (name, description, JSON schema, allowlist); per-provider serializers emit OpenAI function format, Anthropic `tool_use` schema, or realtime session tools. Longer term, **MCP is the provider-neutral escape hatch**: OpenAI's realtime API, Anthropic's Messages API, and xAI all speak MCP, so exposing the Mind's tools as an MCP server makes the remote-tool surface provider-independent by construction. Keep the registry (for allowlists and local robot tools) but plan the Mind's tool endpoint as MCP-shaped.

### 5.3 What stays neutral by design

The knowledgebase rules in §7 already guarantee provider independence for the entity's *state*: identity and memory are markdown + SQLite, embeddings are rebuildable (so the embedding model is swappable — re-index, not migrate), and no vendor-hosted conversation store ever holds durable memory. The `SystemPromptBuilder` output is plain text, portable to any provider's system-prompt slot. The one discipline to enforce: never persist provider message IDs, `previous_response_id` chains, or realtime session state into the entity home — transcripts store *content*, in our own schema.

### 5.4 Config shape

Per-role provider selection replaces the current flat `OpenAI:` section over time:

```json
"Providers": {
  "Voice":        { "Shape": "realtime", "Provider": "openai", "Model": "gpt-realtime-2.1-mini" },
  "VoiceAlt":     { "Shape": "chained",  "Stt": "local:whisper-base", "Chat": "anthropic:claude-sonnet-5", "Tts": "local:piper-en" },
  "DeepThink":    { "Provider": "openai", "Model": "gpt-5.6-terra" },
  "Consolidation":{ "Provider": "openai", "Model": "gpt-5.4-mini" },
  "Embeddings":   { "Provider": "openai", "Model": "text-embedding-3-small" }
}
```

Each provider entry carries its own `BaseUrl`/`ApiKeyEnv` defaults (`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `XAI_API_KEY`), so an xAI voice swap is: change `Provider` and `Model`, set the key.

### 5.5 Sequencing — build seams now, adapters lazily

Don't build four adapters up front. Phase 1 defines the interfaces and config shape and *moves the existing OpenAI code behind them* (mostly mechanical, since the turn-engine refactor touches the same code). Then one alternate implementation serves as the proof: the **xAI base-URL swap is the cheapest test** of the realtime seam, and a **local STT/TTS pair on the Mind** is the most valuable test of the chained seam (it also unlocks offline/degraded operation and cuts always-on running costs). Anthropic chat lands whenever a role (deep-think, consolidation, or the desktop chat pane) wants it — it's just an `IChatCompletion` implementation.

---

## 6. Architecture: one Mind, many Bodies

### 6.1 The model

The v1.1 docs already split "robot edge runtime" from "server." Generalize that into the actual product concept:

```
                    ┌─────────────────────────────────────────┐
                    │   MIND (desktop server, ReachTether.Mind)│
                    │   identity · memory · knowledge · tools  │
                    │   sub-agents · scheduler · perception hub│
                    │   SignalR gateway + REST                 │
                    └───────┬──────────┬───────────┬──────────┘
                            │          │           │
                 ┌──────────┴──┐  ┌────┴─────┐  ┌──┴────────────┐
                 │ Reachy Mini │  │ Desktop  │  │ Phone (later) │
                 │ embodiment  │  │embodiment│  │ realtime      │
                 │ (edge, ALSA,│  │(WASAPI,  │  │ WebRTC +      │
                 │  GStreamer, │  │ webcam,  │  │ client_secrets│
                 │  motion)    │  │ Blazor UI)│ │ from Mind     │
                 └─────────────┘  └──────────┘  └───────────────┘
```

- An **embodiment** owns exactly: audio in/out, local camera(s), expression/motion output, and the low-latency realtime loop. It holds *no* durable state and *no* identity — it fetches session config (persona prompt, tool definitions, memory hydration) from the Mind at session start and streams events (transcripts, tool calls, snapshots) back.
- The **Mind** owns everything durable: the entity home (§6), tool execution routing, sub-agents, scheduling/heartbeats, perception sources of its own (desktop cameras — goal #1), and the operator UI.
- The entity's *continuity* lives in the Mind. Walking from the robot to the desktop is a session hand-off against the same memory, not a different agent.

### 6.2 Project restructuring

Current portable/hardware entanglement is limited to three walls (GStreamer camera fused into `CameraClient`, ALSA-concrete audio binding, `MotionOrchestrator.SendTargetAsync`). Target layout:

```
ReachTether.Entity.Core          — contracts & pure logic: MotionOffsets, AudioFormat/frames/WAV,
                                   tool registry interfaces, turn/session models, personality,
                                   tracking math (HeadTrackingController, PinholeLookAtProjector),
                                   gesture DSP (SwayRollRt, TalkingGestureSource), all DSP helpers
ReachTether.Embodiment.Abstractions — IAudioDevice (capture + streaming playback), ICameraSource,
                                   IExpressionSink, IEmbodimentInfo
ReachTether.Embodiment.ReachyMini — ReachyMini.Sdk, ALSA session, GStreamer camera source,
                                   MotionOrchestrator's SendTargetAsync tail, pose choreography
ReachTether.Embodiment.Desktop    — WASAPI/CoreAudio device, USB webcam source, avatar/no-op expression
ReachTether.Robot                 — thin host wiring an embodiment + the turn engine (shrinks a lot)
ReachTether.Mind                  — the server (v1.1 "ReachTether.Server", renamed to match the concept)
```

Key moves, in dependency order:

1. **`IExpressionSink`**: extract `MotionOrchestrator`'s blend/clamp/50 Hz loop into Core; the Reachy implementation is just `SendTargetAsync` (`MotionOrchestrator.cs:190-240`) + the Reachy-tuned focus offsets. A desktop embodiment maps `MotionOffsets` to an animated avatar or discards it. Keep the currency *low-level offsets* + a small set of *semantic cues* (nod, look-at, camera-focus) — semantic cues are what non-robot embodiments can honor meaningfully.
2. **`IAudioDevice`** in Core/Abstractions (per §2.3 item 3); ALSA and (new) WASAPI implement it. The `AdjustChannels`/`ResamplePcm16` helpers move to Core.
3. **Camera de-fusion**: `ICameraSource` already exists — implement it *outside* the SDK. `ReachyMiniClient.Camera` should become one implementation among several (`GStreamerCameraSource`, `WindowsWebcamSource`, later `KinectSource` on the Mind). The SDK's REST clients stay; camera transport leaves.
4. **`CameraTool` decoupling**: it currently reaches into `IMotionOrchestrator.HoldCameraFocusAsync` (`Vision/CameraTool.cs:52`) — express that as a semantic cue through `IExpressionSink` so the tool works on any embodiment.

### 6.3 Goal #1: off-device agent with desktop camera/video access

With the Mind in place this is not a special case — it's the Mind hosting its own perception:

- The Mind registers its own `ICameraSource` instances (USB webcam via OpenCvSharp/Media Foundation; Kinect for the planned `KinectShot` tool). Same interface the robot uses.
- A `PerceptionService` on the Mind runs the *same* portable detection stack (local ONNX detector + `HeadTrackingController`-style smoothing where relevant) over any registered feed, publishing tagged observations (`source: "desk-webcam"`, `source: "reachy-head"`) into the system-event queue and memory.
- Conversational access via one tool: `look(source?, question?)` — the router (v1.1 tool registry) executes it locally on the robot for the robot camera, remotely on the Mind for desktop feeds. Snapshots land in `artifacts/` with provenance and appear in the desktop UI timeline (already planned in v1.1).
- Robot video to the Mind: capped-FPS JPEG over SignalR, as the prior docs converged on. The dormant `ReachyWebRtcSession` is the eventual upgrade path (it already negotiates H264 receive), but it is Reachy-daemon-specific — keep it inside the ReachyMini embodiment package.

### 6.4 Recommendations for the previously open questions

| Open question (from prior docs) | Recommendation |
|---|---|
| Desktop UI → robot direct (LAN) or via server (WAN)? | **Via the Mind, always.** The Mind is the session broker; embodiments never talk to each other. LAN latency cost is negligible for UI traffic, and it keeps one auth/story |
| JPEG-over-SignalR vs WebRTC video | JPEG/SignalR now (consensus); revisit WebRTC only when phone embodiment needs it |
| RAG cadence | Hybrid, three lanes: identity/context block injected at session start (context providers per `realtime-orchestrator-refactoring-plan.md`) + `recall` tool on demand + *post-session* consolidation. No per-turn retrieval — latency and prompt-cache churn |
| Face recognition on- vs off-device | Off-device on the Mind (local embedding model, embeddings stored in the entity home). Robot sends crops, not decisions |
| Realtime/deep-think model targets | §3.2 matrix |
| Phone embodiment (gap in prior docs) | Thin client: mobile app gets an ephemeral key from the Mind (`/v1/realtime/client_secrets`), speaks OpenAI Realtime over WebRTC directly, mirrors events to the Mind's gateway for memory/tools. No robot-runtime code reuse required |

---

### 6.5 Build vs adopt: an OSS agent runtime (Hermes) as the Mind

Hermes Agent (Nous Research, Feb 2026, MIT) is a self-hosted agent runtime with exactly the Mind's feature list: persistent memory, a learn-and-reuse skills loop, scheduling, code/web/file tools, model-agnostic backends, and 16+ messaging channels. At ~188K GitHub stars it has more momentum than OpenClaw, whose patterns the v1.1 docs already mined. So the question "should ReachTether build adapters for Hermes instead of building a Mind" is legitimate — and the answer splits cleanly along the Mind/embodiment boundary:

**What Hermes cannot replace:** the embodiment layer. The realtime S2S voice loop — barge-in, VAD, streamed audio, motion sync, camera — is not what a messaging-first agent runtime does. Wiring Reachy in as a Hermes "channel" would give text-turn semantics, not sub-second voice. Everything in Sprints 1 and the `Embodiment.*` split stays ours regardless of this decision.

**What Hermes could replace:** the Mind's internals — memory, consolidation, skills, scheduler, deep-think orchestration. That's real leverage: it's the part of v1.1 with the most greenfield code, and a large community would be maintaining it.

**The costs to weigh:** (a) stack mismatch — the Mind boundary is network-shaped anyway, so a non-.NET Mind is tolerable, but debugging spans two ecosystems; (b) **memory-schema ownership** — the entity-home requirements (inspectable markdown source of truth, rebuildable indexes, folder-copy migration, no absolute paths) become "whatever Hermes's store does," and a project gaining 24K stars/week churns internals fast; (c) security surface — an autonomous runtime with shell access and messaging integrations on the desktop is a bigger attack surface than a three-endpoint service (OpenClaw's exposed-instance incidents are the cautionary tale); (d) personality layering and per-personality tool allowlists would have to map onto Hermes's own persona/skill concepts or be maintained as a fork.

**Decision approach — keep it reversible, then spike it.** Sprint 2's real deliverable is the **Mind API contract** (`GET /session-context`, `POST /transcripts`, `POST /tools/execute`): the robot only ever talks to that contract. Behind it, "hand-rolled SQLite + files" and "facade over Hermes" are interchangeable implementations. So: build the contract + hand-rolled v0 as planned (it's ~a day and the entity home files must exist in our format anyway), and in parallel timebox a 1–2 day spike standing Hermes up on the desktop to answer three questions: (1) can memory be injected/queried cleanly over its API at session-start latency? (2) is its memory storage inspectable and folder-migratable, or an opaque store? (3) do its skills/persona concepts carry the personality + allowlist model, or fight it? If the spike wins, the facade route means adopting Hermes costs days, not a rewrite — and abandoning it later costs the same.

---

## 7. Goal #2: the migratable knowledgebase ("entity home")

### 7.1 Design principle: files first, database as index

One directory *is* the entity. Copy the directory, you've moved the entity. Nothing inside it may contain absolute paths or machine names.

```
entity-home/
  entity.json               — schema version, entity name, created date
  identity/
    IDENTITY.md             — name, vibe, voice prefs (per OpenClaw findings)
    SOUL.md                 — durable tone/values/opinions
    personalities/          — situational overlays (successor of personalities.json)
  memory/
    MEMORY.md               — human-readable index, one line per fact file
    facts/*.md              — one durable fact per file, frontmatter: type/created/source/confidence
    people/*.md             — per-person profiles (+ face embedding refs)
  transcripts/
    2026-07-18-<session>.jsonl   — append-only raw turns, tagged with embodiment + location
  artifacts/
    snapshots/…             — camera captures with provenance sidecars
  index/
    knowledge.db            — SQLite: FTS5 + vector table (sqlite-vec) + metadata
    faces.db                — face embeddings
  locks/
    mind.lock               — single-writer guard (host name + pid + heartbeat timestamp)
```

Rules that make it migratable and durable:

1. **Markdown is the source of truth** for identity and distilled facts; humans (and other tools) can read and edit it. This is the "grows with me" surface — you can open SOUL.md in five years regardless of what happened to the code.
2. **Everything in `index/` is derivable.** Embeddings and FTS indexes rebuild from `memory/` + `transcripts/` via a `reindex` command. Corrupt or missing index ⇒ rebuild, never data loss. This also means embedding-model upgrades are a re-index, not a migration.
3. **Transcripts are append-only** and the raw material for consolidation; prune/archive by age policy, never rewrite.
4. **Single-writer:** exactly one Mind instance owns the home at a time (lock file with heartbeat; stale-lock takeover with prompt). Laptop↔desktop movement = sync the folder (Syncthing/OneDrive/git — user's choice, the design doesn't care) and start the Mind there. No multi-master merge in v1; the lock makes the failure mode explicit instead of silent corruption.
5. **Versioned schema** (`entity.json.schemaVersion`) with forward-only migrations run on Mind startup.

### 7.2 Memory pipeline

- **Capture:** every turn (any embodiment) streams to the current transcript; tool calls and snapshots recorded with provenance.
- **Consolidate:** background Mind job (session-end + nightly) runs the consolidation model (§3.2) over new transcript material: extract durable facts → `memory/facts/`, update `people/`, update `MEMORY.md`, embed + index. Include contradiction handling (supersede, don't duplicate) and decay (facts carry `lastReinforced`; stale low-value facts get archived).
- **Retrieve:** (a) session-start hydration block: identity + top-relevant facts for time/place/person present (the `IContextProvider` chain already planned); (b) `recall(query)` tool for explicit memory reaches; (c) `remember(fact)` tool for user-directed saves.
- **Edge cache:** the robot keeps only the hydration block it was handed — nothing durable on the 16 GB flash. If the Mind is unreachable, the robot runs with identity-only context and queues transcript upload (degraded but alive).

### 7.3 Why not vendor-hosted memory

The Conversations API (Aug 2025) and Responses server-side compaction are useful for *transport-level* session state, and fine to use per-session. But the entity's long-term memory must not live in a vendor's opaque store: it can't be synced to a laptop, inspected, edited, or migrated to a different model provider — all core requirements here. Use hosted conversation state as ephemeral plumbing; the entity home is the durable record.

---

## 8. Phased roadmap (revision of the v1.1 phase order)

**Phase 0 — Stabilize the foundation (do first, small):**
model config refresh + explicit `VoicePipeline` (§3), **GA realtime migration** (§4), local ONNX head detector behind `IHeadDetector`, kill the duplicated orchestrator scaffolding into a shared turn engine, DSP into `ReachTether.Audio`, tests for the pure math, flip `LogResponsesApiBodies` default off.

**Phase 1 — Tool router + Mind skeleton + provider seams (v1.1 Phase 1/2, extended):**
`IToolRegistry`/`IToolExecutor`/`IToolRouter` with per-personality allowlists and provider-neutral tool definitions; **define the provider seams** (`IRealtimeVoiceSession`, `IChatCompletion`, `ISpeechToText`, `ITextToSpeech` + the `Providers:` config shape, §5) and move the existing OpenAI code behind them — this rides along with the turn-engine refactor since it touches the same code; `ReachTether.Mind` host with SignalR gateway; wire the dead `"ReachTether.Server"` HttpClient into a typed client; entity home v0 (directory layout, transcripts capture, SOUL/IDENTITY split of personalities.json, SQLite FTS5 — no vectors yet).

**Phase 2 — Memory + deep think + desktop perception:**
consolidation job + `recall`/`remember` tools + session-start hydration; `delegate_reasoning` via GA **async function calling** to GPT-5.6 on the Mind; Mind-side `ICameraSource` (webcam/Kinect) + `look(source)` tool; JPEG/SignalR robot video in the UI.

**Phase 3 — Embodiment split + always-on + provider proof:**
extract `Entity.Core` / `Embodiment.*` projects (§6.2); desktop embodiment (WASAPI + webcam) as the second body — this is the forcing function that proves the hardware abstraction; **second-provider proof** for the model seams — the xAI base-URL swap (realtime seam, nearly free) and a local Whisper/Piper pair on the Mind (chained seam, unlocks offline mode and cuts always-on cost); always-on (don't-shutdown-on-goodbye, `Sleeping` state, heartbeats via out-of-band responses, scheduler).

**Phase 4 — Reach:**
phone embodiment (Realtime WebRTC + client secrets), face recognition + personalized greetings, vector search + people profiles maturity, WebRTC video if still wanted.

The ordering principle: *Phase 0 removes the rot the other phases would be built on; Phase 3's second embodiment is deliberately scheduled after memory exists, because an embodiment without the shared Mind is just another chatbot.*

### 8.1 Execution cut — memory-first revision (2026-07-18)

The phases above are dependency-ordered; this is the *sprint* order for actually shipping, pulling memory forward ahead of tools/video/sub-agents. Each sprint is sized to be one focused working session against this doc.

**Sprint 1 — unblock the voice loop (subset of Phase 0; prerequisite for everything).** *(audit status: **not started** — config unchanged (`VoicePipeline: "auto"`, `gpt-realtime-1.5`, `LogResponsesApiBodies: true`), still on OpenAI SDK 2.1.0 beta `RealtimeConversationClient`. This remains the top of the queue; see §10.4.)*
1. Config only, minutes of work: set `VoicePipeline: "realtime"` explicitly (kills the `auto`-substring routing foot-gun at `RobotAppOptions.cs:297-311`), move `Realtime:Model` to `gpt-realtime-2.1-mini`, update stale fallbacks, flip `LogResponsesApiBodies` off.
2. **GA realtime migration** — upgrade the `OpenAI` NuGet, absorb the event renames in `Realtime/Handlers/*`, delete the raw-JSON image hack in `CameraTool`. Land it *behind* the `IRealtimeVoiceSession` seam while the code is open; that's the only §5 work Sprint 1 needs.
3. Bug-class fixes only: transcript-gating on playback, session-reset context loss. **Defer** the full orchestrator dedup and the local face detector — extract only what the migration forces. (Face tracking can simply stay disabled; it already is in config.)

**Sprint 2 — memory MVP (the interesting part).** *(audit status: **largely shipped in the March 2026 sprint** — before this doc was written — but shaped differently than specified below: SQLite-only memory (no entity home / markdown), automatic per-turn promotion instead of a consolidation job, `memory_query`/`smarty_mode` tools instead of `recall`/`remember`, and per-turn retrieval where this doc said session-start-only. Full mapping in §10.2, divergences in §10.3.)* Deliberately skips SignalR/video/sub-agents; the Mind starts as a boring little HTTP service:
- **Entity home v0** on the desktop: directory layout, `SOUL.md`/`IDENTITY.md` split, `memory/facts/`, `transcripts/`, SQLite FTS5. No vectors yet.
- **Minimal Mind service** — three endpoints are enough: `GET /session-context` (returns the hydration block), `POST /transcripts` (append turn events), `POST /tools/execute` (`recall`, `remember`).
- **Robot side**: `SystemPromptBuilder` fetches the hydration block at session start; transcript events stream to the Mind; a minimal `IToolRegistry` with `remember`/`recall` (fold `camera` into it — that's the seed of the v1.1 tool router, built when first needed rather than as a standalone phase).
- **Consolidation job** on the Mind: session-end + nightly, cheap model (Luna / `gpt-5.4-mini`), extracting facts → markdown → index.
- Acceptance test: tell the robot a fact, reboot everything, it still knows; and the *same* fact is visible from a desktop text REPL against the Mind — that's the first two-embodiment moment.
- Optional parallel track: the 1–2 day **Hermes spike** (§6.5) — the Mind API contract built here is exactly what makes a later swap to a Hermes-backed Mind cheap.

**Sprint 3+** — deep-think via async function calling, desktop camera perception, then the Phase 3/4 order as written.

**How memory weaves into a realtime *audio* model** (the answer is: the same way it weaves into Claude — the realtime API's text surfaces are all still there):
- **Session start:** the hydration block goes into `session.instructions` — plain text, exactly what you'd put in a Claude system prompt.
- **Mid-session:** `conversation.item.create` injects text items into the live conversation (recall results, system-event context), and out-of-band responses let the Mind prompt the model without disturbing the audio turn flow.
- **Tools:** `recall(query)` / `remember(fact)` are ordinary function tools; with GA async function calling the model keeps speaking while the Mind does retrieval.
- **Capture:** the realtime API already emits user-input transcription and assistant-output transcript events — those are precisely what lands in `transcripts/`. Consolidation never touches audio; it always runs on a text model.
- The one real difference: realtime models are weaker readers than frontier chat models, and instructions are re-sent per session. So keep the hydration block compact (~1–2K tokens of identity + top-relevant facts), lean on the `recall` tool and deep-think for depth, and re-hydrate on every session reset (the reset path in `RealtimeInteractionOrchestrator` must call back into the context builder — worth an explicit test).

---

## 9. Decisions taken here (flag if you disagree)

1. `gpt-realtime-2.1-mini` default voice, `-2.1` as quality switch; GPT-5.6 Terra for deep-think; Luna/5.4-mini for consolidation.
2. All UI/embodiment traffic routes through the Mind (no LAN direct path).
3. Memory is file-first local (entity home), not vendor-hosted; SQLite is an index, not the source of truth; single-writer lock instead of sync merging.
4. Face detection local ONNX; face recognition Mind-side.
5. Both pipeline shapes are first-class: realtime S2S for latency-sensitive voice, the chained pipeline as the provider-portability layer (required for Anthropic and local engines). Provider-specific code lives only in adapters behind `IRealtimeVoiceSession` / `IChatCompletion` / `ISpeechToText` / `ITextToSpeech`.
6. Provider strategy: OpenAI stays the default for voice and deep-think; the realtime adapter takes base URL/model from config (xAI Grok voice is OpenAI-Realtime-compatible, so it's the designated portability test — OpenAI + Grok supported nearly in parallel at no extra effort); Claude is the leading alternative for Mind-side chat roles; local Whisper/Piper on the Mind is the cost/offline floor; Gemini is supported-in-principle via the seam but not planned work, since its Live API is a different wire protocol and would cost a dedicated adapter. Build seams in Phase 1, alternate adapters lazily.
7. "ReachTether.Server" renamed conceptually to **Mind**; robot runtime becomes one embodiment host among several.

## 10. Implementation status audit — 2026-07-18 (post-recovery)

**What happened:** the sections above were written against a tree missing the March 2026 laptop sprint (`a32aac1` "reachtether server + camera snapshots, Phase 1/2 of v1.1", `3a4df4c` "phase 3 memories", `8ac3de5` "memory retrieval + non-blocking turn persistence") and including the vision WIP now parked on `wip/vision-head-tracking`. This section audits today's `main` (plus the uncommitted camera/startup fixes) against the plan.

### 10.1 Corrections to the review sections

1. **The face-tracking stack is not on main.** `OpenAiHeadDetector`, `IHeadDetector`, `ILookAtProjector`, `HeadTrackingController`, `PinholeLookAtProjector`, and the four-loop design exist only on `wip/vision-head-tracking` (see the merge notes: hand-port its prompt-guidance changes into `PromptContextBuilder` when reviving — `ToolPromptAugmenter` no longer exists). On main, vision is on-demand only (`camera` tool → single snapshot), and `Vision:FaceTrackingEnabled` / `AmbientContextEnabled` are **dead config options** read into `RobotAppOptions` but consumed by nothing.
2. **The "dead server HttpClient" finding (§2.3 #5) is obsolete** — the server seam is fully wired (§10.2).
3. **Config facts:** `VoicePipeline` is explicitly `"realtime"`; `ChatModel` and `FallbackChatModel` are both `"gpt-5.6-luna@low"`; and `Realtime:Model` is `"gpt-realtime-2.1"`. The `@low` portion is a local compact-handle convention: the Responses API request builders send `model: "gpt-5.6-luna"` and `reasoning.effort: "low"` as separate fields. The substring foot-gun remains in `RobotAppOptions.cs:307-321` for `auto`. `Diagnostics:LogResponsesApiBodies` is still `true` (§2.2 #4 stands).
4. **Renamed/removed files referenced above:** `SystemPromptBuilder.cs` → `PromptContextBuilder.cs` (`IPromptContextBuilder`, registered in `Program.cs:100`); `ToolPromptAugmenter.cs` deleted; tool-usage guidance now comes from `ToolRouter` via `IToolDefinitionSource`.
5. **Several §2.3 line references are stale** (orchestrators grew server-integration code); treat them as approximate.

### 10.2 What the March sprint already delivered

| Planned (Sprint 2 / Phases 1–2) | Status | What actually exists |
|---|---|---|
| Minimal Mind service, 3 endpoints | ✅ **Done, bigger than planned** | `ReachTether.Server` (.NET 10 Blazor + minimal APIs, port 5057): `POST /api/sessions/start-or-resume` (≈ session-context), `POST /api/session-turns` (≈ transcripts), `POST /api/tools/execute`, plus `POST /api/knowledge/query`, admin memory endpoints (search/archive/restore/promote/reindex), `POST /api/snapshots` + `GET /artifacts/{id}/content` |
| SQLite FTS5 index (no vectors yet) | ✅ **Ahead of plan** | FTS5 (`memory_records_fts`) **and** embeddings (`memory_vectors`, JSON blobs, brute-force cosine in C#), hybrid retrieval fused 0.65 vector / 0.35 FTS with graceful FTS→LIKE fallback (`MemoryRetrievalService.cs`) |
| Transcript capture from robot | ✅ Done | Both orchestrators persist every turn (user/assistant text, tool calls, artifacts) via `TryPersistTurnAsync` — awaited in the turn loop but non-fatal on failure; server-side promotion runs fire-and-forget |
| Session-start hydration | ✅ Done | `ServerSessionCoordinator.StartOrResumeAsync` → active profile, session summary, last-6 turns, pending events; `PromptContextBuilder` assembles the system prompt; session reset / personality switch re-hydrates and (realtime) re-pushes via `ConfigureSessionAsync` |
| Minimal `IToolRegistry` with `remember`/`recall` + camera folded in | ✅ Done, different names | `ToolRouter` + `IToolRegistration` serving **both** pipelines (legacy 3-round loop; realtime `FunctionCallHandler`). Registered: `camera` (local), `memory_query`, `smarty_mode`, `scheduler` (server stub), `kinect_shot` (server stub). No `remember` tool — capture is automatic (see below). No per-personality allowlists |
| Consolidation job (session-end + nightly, cheap model) | ⚠️ **Different shape** | No batch job. Per-turn fire-and-forget promotion (`MemoryPromotionService`): LLM fact extraction (`gpt-5.6-luna@low`→`gpt-5-nano`, strict JSON schema, confidence-gated) with regex fallback; dedup/supersede; rolling session summary; embedding per record |
| — (not planned until Phase 4) | ✅ **Ahead of plan** | **People profiles** (`profiles` table, per-profile facts, profile summaries) with ambiguity/conflict handling via `pending_system_events` surfaced back into the prompt — this is a chunk of Phase 4's "people profiles maturity" already working |
| Deep think (planned Sprint 3, async function calling) | ⚠️ Partial | `smarty_mode` tool → server → OpenAI `/responses` with `gpt-5.4` — **synchronous**, blocks the turn; the async-function-calling upgrade still needs the GA migration |
| Snapshots in UI (planned Phase 2, JPEG/SignalR) | ⚠️ Partial | Snapshots upload as base64 HTTP POST → `FileSnapshotStore` (`data/snapshots` + manifest + `artifacts` table); read-only Blazor console (snapshot grid + profiles) polling every 2 s. **No SignalR anywhere** — no gateway, no video |
| Acceptance test ("tell it a fact, reboot, it still knows; visible from desktop") | ✅ Effectively met | SQLite persistence + hydration covers the reboot; the Blazor profiles page is the desktop view. `Server.Tests` cover store persistence, promotion (incl. ambiguity/conflict), retrieval (FTS + vector + archived), admin, schema upgrade |
| Degraded mode (robot alive without Mind) | ✅ Done | Server failures are non-fatal everywhere: local synthesized session, empty hydration, warnings only; `Server.Enabled=false` also disables remote tools. (Planned "queue transcript upload for later" is **not** implemented — offline turns are simply lost) |

Also delivered since (uncommitted, working tree): `RobotStartup.EnableMotorsAndWakeAsync` — shared startup extracted from both orchestrators, now enable-motors → verify → wake → poll-for-completion (fixes wake-while-disabled); `CameraClient` fixes (no forced framerate on the unix-socket feed, correct GStreamer teardown, original-exception preservation); `MotorsClient` snake_case mode mapping. Per `camera-and-startup-fixes.md`: code done, on-robot validation pending.

### 10.3 Divergences from the decisions in §9 (decide: amend the decision or schedule the work)

1. **Memory is SQLite-first, not file-first.** Decision #3 (entity home; "SQLite is an index, not the source of truth") is contradicted by the implementation: everything lives in `data/reachtether-server.db`, no markdown, no `SOUL.md`/`IDENTITY.md`, no rebuildable-index separation, no single-writer lock, paths relative to server content root. This is the **largest open divergence**. If the entity-home goal stands, the migration is: promotion writes fact markdown + `MEMORY.md`, SQLite becomes the derived index with a `reindex` command (the `/api/memory/reindex` endpoint is a seed), transcripts move to append-only files. Nothing in the current schema prevents this — but every week of accumulation raises the cost of the switch.
2. **Per-turn retrieval is implemented** — `knowledge/query` (hard-coded `topK=4`) runs on *every* turn, rebuilds the whole system prompt, and (realtime) re-pushes session instructions. §6.4 explicitly decided against per-turn retrieval on latency and prompt-cache-churn grounds. Either accept it (it works) or move to the decided hybrid: hydrate at session start + `recall` on demand. Note the re-push also defeats provider prefix caching every turn.
3. **Tool vocabulary differs:** `memory_query` not `recall`; no `remember` (automatic extraction instead — arguably better UX, but there's no user-directed "remember this" path); `smarty_mode` not `delegate_reasoning`, and it's synchronous.
4. **No per-personality tool allowlists** — `ToolRouter` filters only on global `IsEnabled`; personalities are never consulted (v1.1 Phase 1 requirement, still open).
5. **Some model IDs still predate the §3.2 matrix:** extraction is now `gpt-5.6-luna@low` with `gpt-5-nano` fallback and voice is `gpt-realtime-2.1`; smarty remains `gpt-5.4` and embeddings remain `text-embedding-3-small`. Future config refreshes must cover the **server's** `appsettings.json`, which §8.1 didn't anticipate.
6. **Embedding provider config is misleading:** preferred provider is `local` but `LocalMemoryEmbeddingProviderStub` throws and is disabled — in practice embeddings are OpenAI-only, and without `OPENAI_API_KEY` the system silently degrades to FTS/LIKE. (The local-embeddings slot is exactly where the §5 local-stack work plugs in.)

### 10.4 Gap list — what's still missing or incomplete

Ordered roughly by the §8.1 sprint logic:

1. **GA realtime migration (Sprint 1 — unchanged, still #1).** `OpenAI` 2.1.0, `#pragma OPENAI002`, `RealtimeConversationClient`, typed beta `ConversationUpdate` handlers, and the raw-JSON `input_image` hack in `CameraTool.cs:116-144` are all still in place. Everything in §4 stands.
2. **Model/config refresh (Sprint 1)** — now includes the server side (§10.3 #5) and flipping `LogResponsesApiBodies` off on the robot.
3. **Entity home v0** — not started; see §10.3 #1 for the migration path from the existing SQLite store.
4. **Session-end/nightly consolidation** — per-turn promotion is best-effort fire-and-forget (`Task.Run`, failures logged, lost on shutdown); no retry queue, no decay/`lastReinforced`, no contradiction sweep beyond per-fact conflict events.
5. **Orchestrator dedup (§2.3 #1) got worse, not better** — the server integration (`TryStartOrResumeAsync`/`TryHydratePromptAsync`/`TryPersistTurnAsync`/`ReportNonFatalServerError`/`RewriteMemoryQuery`), shutdown-intent regexes, and personality-switch/farewell blocks are now duplicated near-verbatim across both orchestrators. Only the wake sequence has been extracted (`RobotStartup.cs`). The shared turn engine is still the fix.
6. **Stubs returning canned JSON:** `scheduler` and `kinect_shot` in `ToolExecutionService` (`stub: true`); `LocalMemoryEmbeddingProviderStub` throws.
7. **Dead code:** robot client's `PromoteMemoryAsync`/`ReindexMemoryAsync` have no callers; `FaceTrackingEnabled`/`AmbientContextEnabled` options have no consumers.
8. **Test gaps:** no endpoint/integration tests (`WebApplicationFactory`), nothing for `ToolExecutionService`, `SmartyMode`, `FileSnapshotStore`; robot-side pure math still untested (§2.3 #8 — though most of that math is on the parked branch).
9. **Server UI is read-only** — archive/restore/promote endpoints exist but aren't surfaced; no text REPL/chat lane against the Mind yet (the second-embodiment moment is close: the endpoints exist, only a UI/CLI is missing).
10. **Scaling/robustness later:** brute-force vector scan over all records; regex fallback extraction is English-only; snapshot manifest is in-memory + JSON file.
11. **On-robot validation of the uncommitted camera/startup fixes** (checklist in `camera-and-startup-fixes.md`), then commit them together with `RobotStartup.cs`.
12. **Vision revival** — merging `wip/vision-head-tracking` per the merge notes (hand-port prompt guidance into `PromptContextBuilder`; expect conflicts in both orchestrators and `MotionOrchestrator`), ideally landing the local ONNX detector directly instead of resurrecting `OpenAiHeadDetector`.

### 10.5 Revised sprint order

- **Sprint 1 (unchanged): voice-loop unblock** — config refresh (both apps) + GA realtime migration behind `IRealtimeVoiceSession`. Everything realtime-adjacent (async smarty_mode, out-of-band heartbeats, native image input) is gated on this.
- **Sprint 2 (revised): finish memory, don't rebuild it** — decide §10.3 #1 (entity home) and #2 (retrieval cadence) explicitly; add the consolidation job on top of the existing promotion path; extract the shared turn scaffolding while touching both orchestrators (item 5 above rides along).
- **Sprint 3+:** as written in §8.1, with §10.4 items 6–12 folded in where they're touched anyway.

## Sources

- [Introducing gpt-realtime (OpenAI)](https://openai.com/index/introducing-gpt-realtime/)
- [Realtime API guide (OpenAI)](https://developers.openai.com/api/docs/guides/realtime)
- [API changelog (OpenAI)](https://developers.openai.com/api/docs/changelog)
- [gpt-realtime-2.1 release coverage (DataNorth)](https://datanorth.ai/news/openai-releases-gpt-realtime-2-1-voice-models)
- [GPT-Realtime-2 family overview (BuildFastWithAI)](https://www.buildfastwithai.com/blogs/openai-gpt-realtime-2-voice-ai-models)
- [Introducing GPT-5.4 (OpenAI)](https://openai.com/index/introducing-gpt-5-4/)
- [Model release notes (OpenAI Help)](https://help.openai.com/en/articles/9624314-model-release-notes)
- [Grok Voice Agent API announcement (xAI)](https://x.ai/news/grok-voice-agent-api)
- [xAI Voice API overview](https://x.ai/api/voice)
- [xAI voice docs](https://docs.x.ai/developers/model-capabilities/audio/voice)
- [xAI realtime via LiteLLM (OpenAI-compat note)](https://docs.litellm.ai/docs/providers/xai_realtime)
