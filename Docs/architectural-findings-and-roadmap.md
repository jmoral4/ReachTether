# ReachTether: Architectural Assessment & Evolution Roadmap

## 1. Current State Assessment

The ReachTether prototype successfully demonstrates end-to-end multimodal interaction (Speech -> Chat -> Speech + Motion), but its current "script-like" architecture faces several scalability and performance bottlenecks when moving toward production-grade reliability on Reachy Mini.

### Key Observations:
*   **Synchronous Orchestration:** The main application loop in `ReachTether.Robot` is linear (`Capture -> Transcribe -> Chat -> TTS -> Play`). This "stop-the-world" model prevents continuous perception and makes "barge-in" (user interruption) impossible to handle cleanly.
*   **Transient Hardware Lifecycle:** `LocalAudioSession` opens and closes ALSA device handles for every recording/playback turn. On embedded ARM systems like the CM4, this adds significant latency (50-150ms) and increases the risk of "Device or resource busy" errors due to race conditions.
*   **In-Memory Buffering:** Audio frames are currently handled as `byte[]` copies and stored in `List<AudioFrame>` before processing. This creates significant GC pressure on the quad-core CPU and lacks the backpressure mechanisms needed for stable long-running sessions.
*   **REST-First SDK:** The `ReachyMini.Sdk` is well-structured but primarily focuses on REST. For high-performance control and state synchronization, it should transition toward the Daemon's WebSocket endpoints.

---

## 2. Proposed Architectural Evolution

To align with the "Thin Hardware + Networked Application" model of Reachy Mini, ReachTether should evolve into a **streaming, event-driven system**.

### A. Shift to .NET Generic Host & Supervised Workers
Decompose the monolithic application into independent `BackgroundService` components managed by a `Host`. This provides standardized startup/shutdown, dependency injection, and worker supervision.

*   **`AudioCaptureService`:** A long-lived loop that keeps the ALSA capture device open and pushes raw PCM frames into a high-performance channel.
*   **`RealtimeSessionService`:** Manages the persistent WebSocket connection to the OpenAI Realtime API, framing events and managing session state.
*   **`InteractionOrchestrator`:** The "brain" that coordinates between perception (wake-word/VAD), AI reasoning, and execution (motion/speech).
*   **`AudioPlaybackService`:** Consumes an outgoing audio channel and handles playback, with a "flush" capability to support immediate barge-in.

### B. Bounded Streaming Pipelines with `System.Threading.Channels`
Replace manual lists and `Task.Yield()` with **`System.Threading.Channels`** to enforce backpressure:
*   **`BoundedChannelOptions` (FullMode = DropOldest):** Ensures the robot always processes the most recent audio, preventing "latency drift" where the robot responds to something said 5 seconds ago because of a full buffer.
*   **Memory Efficiency:** Transition toward `ArrayPool<byte>` and `ReadOnlyMemory<byte>` for audio frames to reduce allocations in the hot path.

### C. First-Class Barge-In (Interruption)
Interruption is a core requirement for natural conversation. The architecture must support:
*   **Cancellation-Aware Playback:** The `AudioPlaybackService` must monitor a `CancellationToken`. When a "User Started Speaking" event is received from the Realtime API, the service must immediately stop writing to the ALSA device and drain its local buffers.
*   **Session Truncation:** Ensure the `RealtimeSessionService` sends the `conversation.item.truncate` event to OpenAI so the AI "knows" exactly where it was cut off.

---

## 3. Reachy Mini Platform Alignment

*   **Logical ALSA Devices:** Explicitly target `reachymini_audio_src` and `reachymini_audio_sink`. These are configured via the robot's `.asoundrc` for multi-client access, preventing device locking issues.
*   **Snapshot Vision:** Instead of continuous WebRTC video (high complexity/CPU), implement a "Vision Tool" that captures JPEG snapshots from the Daemon API only when the model requests visual context.
*   **Daemon WebSocket Integration:** Transition from polling `GET /api/state/full` to subscribing to `ws://localhost:8000/api/state/ws/full` for low-latency robot state tracking (joint positions, battery, etc.).

---

## 4. Storage & RAG Strategy

Given the 16GB flash (and ~2GB free) constraint:
*   **Thin Edge Cache:** Store only high-frequency "identity" facts locally (robot name, owner name, home WiFi).
*   **Remote Knowledge Index:** Offload the vector database and document chunking to an external service. Use a "Knowledge Tool" to retrieve relevant snippets over the network.
*   **Bounded Logs:** Ensure the .NET logging provider is configured with a size-limited rolling file policy to prevent filling the internal eMMC.

---

## 5. Prioritized Roadmap

1.  **Phase 1: Stabilization (Short-term)**
    *   Transition `LocalAudioSession` to a persistent lifecycle (keep devices open).
    *   Implement `System.Threading.Channels` for the audio capture path.
    *   Add basic "flush" logic to the playback loop for primitive barge-in support.

2.  **Phase 2: Refactoring (Medium-term)**
    *   Migrate to `.NET Generic Host`.
    *   Isolate the `Realtime API` (WebSocket) transport from the orchestration logic.
    *   Implement a proper state machine for `Idle -> Listening -> Thinking -> Speaking -> Interrupted`.

3.  **Phase 3: Optimization (Long-term)**
    *   Introduce `ArrayPool` and `Span<T>`-based audio processing.
    *   Add vision snapshot tools and remote RAG integration.
    *   Implement end-to-end telemetry using `OpenTelemetry` (`ActivitySource` and `Meter`).
