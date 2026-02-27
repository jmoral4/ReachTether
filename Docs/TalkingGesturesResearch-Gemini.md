# Talking Gestures Research & Porting Analysis

## Overview
The "talking gestures" in the Python `reachy_mini_conversation_app` (also known as the "head wobble") are implemented through a real-time signal processing layer that converts audio energy into head movement offsets. This document analyzes the implementation and how it can be ported to the .NET `ReachTether.Robot` application.

## 1. Python Implementation Analysis (`SwayRollRT`)
The core logic resides in `src/reachy_mini_conversation_app/audio/speech_tapper.py`.

### Signal Processing Chain
1.  **Input:** PCM audio chunks (base64 from OpenAI Realtime or local audio).
2.  **Loudness/Envelope:**
    -   Calculates **RMS (dBFS)** for each frame.
    -   Uses **VAD (Voice Activity Detection)** with hysteresis (-35dB on, -45dB off) to detect speech activity.
    -   A **Sway Envelope** (0.0 to 1.0) follows the speech activity with specific attack (40ms) and release (250ms) times.
    -   **Loudness Gain** is derived from the dB level with a gamma correction.
3.  **Oscillators:**
    -   Six independent oscillators (sine waves) for each axis:
        -   **Pitch:** 2.2 Hz, 4.5° max amplitude
        -   **Yaw:** 0.6 Hz, 7.5° max amplitude
        -   **Roll:** 1.3 Hz, 2.25° max amplitude
        -   **X (translation):** 0.35 Hz, 4.5mm max amplitude
        -   **Y (translation):** 0.45 Hz, 3.75mm max amplitude
        -   **Z (translation):** 0.25 Hz, 2.25mm max amplitude
    -   The amplitude of each oscillator is multiplied by `loudness * envelope * sway_master`.
4.  **Composition:** These offsets are added to the "primary" pose (the base position of the head) in a 100Hz control loop (`MovementManager`).

## 2. Portability to .NET (`ReachTether.Robot`)

### Feasibility: **High**
The .NET SDK (`ReachyMini.Sdk`) already supports the `SetTargetAsync` method, which accepts a `FullBodyTarget`. This target can be expressed as `XYZRPYPose`, mapping directly to the outputs of the gesture logic.

### Challenges:
-   **No Background Motion Loop:** `ReachTether.Robot` currently uses discrete `GotoAsync` calls. A continuous "wobble" requires a background task running at ~50-100Hz to stream target poses.
-   **Audio Hook:** The current .NET app uses `LocalAudioSession.PlayWaveAsync`. We need to intercept the PCM buffer during playback to drive the gesture engine.

## 3. Proposed .NET Architecture

### New Components
1.  **`GestureEngine` (Port of `SwayRollRT`):**
    -   C# class that maintains phase state for oscillators.
    -   Method `Feed(short[] pcm)` to update loudness and envelope.
    -   Method `GetNextOffsets(double dt)` to calculate the current (x, y, z, r, p, y).
2.  **`MotionOrchestrator`:**
    -   A background `Task` (using `PeriodicTimer` or a high-frequency loop).
    -   Responsibility: Periodically call `ReachyClient.Move.SetTargetAsync` with `(BasePose + GestureOffsets)`.
3.  **`AudioPlaybackInterceptor`:**
    -   Enhancement to `LocalAudioSession.PlayWaveAsync` to provide a callback/stream of the current playing PCM buffer.

### Integration Plan
-   **Phase 1 (Signal Processing):** Port the `SwayRollRT` math to C#. Use `System.Math` and a simple `Phase` state.
-   **Phase 2 (Control Loop):** Implement a `BackgroundService` or similar in `ReachTether.Robot` that handles the 50Hz update loop.
-   **Phase 3 (Audio Link):** Update `PlayWaveAsync` to call `GestureEngine.Feed()` for each chunk of audio written to the ALSA device.

## 4. Complexity & Effort
-   **Porting Math Logic:** Low complexity (1-2 days).
-   **Real-time Loop Integration:** Moderate complexity (2-3 days). Requires careful handling of async/await to avoid blocking the audio playback or jittering the motion.
-   **Full Parity:** High complexity (2+ weeks) if including "dances", "emotions", and "smooth blending" as found in the Python app.

## Recommendation
Start with a **Phase 1 + Phase 2** implementation for `ReachTether.Robot`.
1. Add a `SetTargetAsync` based loop that runs while `PlayWaveAsync` is active.
2. Use a simplified version of the oscillators tied to the audio RMS.
3. This will provide immediate "life-like" quality during speech with minimal architectural changes.
