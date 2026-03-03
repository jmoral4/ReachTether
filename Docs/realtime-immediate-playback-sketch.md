# Realtime Immediate Playback Sketch

## Goal
Start speaking model audio as soon as the first `response.audio.delta` chunk arrives, instead of waiting for the full response to complete.

## Current Behavior (as implemented now)
- `RealtimeInteractionOrchestrator.RunRealtimeTurnAsync()` buffers all output audio chunks into memory.
- Playback starts only after `ConversationResponseFinishedUpdate`.
- This is robust but adds full-response latency.

## Target Behavior
- On first audio delta, start playback immediately.
- Continue feeding audio chunks while response is streaming.
- Support interruption (`barge-in`) by stopping playback instantly and canceling the active response.

## Proposed Pipeline
1. `CaptureLoop` (existing VAD utterance capture).
2. `RealtimeSendLoop`:
   - Send captured PCM16 mono to realtime.
   - Commit and start response.
3. `RealtimeReceiveLoop`:
   - Consume `ConversationUpdate` stream.
   - Push `ConversationItemStreamingPartDeltaUpdate.AudioBytes` to a bounded `Channel<byte[]>`.
   - Accumulate transcript text in parallel.
4. `PlaybackLoop`:
   - Read chunks from the channel and write directly to ALSA as a continuous stream.
   - Do not drain per chunk; drain only at end of response.

## Needed Code Changes

### 1) Add streaming playback contract
- Add a new interface for chunked PCM playback (separate from current WAV turn playback):
  - `StartStreamAsync(format)`
  - `WriteChunkAsync(byte[] chunk)`
  - `CompleteAsync()`
  - `CancelAsync()`

This can be implemented either:
- In `AudioPlaybackService` with a new `PlaybackItemKind.Stream`, or
- As a dedicated `RealtimeAudioPlaybackService`.

### 2) Extend `LocalAudioSession`
- Add direct PCM stream write methods so we can avoid per-turn WAV encode/decode:
  - `BeginPlaybackStream(...)`
  - `WritePcm16Chunk(...)`
  - `EndPlaybackStream(...)`
- Keep existing `PlayWaveAsync()` unchanged for legacy pipeline.

### 3) Realtime orchestrator change
- Replace in-memory `MemoryStream` buffering with producer/consumer chunk flow:
  - Producer: update receiver pushes `AudioBytes`.
  - Consumer: playback loop writes immediately.
- Keep transcript aggregation as-is for console logs and shutdown intent checks.

## Interruption / Barge-in Plan
- Trigger on `ConversationUpdateKind.InputSpeechStarted` while assistant audio is active:
  1. Stop playback stream immediately.
  2. Call `CancelResponseAsync()` (or `InterruptResponseAsync()` if we prefer semantic “barge-in”).
  3. Drop queued audio chunks.
- Optional follow-up: call `TruncateItemAsync(...)` if we need strict conversation-state alignment with unheard audio.

## Buffering and Backpressure
- Use bounded channel capacity (example: ~1s of chunks).
- Full mode:
  - Prefer `DropOldest` for conversational responsiveness, or
  - `Wait` for highest fidelity at risk of latency growth.
- Start with `DropOldest` for robot conversation UX.

## Audio Format Notes
- Realtime output is PCM16 audio chunks.
- Our ALSA session is currently configured as `16000 Hz`, `2 channels`.
- We should avoid per-chunk WAV wrapping.
- If realtime output sample rate differs (often 24k mono), add stream-safe resampling/channel conversion in playback writer.

## Suggested Rollout
1. Add streaming playback API + ALSA chunk writer.
2. Switch realtime orchestrator to chunked playback with channel buffering.
3. Add interruption handling on `InputSpeechStarted`.
4. Add metrics:
   - chunk queue depth
   - dropped chunks
   - first-audio latency (response start -> first playback write)
   - interruption latency

## Definition of Done
- Assistant begins audible playback before response finishes.
- `InputSpeechStarted` interrupts playback within one chunk interval.
- Legacy non-realtime path remains unchanged.
