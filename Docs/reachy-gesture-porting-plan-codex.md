# Porting Reachy Conversation Gestures to `ReachTether.Robot`

## Short Answer
Yes, we can recreate this natively in .NET.

Difficulty is **moderate** for a useful v1 and **moderate-high** for full parity with the Python app's motion quality. The current .NET SDK surface already has what we need (`SetTargetAsync`, `GotoAsync`, head pose + antennas + body yaw).

## What the Python app is doing today

From `reachy_mini_conversation_app`:
- `MovementManager` runs a dedicated control loop at ~100Hz and is the single writer to robot pose (`set_target`).
- Motion is layered:
  - `primary` motions (goto/dance/emotion/breathing) run sequentially via a queue.
  - `secondary` offsets (speech wobble, face tracking) are additive on top of primary.
- `HeadWobbler` converts assistant audio deltas into time-aligned pose offsets while speaking.
- Listening state freezes antennas and blends back smoothly after listening.
- Idle mode starts a breathing move automatically.
- Tool calls (`move_head`, `dance`, `head_tracking`, `play_emotion`) enqueue or toggle motion behavior, instead of directly fighting for robot control.

## Where `ReachTether.Robot` is now

Current app behavior is state-pose based, not a motion engine:
- Uses one-off `GotoAsync` calls for listening/thinking/speaking/farewell antenna poses.
- No queued move scheduler.
- No additive speech-synced head offsets.
- No automatic breathing/idle animation.
- No arbitration layer to prevent multiple movement sources from conflicting.

So today it is expressive but "pose switching", while Python is "continuous motion composition".

## Can we do this natively in .NET?

Yes. `ReachyMini.Sdk` already supports the required primitives:
- `MoveClient.SetTargetAsync(FullBodyTarget)` for streaming high-rate full-body targets.
- `MoveClient.GotoAsync(GotoModelRequest)` for discrete transitions.
- State APIs for current pose/joints.

What we would need to build in .NET is the runtime policy layer (scheduler + composer), not missing robot APIs.

## Porting Options

### Option A: Native .NET motion runtime (recommended)
Build a `RobotMotionOrchestrator` in `ReachTether.Robot` (or shared lib) that mirrors Python architecture.

Proposed components:
- `MotionLoopService` (50-100Hz task loop)
- `PrimaryMoveQueue` (goto/emotion/dance/breathing)
- `SecondaryOffsetChannels` (speech + vision + future)
- `PoseComposer` (primary + additive offsets)
- `BehaviorState` (listening, speaking, idle, thinking)

Why this is best:
- Single language/runtime for your dotnet app.
- Deterministic behavior and easier integration with existing OpenAI/ALSA flow.
- No Python dependency on the robot for core gesture behavior.

Estimated effort:
- v1 (listening/thinking/speaking idle breathing + smooth transitions): **3-5 days**
- v2 (speech-synced wobble from audio envelope + tool-like actions): **5-9 days**
- v3 (near Python parity incl. richer choreography and tracking): **2-4 weeks**

### Option B: Hybrid bridge to Python motion engine
Keep Python as motion server/process and send high-level commands from .NET.

Pros:
- Faster path to parity if we want current behavior quickly.

Cons:
- Two runtimes, deployment complexity, monitoring complexity, failure modes across process boundary.
- Harder long-term ownership for a dotnet-first app.

Estimated effort:
- Integration and stabilization: **2-6 days** (depending on IPC protocol + deployment constraints)

### Option C: Minimal native gestures only
Stay with discrete `GotoAsync` poses and add randomized variation/timing.

Pros:
- Very fast (1-2 days).

Cons:
- Won't feel like Python conversation app quality.
- No proper speech-reactive motion layering.

## Recommended Implementation Plan (Native .NET)

### Phase 1: Motion orchestration foundation
- Introduce one background loop as sole movement writer.
- Route all current pose changes through orchestrator API (`SetListening`, `SetThinking`, `SetSpeaking`, `SetIdle`).
- Add smooth blending and queue for discrete moves.

Acceptance criteria:
- No conflicting movement commands.
- State transitions are visibly smooth.

### Phase 2: Speech-reactive gesture channel
- While playing TTS audio, compute a short-hop amplitude envelope from PCM and map to subtle pitch/roll/yaw offsets.
- Feed these offsets as secondary layer during speaking.
- Reset offsets cleanly at end-of-utterance and interruption.

Acceptance criteria:
- Head/antenna micro-motion follows speech cadence.
- No large jitter or drift.

### Phase 3: Idle + personality behaviors
- Add breathing move after inactivity delay.
- Add persona-specific motion presets (calm/energetic/bored/etc).
- Optional: random micro-gestures tied to punctuation/intent.

Acceptance criteria:
- Robot never looks frozen during long idle periods.
- Behavior style changes are obvious but safe.

### Phase 4 (optional): Tool-style actions
- Add command handlers for `move_head`, `dance`, `play_emotion`, `head_tracking` analogs.
- Queue/priority rules so explicit actions preempt idle behaviors safely.

## Technical Risks and Mitigations

- Control-loop overload / API spam:
  - Start at 50Hz and clamp command rate; only send when pose delta exceeds threshold.
- Motion conflicts:
  - Keep single-writer orchestrator rule strict.
- Jitter from async audio + motion timing:
  - Use monotonic clock and small bounded queues.
- Safety/comfort:
  - Clamp offsets, enforce max angular velocity, quick neutral fallback.

## Suggested v1 Scope for `ReachTether.Robot`

Deliver this first:
- Single-writer motion orchestrator.
- Smooth listening/thinking/speaking transitions.
- Idle breathing.
- Simple speech envelope -> head nod/roll offsets during TTS playback.

This gets most of the perceived expressiveness jump without needing full Python feature parity.

## Bottom line

- **Feasible natively in .NET:** yes.
- **How hard:** moderate for high-impact v1; moderate-high for full parity.
- **Best path:** native orchestrator in phases, starting with behavior states + speech-reactive offsets.
