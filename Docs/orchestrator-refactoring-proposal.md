# Orchestrator Refactoring Proposal

## Purpose

This document captures the current architectural findings around `InteractionOrchestrator` and
`RealtimeInteractionOrchestrator`, and proposes a refactoring path that will keep the codebase
maintainable as vision, tool execution, and other cross-cutting features are added.

This is an investigation-only proposal. It does not assume any code changes have been made yet.

---

## Findings

### 1. The orchestrators currently own too many responsibilities

`InteractionOrchestrator` and `RealtimeInteractionOrchestrator` are both acting as:

- application lifecycle coordinators
- robot startup/shutdown coordinators
- audio session managers
- interaction state coordinators
- personality-switch handlers
- robot pose/motion cue coordinators
- conversation turn runners
- error recovery points

In the realtime case, the orchestrator also owns:

- realtime session creation/reset/disposal
- outbound audio preprocessing and streaming
- inbound realtime event dispatch
- timeout and failure policy
- streamed audio playback coordination

That is already a wide responsibility surface before tool execution or vision features are present.

### 2. Vision is not yet represented as an application-level capability boundary

The current camera path is real and working, but the app-layer integration is still mostly a startup
probe:

- `ReachyMini.Sdk.CameraClient` provides camera access
- `VisionStartupProbe` warms and probes the camera after robot ready
- the orchestrators do not yet consume vision via a reusable robot-layer abstraction

This means future camera tool support would likely get added directly into orchestration code unless
an intermediate seam is introduced first.

### 3. The SDK camera implementation is self-contained, but broad

`ReachyMini.Sdk.CameraClient` is reasonably isolated from the robot app, which is good. However, it
currently owns many concerns:

- source normalization and validation
- pipeline selection
- pipeline lifecycle
- retry behavior
- sample draining
- raw byte extraction
- JPEG encoding
- timing metrics

That is acceptable for the current Phase 0 state, but it is not ideal as the only long-term seam for
vision-related behavior.

### 4. Current configuration is shaped around probing, not product behavior

`RobotAppOptions.VisionSettings` currently emphasizes:

- warmup-on-startup
- probe-on-startup
- probe-only
- source configuration
- capture timeouts

This is appropriate for integration/debugging, but it is not the eventual product-facing shape for
vision features such as:

- on-demand camera tool usage
- snapshot caching
- ambient scene context
- tracking

### 5. Realtime orchestration is the highest maintainability risk

The legacy orchestrator is large, but still understandable as a single turn-based loop.

The realtime orchestrator is more fragile because it combines:

- long-lived session state
- concurrent audio send behavior
- async event stream processing
- playback state transitions
- timeout policy
- recovery/reset logic
- direct robot behavior changes

If camera tools or future vision-triggered behaviors are added directly there, the complexity will
increase quickly.

---

## Overall Assessment

The current code is good enough for proving the camera path and for continuing exploration.

It is not yet properly abstracted for long-term feature growth.

The main problem is not that the architecture is fundamentally wrong. The problem is that the
orchestrators are still too close to implementation details, and there is not yet a shared
application-layer seam for turn execution, tool handling, and vision access.

---

## Refactoring Goals

The refactor should aim to make each orchestrator primarily responsible for:

- host/service lifetime
- top-level mode selection
- fatal error boundaries

Everything else should move into focused services.

Concretely, we want:

1. shared startup/shutdown behavior in one place
2. turn execution behind explicit interfaces
3. personality handling isolated from transport loops
4. robot cue/pose logic isolated from conversation control
5. tool execution shared across legacy and realtime flows
6. vision consumed through a robot-layer abstraction rather than directly from the orchestrators

---

## Proposed Target Architecture

### 1. `RobotRuntimeCoordinator`

Responsibility:

- wake robot
- connect audio session
- wait for daemon ready
- run startup vision warmup/probe
- move robot to neutral pose
- perform orderly shutdown

Why:

- startup/shutdown code is duplicated conceptually across both orchestrators
- this is a natural shared runtime boundary

### 2. `ConversationLoopService`

Responsibility:

- own the outer interaction loop
- coordinate listen -> execute turn -> render result -> reset state

Why:

- the hosted service should not also be the detailed interaction policy object
- this makes the `BackgroundService` class a thin adapter

### 3. `ITurnExecutor`

Suggested shape:

```csharp
public interface ITurnExecutor
{
    Task<TurnOutcome> ExecuteTurnAsync(
        TurnContext context,
        CancellationToken cancellationToken);
}
```

Implementations:

- `LegacyTurnExecutor`
- `RealtimeTurnExecutor`

Why:

- this becomes the main architectural seam between the legacy and realtime interaction paths
- it keeps transport-specific behavior out of the host-level orchestrator

### 4. `TurnContext`

Responsibility:

- carry turn-scoped dependencies and state

Possible contents:

- active personality/session info
- state machine
- motion services
- audio services
- tool handler
- response presenter
- relevant options

Why:

- avoids passing the entire dependency graph through every method
- gives both turn executors a stable contract

### 5. `IPersonalitySessionService`

Responsibility:

- resolve personality switch commands
- provide active instructions
- update session configuration when needed

Why:

- personality logic is currently mixed into the turn loops
- the realtime path in particular should not own instruction reconfiguration directly

### 6. `IRobotCueService`

Responsibility:

- listening pose
- thinking pose
- speaking pose
- confused pose
- farewell pose
- neutral pose

Why:

- these cues are currently inline and duplicated in orchestration code
- later vision-based look behavior or motion composition should attach here, not to the orchestrators

### 7. `IToolCallHandler`

Suggested shape:

```csharp
public interface IToolCallHandler
{
    Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyList<ToolCall> toolCalls,
        CancellationToken cancellationToken);
}
```

Why:

- both legacy and realtime flows will need tool handling
- tool execution should not live in transport classes
- a shared handler is the correct place for camera tool support

### 8. `ICameraSnapshotProvider` or `IVisionService`

Suggested shape:

```csharp
public interface ICameraSnapshotProvider
{
    Task<CameraSnapshot?> CaptureSnapshotAsync(
        CancellationToken cancellationToken = default);
}
```

Why:

- tool handlers should depend on a robot-layer vision abstraction
- orchestrators and turn executors should not know about `reachyClient.Camera` directly

### 9. `IResponsePresenter`

Responsibility:

- render model output for the user

Examples:

- legacy path: generate TTS and play it
- realtime path: use streamed audio if present, fallback to TTS if not

Why:

- output presentation is separate from turn execution
- transport-specific response behavior is easier to test and evolve if isolated

---

## Proposed Refactoring Sequence

### Phase A: Extract runtime coordination

First extract:

- robot startup/wake logic
- daemon-ready check
- startup vision warmup/probe
- shutdown/sleep logic

Target:

- `RobotRuntimeCoordinator`

Expected benefit:

- immediate reduction in duplicated top-level orchestration code
- cleaner startup/shutdown boundary

### Phase B: Extract robot cues

Move pose choreography into:

- `RobotCueService`

Expected benefit:

- removes repeated inline motion code
- creates a stable place for future motion composition

### Phase C: Isolate turn execution

Move:

- legacy turn body into `LegacyTurnExecutor`
- realtime turn body into `RealtimeTurnExecutor`

Expected benefit:

- clear separation between host lifecycle and turn behavior
- explicit legacy vs realtime contracts
- tool integration has a natural insertion point

### Phase D: Introduce shared tool execution

Add:

- `IToolCallHandler`
- concrete camera tool handler later

Expected benefit:

- one tool model for both legacy and realtime
- avoids duplicating camera tool behavior in two orchestration paths

### Phase E: Introduce shared vision abstraction

Add:

- `ICameraSnapshotProvider`
- `CameraSnapshotService`

Expected benefit:

- vision becomes an application capability rather than an SDK detail
- caching or JPEG strategy changes can be made in one place

### Phase F: Clean up state ownership

Replace scattered local variables with explicit state objects where useful:

- `ConversationSessionState`
- existing `RealtimeTurnState` remains the directionally correct model for realtime turn state

Expected benefit:

- fewer implicit invariants in long methods
- easier reasoning about state transitions and failure cases

---

## What Should Not Be Done

### 1. Do not merge legacy and realtime into one giant orchestrator

They should share abstractions, not collapse into one monolith.

### 2. Do not move tool execution into `OpenAiTransport`

`OpenAiTransport` should remain a transport/client boundary.

It can:

- send requests
- serialize image parts
- parse model outputs

It should not:

- decide which tools to run
- call robot capabilities
- manage camera behavior

### 3. Do not let camera logic land directly in `RealtimeInteractionOrchestrator`

That would solve the immediate problem while making the hardest class even harder to evolve.

### 4. Do not introduce polling/caching before the app-level vision seam exists

Caching strategy belongs behind a robot-layer vision abstraction, not inside an orchestrator or
directly inside tool execution logic.

---

## Suggested End-State Responsibilities

### Hosted services

- `InteractionOrchestrator`: host adapter for legacy mode
- `RealtimeInteractionOrchestrator`: host adapter for realtime mode

### Runtime coordination

- `RobotRuntimeCoordinator`: startup/shutdown and readiness sequencing

### Turn execution

- `LegacyTurnExecutor`: one legacy turn
- `RealtimeTurnExecutor`: one realtime turn

### Shared application services

- `PersonalitySessionService`
- `RobotCueService`
- `ToolCallHandler`
- `CameraSnapshotService`
- `ResponsePresenter`

---

## Suggested Folder Direction

One reasonable shape inside `dotNet/ReachTether.Robot/`:

```text
Runtime/
  RobotRuntimeCoordinator.cs

Interaction/
  ConversationLoopService.cs
  TurnContext.cs
  TurnOutcome.cs
  ITurnExecutor.cs
  LegacyTurnExecutor.cs
  RealtimeTurnExecutor.cs

Personality/
  IPersonalitySessionService.cs
  PersonalitySessionService.cs

Motion/
  IRobotCueService.cs
  RobotCueService.cs

Tools/
  IToolCallHandler.cs
  ToolCallHandler.cs
  CameraToolHandler.cs

Vision/
  ICameraSnapshotProvider.cs
  CameraSnapshotService.cs
  VisionStartupProbe.cs

Responses/
  IResponsePresenter.cs
  LegacyResponsePresenter.cs
  RealtimeResponsePresenter.cs
```

This does not need to be implemented all at once. It is a target direction, not a demand for a
single large refactor.

---

## Recommended Near-Term Priority

If this refactor is pursued incrementally, the best first steps are:

1. extract `RobotRuntimeCoordinator`
2. extract `RobotCueService`
3. isolate `LegacyTurnExecutor` and `RealtimeTurnExecutor`
4. only then add shared tool execution and vision integration

That sequence reduces immediate complexity while also creating the correct seam for the upcoming
camera tool work.

---

## Bottom Line

The current architecture is sufficient to prove the camera path and continue experimentation.

It is not yet in the right shape for sustained feature growth around vision and tools.

The correct response is not a total rewrite. The correct response is to narrow the responsibilities
of the orchestrators and move turn execution, runtime coordination, tool handling, and vision access
behind explicit service boundaries.
