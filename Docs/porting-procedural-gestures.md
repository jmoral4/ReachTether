# Porting Procedural Gestures & Dances to .NET

## Executive Summary
The "gestures" in the Python `reachy_mini_conversation_app` are not simple recorded playback files. They are implemented using a **procedural rhythmic motion library** (`reachy_mini_dances_library`) that calculates joint offsets in real-time based on oscillators, transient functions, and audio envelopes.

Bringing this to `ReachTether.Robot` (.NET) is highly feasible but requires moving from discrete `GotoAsync` commands to a continuous **High-Frequency Motion Loop (50Hz-100Hz)**.

---

## 1. Research Findings

### A. Talking Gestures (Audio-Reactive)
- **Source:** `reachy_mini_conversation_app/src/reachy_mini_conversation_app/audio/speech_tapper.py`
- **Mechanism:** A class called `SwayRollRT` uses a multi-oscillator system (Pitch, Roll, Yaw) whose amplitudes are modulated by the **RMS (volume)** of the outgoing audio stream.
- **Porting Effort:** Low-Moderate. The math (Sine/Cosine oscillators + envelope tracking) is standard.

### B. Dance Moves (Procedural/Symbolic)
- **Source:** `reachy_mini_dances_library` (external package).
- **Mechanism:** Functions like `move_simple_nod` or `move_jackson_square` calculate `MoveOffsets` (X, Y, Z, Roll, Pitch, Yaw) as a function of `t_beats` (time in beats).
- **Key Primitives:**
  - `atomic_pitch`, `atomic_roll`, etc. (Oscillators)
  - `transient_motion` (One-off kicks/recoils)
  - `combine_offsets` (Summation of multiple layers)
- **Porting Effort:** Moderate. Requires recreating the "Rhythmic Motion" primitives in C#.

### C. Recorded Gestures (JSON-based)
- **Source:** `reachy_mini/src/reachy_mini/motion/recorded_move.py` and HuggingFace datasets (`pollen-robotics/reachy-mini-dances-library`).
- **Mechanism:** Time-stamped joint trajectories stored in JSON format.
- **Porting Effort:** Low. Requires a JSON parser for the `RecordedMove` format and a simple linear/cubic interpolator in .NET.

---

## 2. Proposed .NET Architecture

To support these gestures natively, we need to introduce a new layer in the `ReachTether.Robot` app:

### The `MotionOrchestrator` (Background Service)
- **Frequency:** 50Hz (20ms interval).
- **Responsibility:**
  1. Main loop that polls all active "Motion Layers".
  2. Aggregates offsets from:
     - **Base Pose Layer:** (e.g., Neutral, Listening, Thinking).
     - **Gesture Layer:** (The procedural oscillators).
     - **Safety Layer:** (Limits and damping).
  3. Sends the final target to the robot using `reachyClient.Move.SetTargetAsync`.

### The `GestureEngine` (C# Port)
- **`IGestureSource` Interface:**
  - `MoveOffsets GetOffsets(double elapsedSeconds, double bpm)`
- **`TalkingGestureSource`:** Implementation of the audio-reactive wobble.
- **`ProceduralDanceSource`:** Implementation of the `AVAILABLE_MOVES` from Python.

---

## 3. Implementation Plan

### Phase 1: The Loop (Foundation)
- Implement `MotionOrchestrator` as an `IHostedService` in `ReachTether.Robot`.
- Verify 50Hz stability with the Reachy Mini hardware/daemon.

### Phase 2: Talking Gestures (Immediate Impact)
- Port `SwayRollRT` to C#.
- Inject `TalkingGestureSource` into the `MotionOrchestrator`.
- Feed audio RMS from `RealtimeInteractionOrchestrator` to the source.

### Phase 3: Procedural Library (Full Parity)
- Recreate `OscillationParams` and `TransientParams` in C#.
- Port high-impact moves (`simple_nod`, `head_tilt_roll`, `side_to_side_sway`).
- Allow the LLM to trigger these via tool calls (e.g., "nod enthusiastically").

---

## 4. Why .NET Natively?
- **Lower Latency:** Directly processing audio chunks and calculating offsets in the same process as the robot client reduces jitter.
- **Better Integration:** We can blend LLM-driven "Personality" poses with automatic "Life-like" movements seamlessly.
- **Simplified Deployment:** No need to manage a sidecar Python process just for motion generation.
