# Realtime Orchestrator & Dynamic Context Refactoring Plan

This document details the implementation of **Solution 1: Critical Architecture Changes** to support the ReachTether roadmap, including Tool Use (SmartyMode), RAG, and Face Recognition.

## 1. Goal: Decouple the Realtime Event Loop
The current `RealtimeInteractionOrchestrator.RunRealtimeTurnAsync` method is a monolithic loop that manually handles low-level OpenAI Realtime API events. This makes adding complex logic like tool execution or vision-based interruptions extremely difficult.

### Architecture: `IRealtimeEventHandler` Strategy Pattern

We will extract the event-handling logic into a set of specialized handlers. The orchestrator will act as a "Dispatcher" that maintains the high-level turn state and delegates specific event logic.

#### A. Define the Event Handler Interface
```csharp
public interface IRealtimeEventHandler
{
    Task HandleUpdateAsync(ConversationUpdate update, RealtimeTurnContext context, CancellationToken ct);
}
```

#### B. Create a `RealtimeTurnContext`
This object will encapsulate the mutable state of a single turn (e.g., `AssistantText`, `UserTranscript`, `IsStreamOpen`, `ResponseId`), replacing the local variables currently in `RunRealtimeTurnAsync`.

#### C. Specialized Handlers
1. **`TranscriptionHandler`**: Handles `ConversationInputTranscriptionFinishedUpdate`. Checks for "Shutdown" or "Personality Switch" intents.
2. **`AudioPlaybackHandler`**: Handles `ConversationItemStreamingPartDeltaUpdate` for audio. Manages `audioSession.WritePlaybackPcm16Chunk` and talking gestures.
3. **`ToolCallHandler` (SmartyMode)**: **New.** Handles `ConversationItemStreamingPartFinishedUpdate` where the part is a function call. It will execute the local C# tool and send the result back to the session.
4. **`StateTransitionHandler`**: Manages transitions for the `IInteractionStateMachine` (e.g., moving from `Thinking` to `Speaking`).

### Implementation Steps
1. **Extract State:** Create a `RealtimeTurnState` class to hold the flags (`speechStarted`, `responseStarted`, etc.).
2. **Implement Dispatcher:** Refactor the `switch (update)` in `RunRealtimeTurnAsync` to iterate through a collection of `IRealtimeEventHandler`.
3. **Migrate Logic:** Move the 300+ lines of switch-case logic into the new handlers.

### Missing Details Added: Dispatcher Contract and Ordering

The dispatcher should run handlers in deterministic order and support "consume" semantics:

```csharp
public interface IRealtimeEventHandler
{
    int Order { get; } // lower runs first
    ValueTask<RealtimeHandleResult> HandleUpdateAsync(
        ConversationUpdate update,
        RealtimeTurnContext context,
        CancellationToken ct);
}

public readonly record struct RealtimeHandleResult(
    bool Handled,
    bool StopDispatch = false);
```

Recommended order:
1. `ProtocolErrorHandler` (terminal protocol/API failures first)
2. `SpeechBoundaryHandler` (`InputSpeechStarted/Finished`)
3. `TranscriptionHandler` (transcript, shutdown/switch intent detection)
4. `ToolCallHandler` (function calls and tool output writeback)
5. `AudioPlaybackHandler` (stream output audio)
6. `AssistantTextAggregationHandler` (text/audio transcript accumulation)
7. `ResponseLifecycleHandler` (`ResponseStarted/Finished`)
8. `StateTransitionHandler` (state machine updates based on context flags)

This avoids racey behavior (for example, tool calls being evaluated after stream completion).

### Missing Details Added: RealtimeTurnContext Shape

`RealtimeTurnContext` should encapsulate both mutable state and side-effect operations:

```csharp
public sealed class RealtimeTurnContext
{
    public required RealtimeTurnState State { get; init; }
    public required RealtimeTurnServices Services { get; init; }
    public required RealtimeTurnOutputs Outputs { get; init; }
}
```

- `RealtimeTurnState`: turn flags (`SpeechStarted`, `ResponseStarted`, `SuppressResponse`, `ActiveResponseId`, `UserTranscript`, etc.)
- `RealtimeTurnServices`: typed dependencies (`LocalAudioSession`, `IMotionOrchestrator`, `IInteractionStateMachine`, `IToolExecutor`, `ISessionControlService`)
- `RealtimeTurnOutputs`: `StringBuilder AssistantText`, failure reason setter, completion signal

Do not keep DI service resolution inside handlers; all dependencies are injected at construction time.

### Missing Details Added: Session Control Extraction (Required for SmartyMode)

Add `ISessionControlService` to centralize all realtime session mutations:

```csharp
public interface ISessionControlService
{
    Task UpdateInstructionsAsync(string instructions, CancellationToken ct);
    Task PauseInputAudioAsync(CancellationToken ct);
    Task ResumeInputAudioAsync(CancellationToken ct);
    Task CancelActiveResponseAsync(CancellationToken ct);
    Task InjectToolResultAsync(string callId, string outputJson, CancellationToken ct);
}
```

`ToolCallHandler` must:
1. Pause input audio / suspend VAD-sensitive behavior.
2. Execute tool with timeout budget.
3. Inject tool result back into realtime conversation.
4. Resume input audio.

This keeps tool execution from scattering `ConfigureSessionAsync`, cancellation, and stream control logic across handlers.

---

## 2. Goal: Abstract the "Context" (Dynamic System Prompts)
Currently, the "System Prompt" is a static string loaded from `personalities.json`. To support **RAG** (external knowledge) and **Face Recognition** (user-specific memory), the prompt must be built dynamically right before a session starts or a turn begins.

### Architecture: `IContextBuilder` & `DynamicContextService`

#### A. Refactor `PersonalityDefinition`
Change `Instructions` from a static `string` to a more flexible structure.
```csharp
public interface IContextBuilder
{
    Task<string> BuildInstructionsAsync(PersonalityDefinition personality, ContextData data);
}
```

#### B. Implement `DynamicContextService`
A central service that aggregates data from various "Context Providers":
- **`IdentityContextProvider`**: Injected by the (future) Face Recognition service. Returns "The user is John."
- **`KnowledgeContextProvider`**: Injected by the (future) RAG service. Returns "Fact: Reachy Mini was released in 2024."
- **`TemporalContextProvider`**: Returns current date, time, and location.

### Implementation Steps
1. **Define `ContextData`:** A simple DTO containing `UserId`, `Location`, and `RecentEvents`.
2. **Update `BuildSessionOptions`:** Modify `RealtimeInteractionOrchestrator` to call `_contextService.GetActiveInstructionsAsync()` instead of reading `activePersonality.Instructions` directly.
3. **Refactor `PersonalityCatalog`:** Update the loader to support either a raw string (default) or a named context template.

### Missing Details Added: Context Provider Contracts

Use composable providers instead of one large context service:

```csharp
public interface IContextProvider
{
    int Order { get; }
    ValueTask<ContextContribution?> GetContributionAsync(ContextData data, CancellationToken ct);
}

public sealed record ContextContribution(
    string SectionName,
    string Content,
    bool Ephemeral = true);
```

`DynamicContextService`:
- gathers provider contributions in `Order`
- drops empty contributions
- enforces token/length budget per section
- calls `IContextBuilder` to render final instructions

### Missing Details Added: Refresh Policy

Define when instructions are rebuilt:
1. Realtime session startup
2. Personality switch
3. Identity change (face recognized/unknown transition)
4. RAG context change (new retrieval result attached)
5. Explicit tool signal (`refresh_context`)

Avoid rebuilding every update event; use change-triggered refresh to limit churn and latency.

### Missing Details Added: Personality Compatibility

To avoid breaking existing `personalities.json`, support both:
- legacy: `instructions` string
- template mode: `instructionsTemplate` + optional `contextSections`

Template mode is opt-in per personality.

---

## 3. Rollout Plan (Incremental, Low Risk)

### Phase 1: Mechanical Refactor (No Behavior Change)
1. Introduce `RealtimeTurnState`, `RealtimeTurnContext`, `IRealtimeEventHandler`.
2. Move existing switch branches into handlers one-by-one.
3. Keep current console outputs, timeouts, and shutdown behavior unchanged.

### Phase 2: Session Control + Tool Skeleton
1. Add `ISessionControlService`.
2. Add no-op `IToolExecutor` and wire `ToolCallHandler`.
3. Validate turn loop remains stable when no tools are called.

### Phase 3: Dynamic Context
1. Add `IContextProvider`, `IContextBuilder`, `DynamicContextService`.
2. Route `BuildSessionOptions` through dynamic instructions.
3. Enable `TemporalContextProvider` first, then Identity and Knowledge providers.

### Phase 4: Feature Activation
1. Enable SmartyMode tool execution.
2. Enable RAG provider/tool path.
3. Enable face-recognition-driven identity provider.

---

## 4. File Mapping (Current Codebase)

Use this as the starting file list for implementation:

1. Realtime loop extraction and dispatcher wiring:
   - `dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs`
2. Session mutation abstraction:
   - `dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs` (initial implementation)
   - `dotNet/ReachTether.Robot/Program.cs` (DI registration)
3. Dynamic context interfaces and service:
   - `dotNet/ReachTether.Robot/PersonalityCatalog.cs`
   - `dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs`
   - `dotNet/ReachTether.Robot/Program.cs`
4. Tool execution contracts (SmartyMode-ready):
   - `dotNet/ReachTether.Robot/OpenAiTransport.cs`
   - `dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs`
5. Shared state/transition usage:
   - `dotNet/ReachTether.Robot/InteractionStateMachine.cs`
   - `dotNet/ReachTether.Robot/MotionOrchestrator.cs`

Keep initial extraction internal to `ReachTether.Robot` and avoid cross-project moves in the first pass.

---

## 5. Test and Validation Requirements

### Unit Tests
1. Handler dispatch order and stop-dispatch behavior.
2. Tool call lifecycle: pause -> execute -> inject result -> resume.
3. Context rebuild triggers and template rendering.
4. Personality loader backward compatibility (`instructions` vs template mode).

### Integration Tests
1. Realtime streaming playback still starts/stops correctly on barge-in.
2. Session reset and recovery still work after update-stream failures.
3. Personality switch updates session instructions without restarting host.
4. Tool timeout/failure returns safe assistant response and does not deadlock the turn.

### Non-Functional Checks
1. No additional allocations in hot audio path beyond existing baseline.
2. No handler performs blocking I/O on the realtime update thread.
3. Structured logs include `turnId`, `responseId`, `toolCallId`, and `personalityId`.

---

## 6. Definition of Done

1. `RunRealtimeTurnAsync` no longer owns event-specific business logic directly.
2. New handlers are individually testable and DI-registered.
3. Dynamic instructions are built from providers and applied via session control.
4. SmartyMode can run as a tool without modifying orchestrator switch branches.
5. Existing shutdown/personality-switch UX remains functionally equivalent.

---

## 7. Benefits for the Roadmap

| Feature | How this architecture enables it |
| :--- | :--- |
| **SmartyMode** | The `ToolCallHandler` can pause the Realtime session, call a "Slower/Smarter" model via `OpenAiTransport`, and inject the text result as a new conversation item. |
| **Video/Vision** | A background `VisionService` can push "Visual Events" into the `RealtimeTurnContext`, which handlers can use to modify the robot's behavior mid-turn. |
| **RAG** | The `KnowledgeContextProvider` can query the ReachTether.Server and append the findings to the system prompt before the connection is established. |
| **Face Rec** | When a face is detected, the `IdentityContextProvider` updates its state. The next time `BuildInstructionsAsync` is called, the AI is told exactly who it is talking to. |
