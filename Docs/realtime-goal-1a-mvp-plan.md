# Realtime Goal 1A MVP Plan (Decouple Event Loop)

This is the **trimmed implementation plan** for Goal 1A only.
It is intended for fast delivery and low-risk refactoring of the current realtime turn loop.

For full roadmap architecture (SmartyMode, RAG, Face Rec, dynamic context), see:
- `Docs/realtime-orchestrator-refactoring-plan.md`

---

## 1. Context

Current issue:
- `RealtimeInteractionOrchestrator.RunRealtimeTurnAsync` owns a large `switch (update)` with mixed responsibilities (speech boundaries, playback, transcript aggregation, response lifecycle, failure handling).

Why this MVP exists:
- Improve maintainability and extension points **without changing behavior**.
- Avoid over-design before tool execution and dynamic context are implemented.

---

## 2. Scope

### In Scope
1. Extract mutable turn state into a dedicated object.
2. Introduce an event-handler interface for realtime updates.
3. Replace `switch` body with ordered handler dispatch.
4. Preserve current behavior, logs, and timeouts.

### Out of Scope
1. Dynamic context (`IContextBuilder`, `DynamicContextService`).
2. Tool execution pipeline (`IToolExecutor`, SmartyMode logic).
3. Session control abstraction (`ISessionControlService`).
4. Personality model/schema changes.

---

## 3. Minimal Architecture

### A. `RealtimeTurnState`
Holds existing per-turn mutable fields currently local to `RunRealtimeTurnAsync`:
- `UserTranscript`
- `ActiveResponseId`
- `SpeechStarted`, `SpeechStopped`, `ResponseStarted`
- `StreamOpen`, `StreamFinalized`, `StreamedAudioPlayback`
- `DropActiveResponseAudio`, `SuppressResponseForShutdownIntent`
- `TranscriptionFailureReason`
- `SpeechStartTime`, `SpeechEndTime`
- `AssistantText` (or keep in context output object)

### B. `IRealtimeEventHandler`

```csharp
internal interface IRealtimeEventHandler
{
    int Order { get; }
    ValueTask<bool> HandleAsync(ConversationUpdate update, RealtimeTurnContext context, CancellationToken ct);
}
```

- `Order`: deterministic execution.
- `bool` return: `true` means handled; dispatcher may continue so multiple handlers can act on one update.

### C. `RealtimeTurnContext`

Contains:
- `RealtimeTurnState State`
- dependencies used by handlers (audio session, motion orchestrator, state machine, logger, options)
- helper methods for failure/result completion

Keep it internal to `ReachTether.Robot`.

### D. Initial Handler Set (MVP)

1. `SpeechBoundaryHandler`
- Handles `ConversationInputSpeechStartedUpdate`, `ConversationInputSpeechFinishedUpdate`.

2. `TranscriptionHandler`
- Handles `ConversationInputTranscriptionFinishedUpdate`, `ConversationInputTranscriptionFailedUpdate`.

3. `StreamingAudioHandler`
- Handles `ConversationItemStreamingPartDeltaUpdate` audio/text accumulation.

4. `ResponseLifecycleHandler`
- Handles `ConversationResponseStartedUpdate`, `ConversationResponseFinishedUpdate`, `ConversationErrorUpdate`.

Do not add tool-call handling in this MVP.

---

## 4. Implementation Steps

1. Create new internal types:
- `RealtimeTurnState`
- `RealtimeTurnContext`
- `IRealtimeEventHandler`

2. Add handlers for the 4 MVP areas above.

3. In `RunRealtimeTurnAsync`, replace `switch (update)` with:
- `foreach` handlers ordered by `Order`
- handler invocation with shared context

4. Keep existing timeout and stream-read loop logic unchanged.

5. Keep existing return shape (`RealtimeTurnResult`) unchanged.

6. Verify no behavior drift with manual run + focused tests.

---

## 5. Files to Touch

Primary:
- `dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs`

Likely new files:
- `dotNet/ReachTether.Robot/Realtime/RealtimeTurnState.cs`
- `dotNet/ReachTether.Robot/Realtime/RealtimeTurnContext.cs`
- `dotNet/ReachTether.Robot/Realtime/IRealtimeEventHandler.cs`
- `dotNet/ReachTether.Robot/Realtime/Handlers/*.cs`

Optional registration location (if constructor-injected list is used):
- `dotNet/ReachTether.Robot/Program.cs`

---

## 6. Acceptance Criteria (MVP)

1. `RunRealtimeTurnAsync` no longer contains event-specific business `switch` logic.
2. Existing user-visible behavior remains equivalent:
- same speech start/stop behavior
- same streaming playback behavior
- same shutdown-intent suppression behavior
- same timeout/failure handling
3. No new blocking I/O added in hot update path.
4. No net increase in per-audio-chunk allocations beyond baseline.

---

## 7. Follow-On (After MVP)

After this lands, add:
1. `ToolCallHandler` + tool execution (SmartyMode).
2. Session mutation abstraction for pause/resume/injection.
3. Dynamic context (Goal 1B).

Those are intentionally deferred to keep Goal 1A small and shippable.
