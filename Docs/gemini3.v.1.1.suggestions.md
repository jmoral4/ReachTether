## Model: gemini-3.1-pro-preview

Based on my review of the `ReachTether` codebase and the `Docs/v1.1.md` requirements, migrating to v1.1 requires shifting from a purely local, isolated console application to a **hybrid edge-cloud architecture**. 

Here is a comprehensive, actionable plan on how to implement the v1.1 features within the existing .NET architecture.

---

### 1. Server Offload and Streaming
**Goal:** Introduce a remote Blazor app for UI, video streaming, and remote tool execution.

**Implementation Strategy:**
*   **SignalR Communication Hub:** Add a SignalR Client to the `ReachTether.Robot` project. This will maintain a persistent 2-way connection to the remote Blazor Server.
*   **Video Streaming:** You already have an `ICameraSnapshotProvider`. Instead of heavy WebRTC video relaying through the .NET app, you can create a background service that captures snapshots at a low framerate (e.g., 5 FPS) and sends them to the Blazor app via SignalR (or a dedicated gRPC stream) as Base64/JPEG payloads.
*   **Snapshot Desktop UI Sync:** Modify `CameraTool.cs`. When `ExecuteAsync()` is called, fire a SignalR message: `await _hubConnection.SendAsync("BroadcastSnapshot", result.ImageDataUrl, question);`. The Blazor app listens to this and displays it on the UI.
*   **Remote Tool Execution:** 
    *   Currently, `CameraTool` is hardcoded in `FunctionCallHandler.cs`. 
    *   Create a `RemoteToolProvider` that fetches available tools from the Blazor Server upon startup.
    *   Inject these tools into the `RealtimeConversationSession` initialization.
    *   In `FunctionCallHandler.cs`, if a tool name isn't `camera`, route it to the server:
      ```csharp
      // In FunctionCallHandler.cs
      if (isRemoteTool) {
          var remoteResult = await _hubConnection.InvokeAsync<string>("ExecuteServerTool", functionName, functionCallArguments);
          outputPayload = remoteResult;
      }
      ```

### 2. Sub-agents and Smart Models
**Goal:** Offload complex thinking and spawn tracked sub-agents.

**Implementation Strategy:**
*   The OpenAI Realtime model (`gpt-4o-realtime-preview`) is fast but has reasoning limitations. 
*   **Smart Offload Tool:** Expose a tool to the Realtime model called `deep_think` or `research_topic`. 
*   When the Realtime model calls `deep_think(prompt)`, route this to the Blazor Server (via the remote tool pipeline above).
*   The Blazor Server spins up a standard LangChain/SemanticKernel agent using `o1-preview` or `gpt-4o`, does the heavy lifting, tracks timeouts, and returns the final synthesized string back to the robot's Realtime session.
*   To the robot's Realtime session, it simply looks like a tool call that took a few seconds and returned a highly intelligent answer, which it will then read out loud.

### 3. Always On
**Goal:** Implement continuous awareness, session persistence, heartbeats, and system events.

**Implementation Strategy:**
*   **System Event Queue:** Add a `ConcurrentQueue<string> SystemEvents` to `RealtimeTurnState`. Allow the SignalR connection or background cron jobs to enqueue events here (e.g., "The server says John's meeting is starting").
*   **Event Injection:** In `RealtimeInteractionOrchestrator.cs` inside the main `while` loop, check the queue. If an event exists, manually inject it into the Realtime session:
    ```csharp
    if (turnState.SystemEvents.TryDequeue(out var sysEvent)) {
        await realtimeSession.AddItemAsync(ConversationItem.CreateUserMessage($"[SYSTEM NOTIFICATION]: {sysEvent}"));
        await realtimeSession.StartResponseAsync();
    }
    ```
*   **Heartbeats:** Create a `HeartbeatWorker : BackgroundService`. Every 30 minutes, it enqueues a system event: `[HEARTBEAT] Review your context. If nothing needs the user's attention, reply ONLY with 'HEARTBEAT_OK'.`
*   **Suppressing Silent Heartbeats:** In `StreamingAudioHandler.cs`, buffer the first few text tokens. If the response starts with `HEARTBEAT_OK`, set a flag `DropActiveResponseAudio = true` and `SuppressResponseForShutdownIntent = true` to prevent the robot from actually speaking.
*   **Session State Persistence:** Modify `OpenAiTransport` and `InteractionOrchestrator` to fetch conversation history (`List<ChatMessage>`) from the Blazor server on boot, rather than starting fresh.

### 4. Personality (OpenClaw Layered Architecture)
**Goal:** Move away from hardcoded JSON blobs to a layered, file-based persona system.

**Implementation Strategy:**
*   **Deprecate `personalities.json`:** Update `PersonalityCatalog.cs` to scan a `Personalities/` directory.
*   **Directory Structure:**
    ```text
    Personalities/
    └── Default/
        ├── IDENTITY.md  (Name, vibe, voice config)
        └── SOUL.md      (Core behaviors, humor style, rules)
    ```
*   **Dynamic Prompt Building:** Refactor `ToolPromptAugmenter.cs` into a `SystemPromptBuilder` that constructs the prompt dynamically at runtime:
    ```csharp
    var promptBuilder = new StringBuilder();
    promptBuilder.AppendLine("You are a personal assistant running inside ReachTether.");
    promptBuilder.AppendLine(File.ReadAllText("Personalities/Default/SOUL.md"));
    promptBuilder.AppendLine(GetSituationalOverlays(currentState)); // e.g., "You just woke up."
    promptBuilder.AppendLine(CameraToolGuidance);
    ```
*   **Session Start Instructions:** When the robot boots, feed a one-time system message: *"You have just booted up. Embody your SOUL.md persona and greet the user."* to immediately establish the character.

### 5. Knowledge and Persistence
**Goal:** RAG, Vector DB, and long-term memory.

**Implementation Strategy:**
*   **Server-Side DB:** Host SQLite (with `sqlite-vss` for vectors) or a lightweight ChromaDB on the Blazor server.
*   **Robot Interface Tools:** Provide the robot with two remote tools:
    *   `remember_fact(topic, details)` -> Sends to server to store in VectorDB.
    *   `recall_memory(query)` -> Sends to server to perform a similarity search and returns matching context.
*   **Hydration:** On startup, the robot sends an API call to the server to get "Core Facts" (e.g., user's name, current location, robot's previous state) and appends this to the bottom of the `SystemPromptBuilder`.

---

### Suggested Refactoring Roadmap for `ReachTether`

To get started, I recommend the following step-by-step refactoring inside your `Program.cs` and DI container:

1. **Add SignalR Client:**
   Add `Microsoft.AspNetCore.SignalR.Client` to the project.
   Register a singleton `HubConnection` pointing to your Blazor app.

2. **Refactor `ToolDefinition` handling in `RealtimeInteractionOrchestrator.cs`:**
   Instead of injecting just `CameraTool`, inject an `IEnumerable<IToolProvider>`. This allows you to easily add a `ServerRemoteToolProvider` that dynamically registers tools the server exposes.

3. **Modify `IRealtimeEventHandler` pipeline:**
   Create a new handler `SystemEventQueueHandler` that checks for pending cross-thread events (like heartbeats or Blazor UI commands) and safely coordinates with `RealtimeTurnContext` to trigger `StartResponseAsync()`.

4. **Update `PersonalityCatalog.cs`:**
   Rewrite the `Load()` method to parse directories and Markdown files instead of `Personalities.json`, mapping `IDENTITY.md` metadata (like default voice) to the `RobotAppOptions`.text: ""
