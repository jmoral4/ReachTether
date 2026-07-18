# Hermes Agent as the ReachTether Entity Runtime

Date: 2026-07-18

## Executive summary

Hermes Agent substantially changes the build-versus-adopt decision described in `Docs/entity-platform-architecture-review.md`.

The earlier review recommends separating ReachTether into:

1. an enduring Entity Host that owns identity, memory, sessions, models, tools, scheduling, and background work; and
2. thin Embodiment Nodes that own hardware, media, and local safety.

Hermes Agent already implements much of the proposed Entity Host. It is an MIT-licensed, self-hosted OSS agent with a shared agent core, model-provider routing, durable SQLite sessions, full-text session search, curated memory, skills, plugins, MCP, subagents, cron, messaging gateways, desktop/CLI surfaces, and multiple programmatic protocols.

The revised recommendation is therefore:

> Use Hermes as the leading candidate for the entity runtime, and evolve ReachTether into a reusable embodiment layer.

Do not rewrite the Reachy hardware runtime in Python or merge it into Hermes. Keep the current .NET implementation for Reachy SDK access, ALSA, camera capture, tracking, motion, health, low-latency media, interruption, and local safety. Connect it to Hermes through plugins, MCP, and a purpose-built embodiment protocol.

This conclusion is strong enough to justify a focused integration spike, but not yet strong enough to commit the product permanently to Hermes. The spike should verify voice quality, session continuity, memory behavior, plugin stability, data portability, security boundaries, and upgrade friction.

## Why Hermes is unusually well aligned

Hermes is not merely another chat frontend or model wrapper. Its documented architecture is close to the Entity Host proposed for ReachTether.

### Shared agent core

Hermes runs the same `AIAgent` core behind:

- CLI and TUI
- its messaging gateway
- an Electron desktop application
- batch execution
- an OpenAI-compatible API server
- ACP integrations
- a Python library

This matches the goal of one entity expressed through several surfaces instead of one agent implementation per device.

### Provider and model flexibility

Hermes supports multiple model providers and mid-session model switching. That could replace much of the proposed ReachTether model-router work and reduce the need to encode specific OpenAI model families throughout the robot application.

This does not eliminate the need for workload policy. ReachTether would still need to decide which tasks deserve a fast conversational model, a deeper reasoning model, a local model, or a privacy-constrained route. Hermes supplies the provider/runtime machinery; the entity configuration supplies the policy.

### Durable session storage

Hermes stores session metadata, complete message history, tool calls, reasoning/provider information, token use, and costs in `~/.hermes/state.db`.

Its session database includes:

- SQLite WAL mode
- FTS5 message search
- trigram search
- session lineage
- source/platform tags
- schema migrations
- export and deletion operations
- support for concurrent readers and a single writer

This is much closer to the proposed portable session store than ReachTether's current in-memory legacy history and transient Realtime sessions.

### Existing memory and learning mechanisms

Hermes includes:

- bounded `MEMORY.md` agent notes
- bounded `USER.md` user-profile memory
- session search across the SQLite history
- skills that preserve procedural knowledge
- a learning loop that encourages the agent to save useful knowledge and techniques

Those mechanisms directly address part of the desired “entity that grows with me” experience.

They are not, by themselves, the complete long-term knowledgebase envisioned for ReachTether. The curated memory files are intentionally small, and searchable transcripts are not the same as evidence-backed semantic memory. A richer knowledge and artifact layer may still be needed, but it can be added around Hermes instead of first building an entire agent runtime.

### Tools, plugins, and MCP

Hermes provides three relevant extension routes:

1. **Plugins** can add tools, hooks, skills, commands, providers, and gateway platforms without modifying core Hermes code.
2. **MCP clients** let Hermes discover and call external tool servers.
3. **Hermes as an MCP server** can expose its own conversations/capabilities to other agents.

A Reachy integration is a natural fit for a Hermes plugin plus an out-of-process Reachy tool/control service.

### Background and always-on features

Hermes already includes many of the features previously proposed for a custom Entity Host:

- persistent gateway process
- scheduling and cron delivery
- subagent delegation
- queued/interruptible sessions
- cross-platform delivery
- background runs
- session continuity
- skills and durable context

Adopting these features avoids spending much of the ReachTether roadmap recreating generic agent infrastructure.

### License and deployment

Hermes is MIT licensed and is designed to run on user-controlled infrastructure. It supports native Windows as well as Linux, macOS, WSL2, and Termux. That makes desktop/laptop deployment and experimentation practical.

## Revised target architecture

```text
                    Hermes Agent
          identity, memory, sessions, skills
          model routing, tools, cron, subagents
                         |
              Reachy Hermes integration
       platform adapter + tools + embodiment client
                         |
          authenticated control/event connection
                         |
              .NET Reachy Embodiment Node
       ALSA, camera, tracking, motion, safety, health
```

Hermes becomes the leading implementation of the Entity Host. ReachTether becomes the embodiment framework and Reachy Mini adapter.

### Hermes responsibilities

- stable entity identity and personality
- user/session continuity
- model and provider selection
- prompt assembly
- memory and session search
- skills and learned procedures
- general tools and MCP integrations
- subagents and long-running work
- cron and proactive work
- approvals for consequential tools
- delivery to connected platforms
- desktop and messaging user interfaces

### ReachTether responsibilities

- hardware-neutral embodiment contracts
- Reachy Mini SDK adapter
- ALSA capture and playback
- camera snapshot and stream acquisition
- local face/person tracking
- motion composition and arbitration
- wake/sleep and device health
- low-latency interruption and playback cancellation
- local actuator limits and emergency behavior
- binary media transport
- optional degraded/offline behavior

### Responsibilities that remain product-specific

Hermes does not remove the need to design:

- embodiment identity and capability advertisement
- selecting which embodiment should speak or act
- presence across Reachy, desktop, and phone
- high-rate audio/video transport
- actuator priority and safety
- raw-media privacy and retention
- a migration/backup UX for the entire entity profile
- evidence-backed semantic memories beyond short curated notes
- evaluation of personality consistency across different voice paths

These are the differentiated parts of ReachTether and are better uses of engineering time than building another general-purpose agent loop.

## Recommended integration boundaries

### 1. Keep a separate .NET Reachy Node

The existing `ReachTether.Robot` should evolve toward a headless device service rather than being replaced.

It should expose bounded capabilities such as:

- `get_status`
- `capture_snapshot`
- `start_video_stream`
- `look_at`
- `perform_gesture`
- `start_face_tracking`
- `stop_face_tracking`
- `play_audio`
- `stop_audio`
- `wake`
- `sleep`

It should also publish:

- transcripts or finalized speech events
- tracking observations
- device health
- command completions
- camera artifacts
- optional audio/video streams

### 2. Add a Hermes Reachy plugin

Hermes's plugin system is the preferred place for third-party tools and platform integrations.

A `hermes-reachy` plugin could register:

```text
reachy_status
reachy_camera_snapshot
reachy_look_at
reachy_gesture
reachy_tracking
reachy_wake
reachy_sleep
```

The plugin should remain small. Tool handlers should call the out-of-process Reachy Node rather than contain robot SDK or ALSA code.

This preserves:

- independent Reachy Node testing
- a clear process boundary
- .NET hardware expertise
- Hermes upgradeability
- the ability to replace Hermes later

### 3. Consider MCP for discrete device tools

An alternative or complementary design is to expose Reachy actions as an MCP server.

Advantages:

- Hermes already discovers and registers MCP tools.
- The Reachy capability surface stays usable by other agents.
- Hermes-specific Python code is minimized.
- Per-tool exposure/filtering is supported.

MCP is appropriate for discrete operations and resources. It is not the transport for continuous PCM audio, live video, or a 50 Hz motion-control loop.

### 4. Add a Reachy gateway platform adapter

Hermes supports third-party gateway platforms through plugins. A `reachy` platform adapter would allow Reachy to behave like another Hermes conversation surface.

The adapter could:

- accept finalized user transcripts from Reachy
- map the user/device to a stable Hermes session lane
- send agent responses back to the Reachy Node
- attach camera images to turns
- make Reachy a valid cron/proactive delivery target
- inject embodiment-specific prompt context
- allow stop/interruption events to cancel an active Hermes run

This is the cleanest way to make Reachy feel like one of the entity's native embodiments.

However, Hermes gateway platform adapters are message oriented. They should not carry high-rate audio frames, video frames, or direct motor control.

### 5. Use a separate media/control data plane

Use transport according to workload:

- HTTP for tool requests, status, configuration, and artifacts
- gRPC or binary WebSockets for embodiment events and moderate-rate streams
- WebRTC when adaptive live audio/video and browser/mobile interoperability matter
- MCP for agent-visible discrete capabilities
- Hermes gateway APIs for session messages, approvals, and agent lifecycle

Do not encode continuous camera streams as base64 JSON messages or expose raw Reachy SDK DTOs as the permanent cross-device protocol.

## Voice is the largest open question

Hermes voice support is useful, but it is not currently equivalent to the OpenAI Realtime speech-to-speech pipeline already present in ReachTether.

Hermes's documented CLI voice flow is approximately:

```text
microphone
  -> VAD / bounded recording
  -> Whisper-compatible transcription
  -> text agent turn
  -> sentence-buffered streaming TTS
  -> playback
```

This supports local or cloud transcription, several TTS choices, continuous microphone operation, and sentence-by-sentence speech generation. It is a capable chained voice pipeline.

OpenAI Realtime offers a different experience:

- direct live audio input/output
- lower first-audio latency
- more natural prosody and turn-taking
- speech-level interruption
- live tool calls inside the voice session

Three integration modes should be considered.

### Mode A: Hermes chained voice

```text
Reachy audio -> transcription -> Hermes -> TTS -> Reachy playback
```

Advantages:

- Hermes is unequivocally the conversational brain.
- All turns naturally use Hermes memory, tools, skills, and sessions.
- Transcripts are explicit and easy to persist.
- Provider components can be swapped independently.
- Integration is comparatively simple.

Disadvantages:

- Higher turn latency.
- Less natural interruption and prosody.
- It may feel more like voice chat than a continuously present entity.

This should be the first prototype because it tests the fundamental Hermes/Reachy relationship with the least integration complexity.

### Mode B: Realtime voice model with Hermes as a tool

```text
Reachy audio <-> OpenAI Realtime
                    |
                ask_hermes
                    |
                 Hermes
```

Advantages:

- Preserves the most natural current voice experience.
- Hermes can supply memory, deeper reasoning, skills, and general tools.
- Existing ReachTether Realtime work remains useful.

Disadvantages:

- The Realtime model can become a second “mind.”
- Personality and memory can diverge between the voice shell and Hermes.
- The model may answer without consulting Hermes unless tool policy strongly constrains it.
- Hermes answers may be paraphrased or altered by the voice model.
- Tool and session traces are split between two runtimes.

If used, the Realtime prompt should explicitly define itself as an embodiment/voice renderer for the Hermes entity, not an independent assistant.

### Mode C: Realtime media shell with Hermes-authored responses

In this mode, the Realtime connection handles speech boundaries, audio transport, and speech rendering while Hermes remains responsible for response content.

This provides the cleanest conceptual identity, but may require more custom Realtime control and careful insertion/rendering of Hermes output. It should be investigated only after Modes A and B have been measured.

## Memory and portability analysis

### What Hermes already solves

Hermes stores its persistent state under `HERMES_HOME`, defaulting to `~/.hermes`.

Important contents include:

- `state.db` session and message history
- `MEMORY.md` and `USER.md`
- skills
- plugins
- configuration
- logs and runtime data

Changing `HERMES_HOME` makes the storage location configurable, which aligns well with the desire to carry the entity between machines.

### What still needs work

Simply copying `~/.hermes` while Hermes is running is unsafe because `state.db` uses WAL mode and has companion WAL/shared-memory files.

ReachTether should provide or contribute a profile command such as:

```text
reachctl hermes-profile export --output entity.rte
reachctl hermes-profile import entity.rte
```

Export should:

- stop or quiesce Hermes writes
- create a consistent SQLite backup
- include memories, user profile, skills, plugins, identity files, and selected artifacts
- include schema/application versions and checksums
- exclude or separately handle credentials
- support optional encryption

### Hermes memory is not the entire knowledgebase

The primary Hermes memory files are deliberately bounded:

- `MEMORY.md`: approximately 2,200 characters
- `USER.md`: approximately 1,375 characters

That is enough for high-value identity and preference context, not a lifetime knowledgebase.

Hermes's SQLite transcript search and skills add much more durable context, but the desired entity may still need a companion knowledge plugin with:

- evidence/provenance links
- memory confidence
- fact correction and supersession
- sensitivity and retention classes
- artifact references
- semantic/vector retrieval
- user-facing inspection and deletion

The important difference is that this becomes a focused memory extension rather than part of a new general agent platform.

### Do not make Hermes internals the permanent data contract

Treat Hermes as the current runtime implementation, not the only possible owner of identity.

Keep user-owned identity, knowledge, and artifacts in documented/exportable formats. If the project later changes runtimes, migration should not require reverse-engineering opaque plugin state.

## Risks and reservations

### Rapid upstream development

Hermes is new and moving quickly. Extension APIs, configuration, storage schemas, and behavior may change.

Mitigation:

- integrate through documented plugin/MCP/API surfaces
- pin a known version
- maintain contract tests
- upgrade deliberately
- avoid importing private Python internals
- keep the Reachy Node independent

### Runtime and language split

The product would span Python/TypeScript Hermes code and .NET hardware code.

This is acceptable if the boundary is explicit. It becomes harmful only if robot behavior is split arbitrarily between both runtimes or if shared domain records have no versioned wire contract.

### Security surface

Hermes can expose powerful terminal, file, browser, messaging, and external tools. A physically embodied agent raises the consequence of incorrect or malicious actions.

Mitigation:

- enable the smallest toolsets needed
- keep hardware safety local
- use explicit approvals for consequential actions
- authenticate and authorize the Reachy Node
- separate read-only perception tools from actuator tools
- audit every external and physical action
- never let generic terminal access bypass the Reachy command/safety boundary

### Voice identity split

Using a separate Realtime model may create inconsistent personality or memory behavior.

Mitigation:

- start with Hermes-authored chained voice
- evaluate the Realtime shell separately
- treat the voice model as an embodiment renderer
- require Hermes consultation for memory/personality-sensitive answers

### Memory quality

Hermes's automatic memory and skill creation may preserve incorrect, overly broad, or sensitive information.

Mitigation:

- inspect actual memory behavior during the spike
- require provenance for durable personal facts
- add user confirmation for sensitive categories
- provide memory inspection, correction, and deletion
- back up before autonomous skill/memory experiments

### Dependency versus control

Adopting Hermes trades implementation cost for upstream dependency. A fork would restore control but create a large maintenance burden.

Recommendation:

- use upstream Hermes
- build plugins and external services
- contribute generally useful extension points upstream
- fork only after a concrete, repeated blocker cannot be solved through supported APIs

## Build-versus-adopt comparison

| Area | Build Entity Host in ReachTether | Adopt Hermes |
|---|---|---|
| Agent loop | Full implementation required | Already implemented |
| Model providers | Must design and maintain routing | Broad provider support exists |
| Sessions | New schema/runtime required | SQLite session store exists |
| Search | Must add FTS/vector retrieval | FTS session search exists |
| Memory | Full design required | Curated memory and skills exist; deeper memory still needed |
| Tools | Shared registry must be built | Plugins, built-ins, and MCP exist |
| Subagents | Must be implemented | Already supported |
| Scheduler | Must be implemented | Cron exists |
| Messaging/Desktop | Must be built | Multiple platforms and desktop already exist |
| Voice | Current ReachTether Realtime is stronger | Chained voice exists; Realtime gap remains |
| Robotics | Existing .NET foundation | Must be added externally |
| Data control | Completely custom | Local/exportable, but Hermes-shaped |
| Maintenance | Full ownership | Upstream dependency plus integration maintenance |

Hermes wins strongly on generic agent infrastructure. ReachTether wins strongly on robotics, Realtime voice, and embodiment behavior. The hybrid architecture uses each where it is strongest.

## Recommended validation spike

The purpose of the spike is to test architectural fit, not to begin a permanent migration.

### Phase 1: Run Hermes as a desktop agent service

- Install a pinned Hermes release in an isolated profile.
- Configure an existing model provider rather than assuming Nous Portal is required.
- Enable its API server on localhost only with authentication.
- Record the exact files created under `HERMES_HOME`.

### Phase 2: Connect ReachTether as an API client

- Send finalized ReachTether transcripts to Hermes `/v1/responses`.
- Reuse a named conversation or `previous_response_id` chain.
- Send a camera snapshot as an image input.
- Return Hermes text through the current ReachTether TTS/playback path.

This produces a complete end-to-end chained voice embodiment without modifying Hermes core.

### Phase 3: Add three robot tools

Implement only:

1. `reachy_status`
2. `reachy_camera_snapshot`
3. `reachy_look_at`

Expose them through either a small Hermes plugin or MCP server. Avoid adding gesture libraries, video streaming, scheduling, or broader controls until the basic tool boundary is validated.

### Phase 4: Test continuity and portability

Run at least 20–30 conversations across several restarts.

Verify:

- personality consistency
- user preference recall
- session search quality
- appropriate memory writes
- tool-call reliability
- image/tool context preservation
- moving the profile to another machine
- restoring it to the original machine

### Phase 5: Measure experience

Capture:

- speech-end to first text latency
- speech-end to first audio latency
- total turn latency
- interruption behavior
- tool selection accuracy
- unnecessary memory writes
- useful memory retrievals
- personality adherence
- errors after Hermes upgrades/restarts

### Phase 6: Compare voice architectures

Compare the chained Hermes path with the current OpenAI Realtime path using the same representative conversations.

Do not judge only by latency. Evaluate:

- naturalness
- continuity
- memory grounding
- tool reliability
- interruption
- personality consistency
- operational complexity

## Go/no-go criteria

Proceed with Hermes as the Entity Host if the spike demonstrates:

- reliable programmatic session control
- acceptable voice latency or a credible hybrid voice path
- stable plugin/MCP tool execution
- useful cross-session continuity
- understandable and exportable state
- manageable upgrades
- sufficient security controls
- no need to modify Hermes core for ordinary Reachy capabilities

Do not commit to Hermes if:

- the agent cannot be reliably driven as an embodiment backend
- identity/memory behavior cannot be controlled or audited
- upgrades repeatedly break supported integration surfaces
- low-latency voice requires a second independent personality
- essential behavior requires a permanent invasive fork
- portable backup/restore cannot be made dependable

## Impact on the existing architecture roadmap

`Docs/entity-platform-architecture-review.md` remains directionally correct. Its Entity Host/Embodiment Node split should be retained.

The main revision is implementation strategy:

| Earlier roadmap component | Revised implementation candidate |
|---|---|
| Entity Host | Hermes Agent gateway/API runtime |
| Model router | Hermes provider/model runtime plus ReachTether policy |
| Tool registry | Hermes plugins/MCP |
| Session store | Hermes `state.db` initially |
| Short identity memory | Hermes `MEMORY.md`, `USER.md`, and personality files |
| Deeper knowledgebase | ReachTether/Hermes companion plugin or service |
| Scheduler and background jobs | Hermes cron/runs/subagents |
| Desktop agent UI | Hermes Desktop initially |
| Reachy Embodiment Node | Existing .NET runtime, progressively isolated |
| Realtime media | ReachTether .NET and/or dedicated WebRTC bridge |
| Phone/text embodiments | Hermes gateway platforms plus future device adapters |

This could remove approximately 60–80% of the generic Entity Host implementation work, while leaving the distinctive embodiment work under ReachTether's control. That percentage is an architectural estimate to validate in the spike, not a measured project forecast.

## Recommended decision

Hermes should become the preferred candidate, not an unquestioned dependency.

The next engineering action should be the narrow validation spike above. Do not begin a large ReachTether core refactor or a Hermes fork first.

If the spike succeeds, the product direction becomes:

> Hermes is the enduring entity runtime. ReachTether is the embodiment platform. Reachy Mini is the first rich physical embodiment.

This is more leverage-efficient than rebuilding a complete agent platform, preserves the strongest parts of the current .NET code, and keeps future devices possible through the same embodiment boundary.

## Primary references

- [Hermes Agent repository and license](https://github.com/NousResearch/hermes-agent)
- [Hermes architecture](https://hermes-agent.nousresearch.com/docs/developer-guide/architecture)
- [Programmatic integration](https://hermes-agent.nousresearch.com/docs/developer-guide/programmatic-integration)
- [Session storage](https://hermes-agent.nousresearch.com/docs/developer-guide/session-storage)
- [Persistent memory](https://hermes-agent.nousresearch.com/docs/user-guide/features/memory/)
- [Plugin system](https://hermes-agent.nousresearch.com/docs/user-guide/features/plugins/)
- [MCP integration](https://hermes-agent.nousresearch.com/docs/user-guide/features/mcp)
- [Adding a platform adapter](https://hermes-agent.nousresearch.com/docs/developer-guide/adding-platform-adapters)
- [Voice mode](https://hermes-agent.nousresearch.com/docs/user-guide/features/voice-mode)
- [API server](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/user-guide/features/api-server.md)
