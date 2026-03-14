## Model: gpt-5.2 (high)

## 0) What you have today (relevant to v1.1)

### Robot runtime shape
- **Two voice pipelines**
  - *Turn-based* (`InteractionOrchestrator`) using your custom **Responses API wrapper** (`OpenAiTransport.CompleteChatAsync`).
  - *Realtime* (`RealtimeInteractionOrchestrator`) using `OpenAI.RealtimeConversation`, including **streaming PCM playback** to ALSA (`LocalAudioSession.BeginPlaybackStream/WritePlaybackPcm16Chunk`).
- **Local audio** is solid and already “streamy”: capture loop → bounded channel (`AudioCaptureService`) and playback worker queue (`AudioPlaybackService`).
- **Vision tool exists and is well integrated**
  - `CameraTool` captures a snapshot from `ReachyMiniClient.Camera.CaptureSnapshotAsync`.
  - Tool call handling exists in *both* pipelines, but **only** for the camera tool:
    - Turn-based: `InteractionOrchestrator.ResolveAssistantResponseAsync` filters to camera tool calls.
    - Realtime: `FunctionCallHandler` only executes `CameraTool.Name`.
- **A server hook is already implied**
  - `Program.cs` registers an `HttpClient` named `"ReachTether.Server"` but nothing uses it yet.
- **WebRTC library exists**
  - `ReachTether.WebRtc` can connect to a signaling server and negotiate **audio send/recv + H264 recv-only video track** (`ReachyWebRtcSession` adds a video track).
  - It currently **does not surface video frames** to callers (no OnVideoFrameReceived path in your code).

That’s a good base: v1.1 is mostly “add a real server + generalize tools + persistence + streaming to UI”.

---

## 1) Server Offload & Streaming (Docs/v1.1.md section 1)

### 1.1 Remote Blazor “backend server” + desktop UI
**Recommendation:** create a new ASP.NET Core project, e.g. `dotNet/ReachTether.Server`, using **Blazor Server** (simplest for realtime UI + auth + SignalR) and a set of JSON APIs.

Core responsibilities:
1. **Robot <-> Server control plane** (bi-directional)
2. **UI <-> Server** (operators view state, send commands, see logs, images, video)
3. **Persistence** (snapshots, events, sessions, tool calls, subagent runs, knowledge)

**Transport choice (control plane):**
- Use **SignalR** for “2-way APIs”:
  - Robot connects as a client to `RobotHub` (outbound connection is firewall/NAT friendly).
  - Desktop UI connects to `RobotHub` too (or a separate `UiHub`).
- Keep HTTP endpoints for “request/response” operations (tool execution, uploads), but use SignalR for:
  - robot status heartbeat
  - tool call events
  - snapshot/video notifications
  - scheduler/event wakeups

**Minimal contract primitives**
- `RobotStatus` (state machine state, audio state, active personality, model, uptime, last error)
- `RobotEvent` (tool-called, tool-result, transcription, response text, warnings)
- `SnapshotRecord` (id, capturedAt, mediaType, bytes or blob ref, tags)

You already have a notion of correlation IDs in multiple places; carry that into server events:
- `LocalAudioSession.CorrelationId`
- `ReachyWebRtcSession.SendCommandAsync` uses `correlation_id`

Unify as `TraceId` / `CorrelationId` across server + robot.

---

### 1.2 Streaming video from robot to Blazor app (“see what the robot is seeing”)

You have two realistic implementation paths:

#### Path A (v1.1 MVP): “near-streaming” via snapshot push/pull
- Robot periodically captures snapshots (e.g. 2–10 fps depending on CPU/network).
- Robot sends to server as JPEG bytes (HTTP upload or SignalR streaming).
- UI displays as `<img>` that updates as new frames arrive (looks like video at 5–10 fps).

Why this is a good v1.1 first pass:
- You *already* capture JPEG snapshots reliably via `ReachyMiniClient.Camera.CaptureSnapshotAsync`.
- Avoids browser WebRTC complexity and H264 pipeline issues.

Concrete changes:
- Add a background service on robot: `VideoSnapshotStreamerService`
  - cadence config (e.g. `Vision:UiStreamFps`)
  - calls `ICameraSnapshotProvider.CaptureSnapshotAsync`
  - POST to server: `POST /api/robot/{robotId}/video/frame` with bytes + timestamp
- Server stores last frame in memory + optionally persists rolling buffer to disk/sqlite.
- Server notifies UI via SignalR: `OnNewFrame(robotId, capturedAt)`; UI requests bytes or server pushes base64.

#### Path B (later / “real streaming”): WebRTC to browser
If you want true streaming (audio/video sync, low latency), you’ll likely end up with:
- robot (or server) as a WebRTC producer
- browser as a WebRTC consumer
- server as signaling + auth + maybe TURN config

Your current `ReachyWebRtcSession` is a *client* to an existing signaling server/producer and doesn’t yet expose video frames. If Reachy already provides a WebRTC producer, the cleanest architecture might be:
- Server provides **signaling + access control** and UI connects directly (via JS WebRTC)
- Or server bridges the upstream WebRTC stream to browser

For v1.1, I’d do Path A and keep Path B as v1.2+.

---

### 1.3 Camera tool snapshots captured/tagged to show in desktop UI
You already generate a rich `CameraToolExecutionResult`:
- `Question`
- `Snapshot` (bytes + media type + capturedAt)
- `ToolOutputJson` and `ImageDataUrl`

Add a “publish” step after each camera tool execution:
- Robot sends to server:
  - snapshot bytes
  - question
  - tool call id / response id
  - tags like: `["tool:camera", "turn:123", "question:..."]`

Where to hook:
- Turn-based: inside `InteractionOrchestrator.ResolveAssistantResponseAsync`, right after:
  ```csharp
  var execution = await cameraTool.ExecuteAsync(...);
  ```
- Realtime: inside `FunctionCallHandler` after camera execution.

Server UI:
- A “Tool Feed” page: list of tool events with thumbnails; click expands image + metadata + model response.

---

### 1.4 Robot can call tools on remote server (KinectShot etc.)
This is the biggest code-structure change you need: **general tool routing**.

Today:
- Tool call handling is hard-coded to camera only.
- Everything else returns “tool execution not enabled”.

#### Proposed tool architecture
Create:
- `IToolRegistry` (tool name → schema/description + executor)
- `IToolExecutor` interface:
  - `Task<ToolExecutionResult> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct)`
- Two implementations:
  1. `LocalToolExecutor` (camera, robot-local actions)
  2. `RemoteToolExecutor` (calls server `/api/tools/{name}:execute`)

Then update both orchestrators:

**Turn-based**
- Replace the camera-only loop in `ResolveAssistantResponseAsync` with:
  - iterate tool calls
  - dispatch each to registry
  - collect outputs + any “supplemental messages”
  - call `ContinueToolCallsAsync` as you do now

**Realtime**
- Replace the `FunctionCallHandler` “if camera else unsupported” with a dispatch:
  - if local tool → execute locally
  - else remote tool → execute via server
  - then `AddItemAsync(function_call_output)` as now

#### Tool definition loading
For server tools, robot needs the schemas at runtime.
- Add endpoint: `GET /api/tools/definitions`
  - returns list of `{ name, description, parametersSchema, strict }`
- Robot merges:
  - local tool definitions (`cameraTool.ToolDefinitions`)
  - remote tool definitions (server response)
- Then passes union into `OpenAiTransport.CompleteChatAsync(..., tools: allTools)` and into realtime `sessionOptions.Tools`.

This gets you:
- “KinectShot” (server-side camera)
- “scheduler”
- “knowledge query”
- “spawn_subagent”
…all as ordinary function tools.

---

### 1.5 Robot can call “scheduler” tool on server
Implement scheduler as a server tool first (not a hidden API), because:
- model can decide when to schedule
- UI can show tool usage
- audit trail is easier

Minimal tool surface:
- `scheduler.create_job({ run_at_utc, title, payload })`
- `scheduler.list_jobs({ from_utc, to_utc })`
- `scheduler.cancel_job({ job_id })`

Execution:
- stores in sqlite
- background hosted service checks due jobs and emits events to robot via SignalR:
  - `RobotHub.Send("system_event", { type:"job_due", ... })`
- robot treats that as an “incoming system event” next turn (see Always On section).

---

## 2) Sub-agents + “smarter model offload” (Docs/v1.1.md section 2)

### What you already have
- Two model tiers implicitly:
  - realtime model for interaction
  - non-realtime / fallback model for heavier tasks (`ChatFallbackModel` exists)

### What’s missing
- A structured way to:
  - spawn jobs
  - enforce timeouts
  - collect outcomes
  - surface partial progress to UI

### Proposed v1.1 design
Make subagents **server-side** first (easier to persist + show in UI + run even if robot restarts).

Add server tool:
- `subagent.run({ name, instructions, input, model, timeout_ms }) -> { run_id }`
- server executes in background, stores:
  - `run_id`, status, start/end times, output, error
- robot can poll via tool or server can push completion via SignalR event

Then a robot-side helper:
- when the model asks to use `subagent.run`, you execute it remotely and return `run_id`
- optionally, a second tool `subagent.await({ run_id })` (blocks until complete or timeout)

Smarter-model offload becomes trivial:
- `subagent.run` uses `gpt-5.4` (or whatever you configure server-side)
- robot keeps realtime responsiveness

---

## 3) Personality system (Docs/v1.1.md section 3)

### Current state
- Personalities are in `personalities.json` and injected as “system prompt” instructions.
- `ToolPromptAugmenter` adds a camera-awareness block.
- This is functional, but it’s still “monolithic prompt per personality” and not layered the way your OpenClaw notes describe.

### v1.1 incremental improvement (low risk)
Introduce *layered prompt assembly* without rewriting everything:

1. Keep `personalities.json` but split fields:
   - `Identity` (short)
   - `Soul` (tone/style)
   - `Rules` (hard constraints like “1–2 sentences”)
2. Add server-hosted editable persona files later, but first you can do it locally.

Add a prompt builder that assembles:
- Base runtime system prompt (tool rules, safety, constraints)
- Persona blocks (identity/soul/rules)
- Situational overlays:
  - vision enabled overlay (what you do today)
  - “desktop-ui present” overlay (see below)
  - “always on / system events” overlay (see below)

Also: your default personality says “1–2 sentences maximum”, which will fight debugging and tool-result UX in a desktop UI. Consider a **separate “operator” personality** (or an overlay) when the conversation is routed through the desktop UI (longer, more explicit, more tool metadata).

---

## 4) Knowledge + Persistence (Docs/v1.1.md section 4)

### Goal
Server holds:
- sqlite full-text DB (FTS5)
- vector DB (could still be sqlite-backed)
- maybe graph later

### v1.1 pragmatic approach
Start with:
- **SQLite + FTS5** for “memory notes”
- Optional embeddings later (or store embeddings but don’t require them day 1)

Server endpoints/tools:
- `knowledge.query({ text, top_k }) -> { items:[{ id, snippet, source, score }] }`
- `knowledge.upsert({ text, tags }) -> { id }`

Robot integration points:
1. Before calling the LLM for a user turn:
   - robot asks server `knowledge.query` with user transcript
   - injects returned items as a **system** or **developer-like** message (“Relevant memory:”)
2. After a turn:
   - optionally call `knowledge.upsert` for durable facts (can be automatic heuristics or model-driven tool)

This immediately gives you “hydrating knowledge on related requests”.

---

## 5) Always On (Docs/v1.1.md section 5)

You can copy the OpenClaw *illusion* without pretending the model is continuous:

### v1.1 “Always On” minimal recipe
**Server is the always-running gateway.** It owns:
- sessions (per robot + per operator)
- cron/scheduler
- system-event queue
- periodic heartbeat

Implement:
- `SystemEvents` table: `{ id, robot_id, created_at, payload_json, delivered_at }`
- Server background service:
  - heartbeats every N minutes
  - checks pending scheduled jobs
  - pushes events to robot via SignalR (`RobotHub`)

Robot behavior:
- Subscribe to server events.
- When an event arrives, you have options:
  1. If robot is idle, initiate a “self-turn” (a short proactive utterance).
  2. Otherwise queue it as “pending system events” and inject into the next model prompt.

Where to inject in code:
- Turn-based: right before `CompleteChatAsync(conversationHistory, ...)`, add a `SystemChatMessage` like:
  - “System events since last turn: …”
- Realtime: update `Instructions` in session config is heavier; better:
  - add a “conversation item create” message from `role=user` or `role=system` equivalent (Realtime API constraints apply), or
  - keep a local queue and prepend to the next turn’s first input.

---

## 6) Concrete milestone plan (v1.1-focused)

### Milestone 1 — Server skeleton + robot connectivity
- Add `ReachTether.Server` (Blazor Server + minimal APIs).
- Robot connects via SignalR and sends:
  - status updates (state machine state, audio state, active personality)
  - log/event feed

### Milestone 2 — Snapshot “video” + camera tool gallery
- Implement periodic snapshot streaming (Path A).
- Store camera-tool snapshots with metadata.
- UI shows:
  - live view (auto-refresh image)
  - tool snapshot timeline

### Milestone 3 — Generalized tool execution
- Implement `IToolRegistry` + `RemoteToolExecutor`.
- Add server tool definitions endpoint.
- Modify both orchestrators to dispatch tool calls (not camera-only).

### Milestone 4 — Scheduler + system events + first “always on”
- Implement scheduler tool.
- Server pushes “job due” events to robot.
- Robot injects system events into next turn.

### Milestone 5 — Knowledge store (FTS first)
- Add sqlite + FTS memory query/upsert tools.
- Robot queries before LLM calls, injects memories.

### Milestone 6 — Subagent runs (server-side)
- Add `subagent.run/await` tools.
- UI shows run list + status/progress.

---

## 7) Key code hotspots to change (so you don’t miss anything)

- **Turn-based tool handling is camera-only**
  - `InteractionOrchestrator.ResolveAssistantResponseAsync` → replace with tool router.
- **Realtime tool handling is camera-only**
  - `Realtime/Handlers/FunctionCallHandler.cs` → dispatch to local/remote tools.
- **You already have a server HttpClient**
  - `"ReachTether.Server"` in `Program.cs` → wrap it in a typed client (e.g. `ReachTetherServerClient`) and actually use it.
- **Video**
  - You have snapshot capture today (`CameraSnapshotService`) → easiest streaming MVP.
  - `ReachTether.WebRtcSession` has H264 recv track but no surfaced frames → don’t rely on it for v1.1 unless you commit to implementing video decode + browser delivery.

---

## 8) Two clarifying questions (to lock the “streaming” architecture)
1) Do you want the **desktop UI to connect to the robot directly** (LAN) for video, or must everything route through the server (WAN-friendly)?
2) Is “streaming video” acceptable in v1.1 as **high-frequency JPEG frames** (5–10 fps), or does it need to be true WebRTC video?

If you answer those, I can suggest a concrete API shape (SignalR messages + REST endpoints) and the exact classes/files I’d add on both robot and server sides.