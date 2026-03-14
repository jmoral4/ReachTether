## Model: gpt-5.4 (high)

I inspected the code you shared. Short version:

**You already have a solid robot-side runtime.**  
But **v1.1 requires a shift to a server-centric architecture** where the robot becomes a device/runtime node, and the Blazor app/server becomes the place that owns persistence, remote tools, UI state, and background orchestration.

## What you already have

From the current codebase, you already have good building blocks:

- **Robot audio I/O**
  - `LocalAudioSession`
  - `AudioCaptureService`
  - `AudioPlaybackService`

- **Two conversation pipelines**
  - `InteractionOrchestrator` for turn-based
  - `RealtimeInteractionOrchestrator` for realtime

- **Vision tooling**
  - `CameraTool`
  - `CameraSnapshotService`
  - `VisionStartupProbe`

- **Robot motion / expressive behavior**
  - `MotionOrchestrator`
  - `TalkingGestureSource`

- **Basic personality switching**
  - `PersonalityCatalog`
  - `personalities.json`
  - `ToolPromptAugmenter`

- **Some networking/media groundwork**
  - `ReachTether.WebRtc`
  - unused `ReachTether.Server` `HttpClient` in `Program.cs`

So this is not a greenfield problem.  
The main issue is that the current app is still basically a **single-process robot app** with a little bit of tooling, not yet a **distributed robot + server system**.

---

# Biggest gaps relative to `Docs/v1.1.md`

## 1. No real server control plane yet
You define a `ReachTether.Server` HTTP client in `Program.cs`, but nothing uses it.

There is currently no:

- persistent robot-to-server connection
- server-owned session store
- artifact store
- remote tool execution layer
- UI event stream

## 2. Tool execution is hardcoded and duplicated
Right now tool handling is embedded in two places:

- `InteractionOrchestrator.ResolveAssistantResponseAsync`
- `Realtime/Handlers/FunctionCallHandler.cs`

And both are basically wired around **camera only**.

That will not scale to:

- server tools
- scheduler
- KinectShot
- subagents
- smarter-model delegation

## 3. Personality is still “prompt blob per personality”
`personalities.json` works for simple switching, but it is not yet the layered system described in your OpenClaw notes.

## 4. No persistence / memory
Conversation history is in-memory only and truncated:

- `conversationHistory` in `InteractionOrchestrator`
- realtime session exists only in current process/runtime
- no transcript store
- no long-term knowledge store
- no memory hydration

## 5. No always-on orchestration
The host is long-running, but not “always on” in the OpenClaw sense:

- no persistent per-session lanes
- no heartbeat turns
- no system event queue
- no durable scheduler
- no background initiative

## 6. Video streaming is not actually implemented
You do have some WebRTC code, and `ReachyWebRtcSession` negotiates H264/audio, but:

- it is not used by the robot app
- there is no surfaced video frame pipeline
- there is no UI integration
- current vision API is snapshot-oriented, not stream-oriented

So the **camera tool** exists, but **live video to Blazor** does not.

---

# My recommendation: split the system into 2 layers

## A. Robot Runtime
Keep on the robot:

- mic / speaker
- local camera capture
- robot motion
- local realtime turn handling for low-latency voice
- local safety / wake / hardware access
- local tools that must touch robot hardware

## B. Server Runtime + Blazor UI
Move to server:

- persistent sessions
- transcripts and artifacts
- remote tool host
- scheduler
- memory / retrieval
- subagent execution
- smarter model offload
- UI state and dashboards
- “always on” services

That split matches your docs very well.

---

# Feature-by-feature implementation plan

## 1) Server Offload and Streaming

## Current state
You have:

- `ReachTether.Server` HTTP client registration
- camera snapshots via `CameraTool`
- some WebRTC infrastructure
- no persistent server session

## Recommended architecture
Build a new **ASP.NET Core host with Blazor UI** that contains:

- **Blazor UI**
- **robot control plane API**
- **SignalR hub** for live events/UI updates
- **storage services**
- **tool host**
- **scheduler**
- **subagent runner**

### Important point
Treat “Blazor app” as **an ASP.NET Core server that happens to host a Blazor UI**, not as “UI code that also does backend things.”

That keeps your architecture clean.

---

## Robot ↔ Server connection

For v1.1, I would use a **persistent outbound connection from robot to server**.

Best first-pass choice:
- **SignalR** for robot ↔ server live duplex messaging

Why:
- .NET on both sides
- good reconnect support
- easy UI fanout
- easy server push
- simpler than standing up browser-quality WebRTC immediately

### Robot should connect outbound
That’s important because the robot may be on a LAN/NAT.  
The robot initiating the connection is much easier operationally.

---

## Suggested server-side services

Create something like:

- `RobotSessionManager`
- `RobotConnectionRegistry`
- `ArtifactStore`
- `TranscriptStore`
- `ToolExecutionService`
- `SchedulerService`
- `SubAgentService`
- `MemoryService`
- `HeartbeatService`

And shared contracts like:

- `RobotHello`
- `RobotStatusUpdate`
- `RobotInteractionStateChanged`
- `ToolInvocationRequest`
- `ToolInvocationResult`
- `SnapshotArtifactCreated`
- `VideoFrameChunk`
- `SubAgentRunStarted`
- `SubAgentRunProgress`
- `SubAgentRunCompleted`

---

## Concrete code changes on robot side

### Add a robot control-plane client
New hosted service, e.g.:

- `RobotServerConnectionService : BackgroundService`

Responsibilities:

- connect to server
- register robot identity
- publish state transitions
- publish transcripts
- publish tool events
- publish snapshot artifacts
- receive server commands

This is the natural next step from the unused `ReachTether.Server` client in `Program.cs`.

---

## Video streaming to Blazor

### Important reality
Your current vision path is **snapshot-based**, not video-stream-based.

`CameraSnapshotService` is good for tool captures, but not ideal for live video.

### Best pragmatic v1.1 approach
Do **JPEG frame streaming** first, not full browser WebRTC.

Why:
- simpler
- easier to debug
- enough for a desktop UI “see what the robot sees”
- compatible with Blazor using JS/canvas or image refresh

### Implement:
- `RobotVideoCaptureService`
- capture frames continuously from the same camera source
- publish frames to server at a capped FPS, e.g. 5–10 fps first
- server broadcasts to desktop UI via SignalR
- UI renders latest frame

This gets you the feature sooner.

### Later option
If you want lower latency / smoother video later:
- add real WebRTC to browser
- or extend `ReachyWebRtcSession` with video frame sink abstractions

But I would **not** make that the first v1.1 milestone.

---

## Camera tool snapshots shown in desktop UI

This one is easy to support with your current code.

You already have:
- `CameraToolExecutionResult`
- image bytes
- media type
- question
- timestamp

### Add:
- `IArtifactPublisher`
- server-side `ArtifactStore`

Whenever `CameraTool.ExecuteAsync(...)` succeeds:

1. store artifact on server
2. publish event to UI
3. keep artifact ID in transcript/tool timeline

That lets the UI show:

- the image
- the tool question
- captured time
- maybe the model answer that used it

### Important refactor
Do this in one place, not separately in legacy/realtime pipelines.

---

## Remote tools from robot to server

This is core v1.1.

Right now tool execution is hardcoded around `CameraTool`.  
You need a **tool router**.

### Introduce:
- `IToolExecutor`
- `IToolRegistry`
- `ToolExecutionTarget` enum:
  - `LocalRobot`
  - `RemoteServer`

### Example tools
Local:
- `camera`
- maybe future robot motion / sweep look / face focus

Remote:
- `scheduler`
- `kinect_shot`
- `memory_query`
- `spawn_subagent`

Then the orchestrators do not care where the tool runs.

They just do:

- model requests tool
- `IToolExecutor.ExecuteAsync(...)`
- returns structured JSON result

### This is one of the most important refactors
Because right now you have tool logic duplicated in:

- `InteractionOrchestrator`
- `FunctionCallHandler`

That duplication will become painful fast.

---

## 2) Sub-agents and smarter models

## Current state
No real abstraction exists yet.

## What to build

### Add a server-side subagent runner
Something like:

- `ISubAgentRunner`
- `SubAgentRun`
- `SubAgentRunStatus`
- `SubAgentResult`

Capabilities:
- start subagent
- assign model
- give prompt/context
- timeout/cancel
- stream progress events
- collect final output

### Data to persist
Store:

- `RunId`
- `ParentRunId`
- session id
- model used
- prompt summary
- status
- start/end timestamps
- timeout reason
- final answer / structured output

### Why server-side?
Because:
- easier to observe
- easier to persist
- better place for more expensive models
- better for UI visibility

---

## Offloading “complex thinking” from realtime
Your docs explicitly say the realtime model is not that intelligent.

That strongly suggests a **two-brain model**:

### Fast brain on robot
- realtime voice
- interruption handling
- short conversational turns
- local tool initiation

### Deep brain on server
- planning
- memory synthesis
- multi-step reasoning
- subagents
- heavy tool chains

### Implementation pattern
Give the model/tool layer a server tool like:

- `delegate_reasoning`
- or `spawn_subagent`

The robot can still speak quickly, but offload complex tasks.

---

## 3) Personality system

## Current state
You have:
- `PersonalityCatalog`
- `personalities.json`
- a single active instructions string
- `ToolPromptAugmenter`

This is a good v0/v0.5.

## What’s missing
OpenClaw-style layering:

- stable identity
- stable “soul” / tone
- runtime overlays
- session-start behavior
- channel-specific overlays
- tool/runtime overlays

## Recommended v1.1 refactor

Introduce a prompt composition layer:

- `IPromptComposer`
- `PersonalityProfile`
- `IdentityDoc`
- `SoulDoc`
- `RuntimeOverlay`

### Composition example
Final prompt becomes:

1. base operational system prompt
2. personality identity block
3. personality soul/tone block
4. current environment/tool block
5. session overlays
6. optional social/channel overlay

### Why this matters
Right now `personalities.json` mixes:
- identity
- tone
- style
- tool behavior
- language behavior

That becomes hard to tune.

### Suggested model
Keep `personalities.json` for:
- ids
- display names
- switch aliases

But move the actual prompt docs into separate files or stored documents:
- `SOUL.md`
- `IDENTITY.md`
- maybe `SESSION_START.md`

That is much closer to the behavior you want.

---

## 4) Knowledge and Persistence

## Current state
There is effectively none.

The strongest signal is here:

- `InteractionOrchestrator` keeps conversation in a local `List<ChatMessage>`
- trims it to ~15 entries
- no DB
- no hydration
- no retrieval

## Recommended v1.1 design
Put this on the server.

### Start with SQLite
Good fit for first pass.

Suggested tables:

- `sessions`
- `messages`
- `transcripts`
- `artifacts`
- `tool_executions`
- `memories`
- `entities`
- `edges`
- `scheduled_jobs`
- `subagent_runs`
- `system_events`

### Retrieval modes
Use 3 retrieval types together:

- **recent session context**
- **full-text search**
- **vector similarity**
- optional **graph neighbors**

Then hydrate a compact “relevant memory” block into prompts.

### Minimal memory flow
On each turn:

1. save transcript/user turn
2. extract candidate facts/tasks/entities
3. write memory records
4. retrieve relevant memories for next turn
5. inject concise memory summary

---

## Good place to insert this in current code
Before completion call:

- `InteractionOrchestrator` before `CompleteChatAsync`
- `RealtimeInteractionOrchestrator` during session/tool routing or before deeper offload

But I would avoid wiring memory directly into both orchestrators.  
Better to create a shared conversation core.

---

## 5) Always On

## Current state
You have a long-running host, but not true always-on session orchestration.

## Missing pieces
You need:

- persistent session ownership
- per-session serialized execution
- heartbeat turns
- scheduled jobs
- background events that surface later

## Recommended server-side services

### Session lane / queue
Each session should have a serialized execution lane.

This matters a lot.

It prevents:
- overlapping tool calls
- races between heartbeats and user turns
- incoherent memory writes

### Heartbeat service
Run a periodic check for each active session.

If nothing matters:
- record no-op
- do not notify user

If something matters:
- create system event
- optionally notify UI / queue response

### System event queue
When background work finishes:
- enqueue an event into the session
- surface it on next interaction or heartbeat

### Scheduler
The “scheduler” tool should really be backed by a durable server scheduler, not an ad hoc callback.

Store jobs in SQLite:
- cron/time-based
- target session
- tool/action
- wake behavior
- status

---

# Code refactors I would do before adding features

These are the highest-value refactors.

## 1. Unify tool execution
Create:

- `IToolExecutor`
- `ToolInvocation`
- `ToolResult`
- `IToolRegistry`

Then remove hardcoded camera handling from:

- `InteractionOrchestrator.ResolveAssistantResponseAsync`
- `Realtime/Handlers/FunctionCallHandler.cs`

This is mandatory for v1.1.

---

## 2. Extract shared conversation core
Right now legacy and realtime orchestrators both own too much.

They duplicate:
- personality switching
- shutdown intent handling
- state transitions
- robot wake/sleep lifecycle
- response handling patterns

For v1.1, put shared logic into a service like:

- `ConversationCoordinator`
- or `SessionTurnEngine`

Then keep the two orchestrators as transport adapters.

That way server tools/memory/personality changes are added once.

---

## 3. Move tool DTOs out of `OpenAiTransport.cs`
These types are currently buried there:

- `ToolDefinition`
- `ToolCall`
- `ToolCallOutput`

That’s too low-level and too transport-specific.

Move them into a shared contracts/core layer because they will be used by:

- robot runtime
- server runtime
- subagents
- scheduler
- UI timeline

---

## 4. Stop overloading `IReachySession`
`LocalAudioSession` implements `IReachySession`, but `SendCommandAsync` is a stub returning empty JSON.

That’s a design smell.

I would split this into clearer abstractions, e.g.:

- `IAudioSession`
- `IRobotCommandChannel`
- `IVideoSource`
- `IRobotTransport`

That will make server offload much easier.

---

## 5. Add an internal event bus
You now have scattered direct calls.

Introduce an event bus or at least typed event publisher for:

- interaction state changes
- transcript events
- tool execution events
- snapshot artifact events
- subagent progress
- scheduler events

This is what will feed the server and the desktop UI.

---

# Recommended implementation order

If you want the shortest path to a meaningful v1.1, I’d do it in this order:

## Phase 1 — Server foundation
Build:

- `ReachTether.Server` ASP.NET Core + Blazor
- robot outbound connection service
- shared contracts
- server session registry
- UI dashboard showing:
  - robot connected/disconnected
  - listening/thinking/speaking state
  - latest transcript lines

## Phase 2 — Snapshot artifacts + tool routing
Build:

- `IToolExecutor`
- `RemoteToolExecutor`
- `ArtifactStore`
- publish `CameraTool` snapshots to server/UI
- add first remote server tool: `scheduler` or `kinect_shot`

## Phase 3 — Live video
Build:

- `RobotVideoCaptureService`
- capped FPS JPEG stream
- SignalR broadcast to Blazor UI
- latest-frame viewer in desktop UI

## Phase 4 — Memory/persistence
Build:

- SQLite schema
- transcript persistence
- artifact persistence
- retrieval/hydration service
- inject memory summaries into conversations

## Phase 5 — Subagents + smarter model offload
Build:

- server subagent runner
- timeout/cancel/progress
- stronger model routing for complex tasks

## Phase 6 — Always on
Build:

- per-session lanes
- heartbeat service
- durable scheduler
- system event queue

---

# A few very concrete v1.1 deliverables I’d target

If you want this to feel real quickly, target these 4 first:

## Deliverable 1
**Robot shows up in Blazor desktop UI with live state**
- connected
- current personality
- interaction state
- recent transcript

## Deliverable 2
**Camera tool artifacts appear in UI**
- image
- question
- timestamp
- tool result

## Deliverable 3
**Server tool execution works**
- robot can call `scheduler`
- robot can call `kinect_shot`
- same tool routing works in legacy and realtime paths

## Deliverable 4
**Basic memory works**
- conversation persists across restart
- server can hydrate relevant facts into next turn

That already gets you most of the way toward the doc’s spirit.

---

# My blunt assessment

Your current code is **good robot runtime code**.  
It is **not yet a distributed agent platform**.

The most important architectural move for v1.1 is:

> **Stop embedding orchestration state inside the robot app alone, and introduce a real server-owned control plane + tool plane + memory plane.**

If you do just that, the rest of the v1.1 features line up naturally.

---

If you want, I can turn this into a **concrete proposed folder/project structure** next, or I can draft the **exact interfaces/classes** I’d add first for:
- `IToolExecutor`
- robot/server contracts
- session persistence
- Blazor event streaming