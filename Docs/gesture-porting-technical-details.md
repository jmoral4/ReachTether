# Gesture Porting – Technical Details

This is a companion to `reachy-gesture-porting-plan-codex.md`, which covers strategy and phasing.
This document covers the concrete technical findings from reading both codebases and maps them to
actionable implementation specifics for the .NET app.

---

## Python System – What It Actually Does (Concrete Values)

### Control Loop
- **Frequency:** 100 Hz (10ms tick), maintained via `time.monotonic()` + adaptive sleep
- **Single writer rule:** all pose writes go through one `set_target()` call at the bottom of the loop
- **Threads:** audio thread, camera thread, and main loop — communicate via locks + dirty flags

### Primary Move Queue
Moves implement a simple interface:
```python
class Move:
    @property
    def duration(self) -> float: ...          # total seconds
    def evaluate(self, t: float) -> Tuple:    # pose at t seconds
        # returns (head_4x4_matrix | None, antennas_tuple | None, body_yaw | None)
```

Concrete move types:
- **GotoQueueMove** – linear interpolation between two `XYZRPYPose`s with minjerk-style clamping
- **DanceQueueMove** – wraps `reachy_mini_dances_library.DanceMove(name)`
- **EmotionQueueMove** – wraps `RecordedMoves.get(name)` from HuggingFace dataset
- **BreathingMove** – sinusoidal idle animation (details below)

### BreathingMove (Idle Animation)
Two-phase: smooth interpolation to neutral (1s), then continuous sine waves.

```
Head Z (up/down):   amplitude = 5 mm,   frequency = 0.1 Hz  (10s cycle)
Antenna sway:       amplitude = 15°,    frequency = 0.5 Hz  (2s cycle)
                    left and right are π out of phase (opposite directions)
```

### Speech-Reactive Sway (HeadWobbler + SwayRollRT)
Audio pipeline: 24 kHz TTS audio → resampled to 16 kHz → 50ms hop FFT → RMS dB → 6-axis offsets

**Voice Activity Detection thresholds:**
| Parameter | Value |
|-----------|-------|
| ON threshold | -35 dBFS |
| OFF threshold | -45 dBFS |
| Attack time | 40 ms |
| Release time | 250 ms |

**Loudness mapping to sway amplitude:**
- `SWAY_DB_LOW = -46.0` dBFS → sway = 0
- `SWAY_DB_HIGH = -18.0` dBFS → sway = max
- Gamma curve: `loudness^0.9` for perceptual linearity
- Sensitivity offset: +4 dB (tunable)

**Six-axis sinusoidal sway parameters (applied additively as secondary offsets):**
| Axis | Frequency | Amplitude | Notes |
|------|-----------|-----------|-------|
| Pitch | 2.2 Hz | ±4.5° | Nod-like |
| Yaw | 0.6 Hz | ±7.5° | Slow side look |
| Roll | 1.3 Hz | ±2.25° | Tilt |
| X (fwd/back) | 0.35 Hz | ±4.5 mm | Lean |
| Y (left/right) | 0.45 Hz | ±3.75 mm | Sway |
| Z (up/down) | 0.25 Hz | ±2.25 mm | Bob |

Master amplitude scale: `1.5` (multiplies all amplitudes).
Motion latency added: 200 ms (for natural speech sync feel).

Phases per axis are arbitrary (hardcoded constants); what matters is they are all different so motion
doesn't look synchronized.

### Listening State – Antenna Freeze/Blend
- Antennas are frozen to their current commanded position when listening begins
- On listening end: blend from frozen → current target over **0.4 seconds**

### Named Gestures Available

**Dance moves** (from `reachy_mini_dances_library`):
```
simple_nod, head_tilt_roll, side_to_side_sway, dizzy_spin,
stumble_and_recover, interwoven_spirals, sharp_side_tilt,
side_peekaboo, yeah_nod, uh_huh_tilt, neck_recoil, chin_lead,
groovy_sway_and_roll, chicken_peck, side_glance_flick,
polyrhythm_combo, grid_snap, pendulum_swing, jackson_square
```

**Emotion moves** (from HuggingFace `pollen-robotics/reachy-mini-emotions-library`):
Names are dataset-specific; the Python app queries them at runtime via `ListRecordedMovesAsync`.
The same dataset should be accessible in .NET via `reachyClient.Move.ListRecordedMovesAsync()`.

**Head movement primitives** (degrees in Python, direct yaw/pitch changes):
```
left:  yaw = +40°
right: yaw = -40°
up:    pitch = +30°
down:  pitch = -30°
front: all = 0° (neutral)
```

---

## .NET SDK – What We Already Have

The SDK surface is sufficient. No new API work needed.

### Available Movement Primitives

```csharp
// Discrete interpolated move (use for state transitions)
await reachyClient.Move.GotoAsync(new GotoModelRequest {
    HeadPose = new XYZRPYPose { Pitch = Rad(30) },
    Antennas = [Rad(10), Rad(10)],
    Duration = 0.9,
    Interpolation = InterpolationMode.Minjerk
});

// Streaming full-body target (use for motion loop)
await reachyClient.Move.SetTargetAsync(new FullBodyTarget {
    TargetHeadPose = new XYZRPYPose { ... },
    TargetAntennas = [left, right],
    TargetBodyYaw = 0.0,
    Timestamp = DateTime.UtcNow
});

// Pre-recorded animations
await reachyClient.Move.PlayRecordedMoveAsync("reachy-mini-emotions-library", "happy");
var moves = await reachyClient.Move.ListRecordedMovesAsync();  // discover names

// Query current state for smooth blending start points
var currentPose = await reachyClient.State.GetHeadPoseAsync();
var antennas = await reachyClient.State.GetAntennaJointPositionsAsync();
```

### InterpolationMode
`Minjerk` is the best match for natural gesture motion (matches Python's smooth interpolation).
`Cartoon` is available for exaggerated expressions.

---

## Implementation Sketch – Native .NET Motion Orchestrator

### Architecture Overview

```
┌───────────────────────────────────────────────────────────────┐
│                    Program.cs (conversation loop)             │
│  SetBehaviorState(Listening|Thinking|Speaking|Idle)           │
│  QueueGesture(name)  ──────────────────────────────────────►  │
└────────────────────────┬──────────────────────────────────────┘
                         │
                         ▼
┌───────────────────────────────────────────────────────────────┐
│                  MotionOrchestrator                           │
│                                                               │
│  BehaviorState ─► Primary pose target (from state)           │
│  PrimaryMoveQueue ─► Overrides primary when active           │
│  SpeechSway.Update(pcmChunk) ─► Secondary offset             │
│                                                               │
│  FinalPose = Compose(primary, secondary)                      │
│  ─► SetTargetAsync() at 20-50 Hz                             │
└───────────────────────────────────────────────────────────────┘
```

### Recommended Loop Rate for .NET

100 Hz (as in Python) is likely too aggressive over HTTP REST. Start at **20 Hz** (50ms), which is
enough for smooth perceived motion and well within what HTTP keep-alive can sustain on localhost.
Only send if pose delta exceeds a threshold (e.g., >0.5° or >0.5mm) to avoid unnecessary calls.

If jitter is visible, reduce to 10 Hz for smooth discrete animations, but speech sway will feel
choppier. Profile on the actual hardware.

### Motion State Machine (Phase 1)

```csharp
public enum BehaviorState { Idle, Listening, Thinking, Speaking }

// Target antenna angles per state
private static readonly Dictionary<BehaviorState, double[]> AntennaTargets = new()
{
    [BehaviorState.Idle]      = [Deg(0),   Deg(0)],
    [BehaviorState.Listening] = [Deg(10),  Deg(10)],
    [BehaviorState.Thinking]  = [Deg(12),  Deg(-12)],
    [BehaviorState.Speaking]  = [Deg(16),  Deg(16)],
};
```

### BreathingMove in .NET

```csharp
// Run as part of motion loop when state is Idle
double breathingZ = 0.005 * Math.Sin(2 * Math.PI * 0.1 * elapsedSeconds);  // 5mm, 0.1 Hz
double antennaLeft  = Deg(15) * Math.Sin(2 * Math.PI * 0.5 * elapsedSeconds);
double antennaRight = Deg(15) * Math.Sin(2 * Math.PI * 0.5 * elapsedSeconds + Math.PI); // opposite
```

### Speech Sway in .NET (Phase 2)

The key insight: TTS audio is available as WAV bytes before playback begins. We can analyze it
per-chunk during playback.

**Approach:** Analyze PCM amplitude per 50ms hop to get a loudness envelope, then map to sinusoidal
offsets modulated by that envelope. Since we have the full WAV ahead of time, we can pre-compute the
entire motion track and play it in sync.

```csharp
// Per 50ms audio chunk during playback:
double rmsDb = ComputeRmsDb(pcmChunk);  // 16kHz PCM16
double loudness = MapLoudnessToSway(rmsDb, dbLow: -46.0, dbHigh: -18.0, gamma: 0.9);

// Evaluate sinusoidal offsets modulated by loudness:
double pitchOffset = Deg(4.5) * 1.5 * loudness * Math.Sin(2 * Math.PI * 2.2 * t);
double yawOffset   = Deg(7.5) * 1.5 * loudness * Math.Sin(2 * Math.PI * 0.6 * t + phi1);
double rollOffset  = Deg(2.25) * 1.5 * loudness * Math.Sin(2 * Math.PI * 1.3 * t + phi2);
// ... and similarly for X/Y/Z translations
```

Pre-compute approach (recommended for v1): decode WAV → compute envelope per 50ms → build
`List<XYZRPYOffset>` → replay at 20 Hz during playback. This avoids real-time complexity.

### Pose Composition

```csharp
XYZRPYPose Compose(XYZRPYPose primary, XYZRPYPose speechOffset)
{
    return new XYZRPYPose
    {
        X     = primary.X + speechOffset.X,
        Y     = primary.Y + speechOffset.Y,
        Z     = primary.Z + speechOffset.Z,
        Roll  = Clamp(primary.Roll  + speechOffset.Roll,  Deg(-30), Deg(30)),
        Pitch = Clamp(primary.Pitch + speechOffset.Pitch, Deg(-30), Deg(30)),
        Yaw   = Clamp(primary.Yaw   + speechOffset.Yaw,  Deg(-60), Deg(60)),
    };
}
```

Always clamp composed offsets. Safety is paramount — large offsets from bugs or edge cases should
never send the robot to dangerous positions.

---

## What's Reusable From Python (Without Translation)

### Emotion/Dance Moves via API
The HuggingFace emotions dataset and dance moves are served by the robot's daemon process.
They are accessible from .NET today:
```csharp
await reachyClient.Move.PlayRecordedMoveAsync("reachy-mini-emotions-library", emotionName);
// Discover names:
var available = await reachyClient.Move.ListRecordedMovesAsync();
```

This means the entire `play_emotion` tool equivalent is free in .NET — no porting needed.
For dances, the `reachy_mini_dances_library` generates motion in Python and sends it to the daemon;
the recorded-moves API may not expose them. Confirm by calling `ListRecordedMovesAsync` and
checking if dance names appear. If not, dances would require re-implementing the motion math.

### Head Movement Tool Equivalent
Simple head pose `GotoAsync` calls — already possible and trivial to add:

```csharp
// move_head equivalents
var lookLeft  = new XYZRPYPose { Yaw = Deg(40) };
var lookRight = new XYZRPYPose { Yaw = Deg(-40) };
var lookUp    = new XYZRPYPose { Pitch = Deg(30) };
var lookDown  = new XYZRPYPose { Pitch = Deg(-30) };
var neutral   = new XYZRPYPose();  // all zeros
```

---

## What Would NOT Be Ported (Python-Specific)

- **`reachy_mini_dances_library`** – Python-only; depends on numpy + scipy math. Would need to
  re-implement the motion curves in C# if the daemon doesn't serve them. Low priority.
- **Camera/face tracking** – Python uses a camera feed + CV pipeline. Not in scope for this app.
- **Profile system** (`tools.txt`, per-persona tool loading) – Python-specific plugin architecture.
  In .NET we'd express this as configuration + strategy pattern, which is simpler.

---

## Recommended v1 Scope for ReachTether.Robot

Ship this to get the biggest perceptual improvement:

1. **`MotionOrchestrator` class** – single writer, background `Task`, 20 Hz loop
2. **State-driven antenna + head poses** – `SetBehaviorState()` replaces the current ad-hoc `GotoAsync` calls
3. **Idle breathing** – sinusoidal Z + antenna sway when state = Idle for >2s
4. **Pre-computed speech sway** – analyze WAV bytes after TTS, replay motion during playback

This delivers: no frozen-robot feeling, natural idle animation, and head-bobbing during speech — which
is the biggest visible quality gap vs. the Python app today.

v2 adds: real-time streaming speech sway, emotion/dance tool calls from LLM, antenna freeze/blend.
