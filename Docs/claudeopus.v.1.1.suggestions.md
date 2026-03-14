## Model: claude-opus-4-6

# v1.1 Feature Implementation Analysis

## Current Architecture Summary

Your codebase is a **.NET hosted-service robot** built around two voice pipelines (`InteractionOrchestrator` for turn-based, `RealtimeInteractionOrchestrator` for OpenAI realtime). The robot runs locally on the Reachy Mini, talks to OpenAI directly, holds conversation history in-memory (trimmed to 12 messages), loads personalities from a flat JSON catalog, and shuts down after the conversation ends or on fatal error. There is no server component, no persistence, no heartbeat, and no sub-agent infrastructure.

Below is a feature-by-feature breakdown of what exists, what's missing, and how each v1.1 feature could land.

---

## 1. Server Offload and Streaming

### What exists today

- `Program.cs` already registers a named `HttpClient` called `"ReachTether.Server"` — it's wired up but **never consumed** by any service. This is a clear placeholder for server communication.
- `ReachyWebRtcSession` has full WebRTC plumbing including video track negotiation (`H264` constrained baseline) and audio Opus encode/decode. This could be repurposed or extended for streaming to a Blazor backend.
- `CameraSnapshotService` captures JPEG snapshots with caching (`SnapshotCacheMs`). These are currently only used for the `CameraTool` in chat context.
- `OpenAiResponsesClient` wraps a bare `HttpClient` for the OpenAI Responses API — a pattern that could be replicated for a server API client.

### What's missing

- **No server project exists.** You need a new `ReachTether.Server` Blazor Server (or Blazor WASM + API) project.
- **No 2-way communication channel.** The robot has no SignalR/gRPC/WebSocket connection to a backend server.
- **No video streaming pipeline.** The camera captures snapshots but doesn't stream frames to any external consumer.
- **No server-side tool registry.** Tools like `camera` are defined and executed locally in `CameraTool`. There's no concept of a remote tool.

### Recommended implementation

**A. New project: `ReachTether.Server`**

A Blazor Server app that acts as both backend API and desktop UI. Minimal initial surface:

```
/api/robot/register          — robot announces itself, gets a session token
/api/robot/heartbeat         — periodic keep-alive + state push
/api/tools/{toolName}/invoke — robot calls a server-hosted tool
/hubs/robot                  — SignalR hub for bidirectional real-time comms
```

**B. Robot-side server client: `IServerLink`**

```csharp
internal interface IServerLink
{
    Task ConnectAsync(CancellationToken ct);
    Task<JsonNode> InvokeToolAsync(string toolName, JsonObject arguments, CancellationToken ct);
    Task PushStateAsync(RobotStateSnapshot snapshot, CancellationToken ct);
    Task PushVideoFrameAsync(byte[] jpegBytes, DateTimeOffset capturedAt, CancellationToken ct);
    IAsyncEnumerable<ServerCommand> ReceiveCommandsAsync(CancellationToken ct);
}
```

Register it in `Program.cs` using the already-existing `"ReachTether.Server"` named HttpClient, plus a SignalR `HubConnection` for the realtime channel.

**C. Video streaming**

The simplest v1 approach: run a background service (`VideoStreamService`) that periodically calls `CameraSnapshotService.CaptureSnapshotAsync()` and pushes JPEG frames over the SignalR hub. The Blazor UI renders them as an `<img>` tag with a streaming src or via a JS interop canvas. This avoids needing full WebRTC browser negotiation in v1.1 — you can upgrade to WebRTC relay later.

```csharp
internal sealed class VideoStreamService(
    ICameraSnapshotProvider camera,
    IServerLink serverLink) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = await camera.CaptureSnapshotAsync(stoppingToken);
            if (snapshot is not null)
                await serverLink.PushVideoFrameAsync(snapshot.ImageBytes, snapshot.CapturedAt, stoppingToken);
            await Task.Delay(100, stoppingToken); // ~10fps
        }
    }
}
```

**D. Server-hosted tools**

Extend the existing tool resolution in `InteractionOrchestrator.ResolveAssistantResponseAsync`. Currently it only checks `cameraTool.IsCameraToolCall(toolCall)`. Add a fallback path:

```csharp
if (cameraTool.IsCameraToolCall(toolCall))
{
    // existing local camera execution
}
else if (serverLink.IsServerTool(toolCall.Name))
{
    var result = await serverLink.InvokeToolAsync(toolCall.Name, toolCall.ArgumentsJson, ct);
    toolOutputs.Add(new ToolCallOutput(toolCall.Id, result.ToJsonString()));
    handledTool = true;
}
```

The server would expose tools like `KinectShot` (capturing from a Kinect v2 attached to the server machine) and `scheduler` (creating calendar entries). The tool definitions would be fetched from the server at startup and merged into the `tools` list passed to `CompleteChatAsync`.

**E. Configuration additions to `RobotAppOptions`**

```csharp
public sealed class ServerSettings
{
    public bool Enabled { get; init; } = false;
    public string BaseUrl { get; init; } = "https://localhost:5001";
    public string HubPath { get; init; } = "/hubs/robot";
    public int VideoStreamFps { get; init; } = 10;
    public int HeartbeatIntervalMs { get; init; } = 30000;
}
```

---

## 2. Sub-agents and Other Models

### What exists today

- The `OpenAiTransport` already supports primary + fallback model switching (`ChatModel` / `ChatFallbackModel`).
- Tool call resolution in `ResolveAssistantResponseAsync` runs up to `MaxToolRounds = 3` sequential tool rounds — but always in the same model context.
- In the realtime pipeline, `FunctionCallHandler` executes tool calls inline within the realtime session.

### What's missing

- **No sub-agent abstraction.** There's no way to spawn a parallel or sequential child task that runs its own LLM conversation.
- **No "think harder" offload.** The realtime model (`gpt-realtime-1.5`) handles everything; there's no mechanism to route a complex reasoning question to a smarter model (e.g., `o3`, `gpt-5`) and wait for the result.

### Recommended implementation

**A. Sub-agent abstraction**

```csharp
internal interface ISubAgentRunner
{
    Task<SubAgentResult> RunAsync(SubAgentRequest request, CancellationToken ct);
}

internal sealed record SubAgentRequest(
    string AgentId,
    string Instructions,
    string? Model,
    IReadOnlyList<ChatMessage> Context,
    IReadOnlyList<ToolDefinition>? Tools,
    TimeSpan Timeout,
    SubAgentLocation Location); // Local or Server

internal sealed record SubAgentResult(
    string AgentId,
    string OutputText,
    bool TimedOut,
    string? FailureReason);

internal enum SubAgentLocation { Local, Server }
```

For **local** sub-agents: use `OpenAiTransport.CompleteChatAsync` with a separate conversation history and possibly a different model. Run inside a `Task.Run` with a `CancellationTokenSource` timeout.

For **server** sub-agents: call `IServerLink.InvokeToolAsync("sub_agent", ...)` and let the server orchestrate the child agent's turns.

**B. "Think harder" tool**

Register a tool called `deep_think` (or similar) that the realtime model can call when it recognizes a question is beyond its capability:

```csharp
internal sealed class DeepThinkTool(IOpenAiTransport transport, RobotAppOptions options)
{
    public const string Name = "deep_think";

    public async Task<string> ExecuteAsync(string question, CancellationToken ct)
    {
        var conversation = new List<ChatMessage>
        {
            new SystemChatMessage("You are a careful reasoning assistant. Think step by step."),
            new UserChatMessage(question)
        };

        // Use a smarter model for this call
        var result = await transport.CompleteChatWithModelAsync(
            options.DeepThinkModel, // e.g., "o3" or "gpt-5"
            conversation,
            tools: null,
            ct);

        return result is TextResult text ? text.Text : "Unable to reason about this.";
    }
}
```

This requires adding `CompleteChatWithModelAsync` (or an overload) to `IOpenAiTransport` that takes an explicit model name rather than always using `appOptions.ChatModel`.

**C. Tracking and timeouts**

Add a `SubAgentTracker` singleton:

```csharp
internal sealed class SubAgentTracker
{
    private readonly ConcurrentDictionary<string, SubAgentExecution> _active = new();

    public string Spawn(SubAgentRequest request, Task<SubAgentResult> task) { ... }
    public SubAgentExecution? Get(string executionId) { ... }
    public IReadOnlyList<SubAgentExecution> GetAll() { ... }
}
```

This lets the main orchestrator check on sub-agents during heartbeat or between turns.

---

## 3. Personality

### What exists today

- `PersonalityCatalog` loads from `personalities.json`. Each personality is a single `instructions` string.
- `ToolPromptAugmenter.BuildSystemPrompt()` combines base instructions with camera tool guidance.
- The system prompt is `conversationHistory[0]` and gets swapped on personality switch.
- There is **no** SOUL.md / IDENTITY.md separation. No session-start greeting. No situational overlays.

### What's needed (per the openclaw-personality-findings.md)

The findings document lays out a clear architecture:

1. Keep the base system prompt **short and operational**
2. Put personality into a **separate durable persona document** (SOUL.md equivalent)
3. Add **session-start instructions** so the agent arrives in character
4. Separate **stable identity** from **situational behavior**
5. Add **channel/group-specific overlays**

### Recommended implementation

**A. Restructure `PersonalityDefinition`**

```csharp
internal sealed record PersonalityDefinition(
    string Id,
    string DisplayName,
    string CoreIdentity,        // Short: name, vibe, creature, emoji (IDENTITY.md equiv)
    string SoulInstructions,    // Durable persona/tone (SOUL.md equiv)
    string OperationalRules,    // Tool rules, response length, safety
    string? SessionGreeting,    // What to say/do on session start
    IReadOnlyList<string> SwitchPhrases);
```

Update `personalities.json` to have these sections rather than a single monolithic `instructions` field.

**B. Layered prompt assembly**

Replace `ToolPromptAugmenter.BuildSystemPrompt` with a richer builder:

```csharp
internal static class SystemPromptBuilder
{
    public static string Build(
        PersonalityDefinition personality,
        bool visionEnabled,
        string? situationalOverlay = null,  // e.g., "You are in a group demo" 
        string? sessionStartDirective = null)
    {
        var sb = new StringBuilder();

        // 1. Base operational prompt (short, stable)
        sb.AppendLine("You are a personal assistant running on a Reachy Mini robot.");
        sb.AppendLine();

        // 2. Identity
        if (!string.IsNullOrWhiteSpace(personality.CoreIdentity))
        {
            sb.AppendLine("# IDENTITY");
            sb.AppendLine(personality.CoreIdentity);
            sb.AppendLine();
        }

        // 3. Soul / persona (the main personality document)
        if (!string.IsNullOrWhiteSpace(personality.SoulInstructions))
        {
            sb.AppendLine("# PERSONA");
            sb.AppendLine("Embody the following persona and tone. Avoid stiff, generic assistant replies.");
            sb.AppendLine(personality.SoulInstructions);
            sb.AppendLine();
        }

        // 4. Operational rules
        if (!string.IsNullOrWhiteSpace(personality.OperationalRules))
        {
            sb.AppendLine("# RULES");
            sb.AppendLine(personality.OperationalRules);
            sb.AppendLine();
        }

        // 5. Tool awareness (existing logic)
        if (visionEnabled)
            sb.AppendLine(CameraToolGuidance);

        // 6. Situational overlay
        if (!string.IsNullOrWhiteSpace(situationalOverlay))
        {
            sb.AppendLine("# CURRENT SITUATION");
            sb.AppendLine(situationalOverlay);
        }

        return sb.ToString().Trim();
    }
}
```

**C. Session-start greeting**

In both orchestrators, after the conversation loop starts and before the first listen, if the personality has a `SessionGreeting`, speak it:

```csharp
if (!string.IsNullOrWhiteSpace(activePersonality.SessionGreeting))
{
    stateMachine.TransitionTo(InteractionState.Speaking, "session greeting");
    var greetingWav = await openAiTransport.GenerateSpeechWaveAsync(
        activePersonality.SessionGreeting, options.SpeechVoice, stoppingToken);
    await audioPlayback.PlayAsync(greetingWav, stoppingToken);
}
```

---

## 4. Knowledge and Persistence

### What exists today

- Conversation history is a `List<ChatMessage>` in `InteractionOrchestrator.ExecuteAsync`, trimmed to 12 messages.
- **Zero persistence.** When the process stops, all context is lost.
- No database, no vector store, no full-text search.

### Recommended implementation

**A. Server-side knowledge store**

This belongs on the server (`ReachTether.Server`), not on the robot. The robot has limited resources; the server has SQLite + extensions.

Server-side schema (SQLite):

```sql
-- Full-text knowledge entries
CREATE VIRTUAL TABLE knowledge_fts USING fts5(content, source, tags);

-- Vector embeddings (via sqlite-vss or similar)
CREATE VIRTUAL TABLE knowledge_vec USING vss0(embedding(1536));

-- Conversation memory
CREATE TABLE conversation_memory (
    id INTEGER PRIMARY KEY,
    session_id TEXT,
    robot_id TEXT,
    summary TEXT,
    created_at TEXT,
    embedding BLOB
);

-- Entity/fact store (lightweight graph-like)
CREATE TABLE facts (
    id INTEGER PRIMARY KEY,
    subject TEXT,
    predicate TEXT,
    object TEXT,
    confidence REAL,
    source_session TEXT,
    created_at TEXT
);
```

**B. Robot-side knowledge tool**

Add a `knowledge_query` tool the model can call:

```csharp
internal sealed class KnowledgeTool(IServerLink serverLink)
{
    public const string Name = "knowledge_query";

    public async Task<string> QueryAsync(string query, CancellationToken ct)
    {
        var result = await serverLink.InvokeToolAsync(Name, 
            new JsonObject { ["query"] = query }, ct);
        return result.ToJsonString();
    }
}
```

**C. Automatic context hydration**

Before each chat completion, the orchestrator could query the server for relevant knowledge:

```csharp
// In the interaction loop, after getting userInput:
var relevantKnowledge = await serverLink.InvokeToolAsync("knowledge_hydrate",
    new JsonObject { ["query"] = userInput, ["limit"] = 3 }, stoppingToken);

if (relevantKnowledge is not null)
{
    conversationHistory.Insert(1, new SystemChatMessage(
        $"# RELEVANT KNOWLEDGE\n{relevantKnowledge}"));
}
```

**D. Conversation persistence**

After each completed turn, push a summary to the server:

```csharp
await serverLink.InvokeToolAsync("memory_store", new JsonObject
{
    ["user_input"] = userInput,
    ["assistant_response"] = response,
    ["session_id"] = sessionId
}, stoppingToken);
```

---

## 5. Always On

### What exists today

- Both orchestrators run as `BackgroundService` instances. When the conversation ends (`continueConversation = false`), they call `appLifetime.StopApplication()` — the **entire host shuts down**.
- `IsShutdownIntent` regex patterns detect "goodbye"/"exit" and trigger a full shutdown.
- There is no heartbeat, no session persistence, no cron, no system event queue.
- The state machine (`InteractionStateMachine`) has states: `Idle, Listening, Thinking, Speaking, Interrupted` — but "Idle" doesn't do anything; it's just a label before the next listen.

### What's needed (per the openclaw-always-on-findings.md)

The key ideas:
1. **Long-running runtime** that owns transports and session state
2. **Persistent sessions** reused across interactions
3. **Periodic heartbeat turns** with suppressed no-op output
4. **System event queue** for background work to surface later
5. **Cron/scheduler** as first-class features
6. **Serialized execution** per session

### Recommended implementation

**A. Don't shut down on conversation end**

The most important change. In both orchestrators, replace the shutdown-on-goodbye with a return to an idle/sleep state:

```csharp
if (IsShutdownIntent(userInput))
{
    // Speak farewell
    // ...
    
    // Instead of: continueConversation = false;
    // Do: enter low-power idle, await next voice activation
    stateMachine.TransitionTo(InteractionState.Sleeping, "user farewell");
    await reachyClient.Move.GotoSleepAsync(stoppingToken);
    
    // Wait for wake word or scheduled event
    await WaitForWakeEventAsync(stoppingToken);
    
    await reachyClient.Move.WakeUpAsync(stoppingToken);
    stateMachine.TransitionTo(InteractionState.Idle, "wake event");
    // Loop continues
}
```

Add new states to `InteractionState`:

```csharp
internal enum InteractionState
{
    Idle,
    Sleeping,      // Robot is asleep but process is alive
    Listening,
    Thinking,
    Speaking,
    Interrupted,
    HeartbeatCheck  // Periodic awareness turn
}
```

**B. Heartbeat service**

```csharp
internal sealed class HeartbeatService(
    IServerLink serverLink,
    IInteractionStateMachine stateMachine,
    RobotAppOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(options.Server.HeartbeatIntervalMs);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(interval, stoppingToken);
            
            if (stateMachine.Current is InteractionState.Speaking or InteractionState.Listening)
                continue; // Don't interrupt active conversation
            
            // Check for pending system events from server
            var events = await serverLink.InvokeToolAsync("system_events_poll",
                new JsonObject(), stoppingToken);
            
            if (HasActionableEvents(events))
            {
                // Inject into next conversation turn
                EnqueueSystemContext(events);
            }
            
            // Push robot state to server
            await serverLink.PushStateAsync(BuildStateSnapshot(), stoppingToken);
        }
    }
}
```

**C. System event queue**

```csharp
internal sealed class SystemEventQueue
{
    private readonly ConcurrentQueue<SystemEvent> _events = new();

    public void Enqueue(SystemEvent evt) => _events.Enqueue(evt);
    
    public IReadOnlyList<SystemEvent> DrainAll()
    {
        var events = new List<SystemEvent>();
        while (_events.TryDequeue(out var evt))
            events.Add(evt);
        return events;
    }
}

internal sealed record SystemEvent(
    string Source,      // "cron", "server", "sub_agent", etc.
    string Summary,     // Human-readable for injection into prompt
    DateTimeOffset CreatedAt,
    JsonObject? Payload);
```

In the interaction loop, before each turn:

```csharp
var pendingEvents = systemEventQueue.DrainAll();
if (pendingEvents.Count > 0)
{
    var eventContext = string.Join("\n", pendingEvents.Select(e => $"[{e.Source}] {e.Summary}"));
    conversationHistory.Insert(1, new SystemChatMessage(
        $"# SYSTEM EVENTS SINCE LAST TURN\n{eventContext}"));
}
```

**D. Session persistence**

Store session state to disk so it survives process restarts:

```csharp
internal interface ISessionStore
{
    Task SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken ct);
    Task<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken ct);
}

internal sealed record SessionSnapshot(
    string SessionId,
    string PersonalityId,
    List<SerializedMessage> Messages,
    DateTimeOffset LastActiveUtc);
```

Use a simple JSON file under a `sessions/` directory (mirroring OpenClaw's `~/.openclaw/agents/<agentId>/sessions/` pattern).

---

## Implementation Priority & Dependency Graph

```
Phase 1 (Foundation):
  ├─ Always On: Remove shutdown-on-goodbye, add Sleeping state
  ├─ Personality: Restructure PersonalityDefinition, layered prompt builder
  └─ Session persistence: Save/restore conversation across sleep cycles

Phase 2 (Server):
  ├─ ReachTether.Server Blazor project (skeleton + SignalR hub)
  ├─ IServerLink robot-side client
  ├─ Video streaming (JPEG-over-SignalR)
  └─ Heartbeat service

Phase 3 (Intelligence):
  ├─ Server-hosted tools (KinectShot, scheduler)
  ├─ Knowledge store (SQLite + FTS + vector)
  ├─ Automatic context hydration
  └─ Sub-agents + DeepThink tool

Phase 4 (Polish):
  ├─ System event queue + cron
  ├─ Camera snapshots tagged in desktop UI
  └─ Session-start greetings, situational overlays
```

Phase 1 has **zero external dependencies** — it's all refactoring existing code. Phase 2 introduces the new server project. Phase 3 builds on the server. Phase 4 ties everything together.

The biggest single change is **removing the `appLifetime.StopApplication()` calls** from both orchestrators and restructuring the main loop to be an always-running state machine that transitions between `Sleeping ↔ Idle ↔ Listening ↔ Thinking ↔ Speaking` rather than a linear conversation that terminates.