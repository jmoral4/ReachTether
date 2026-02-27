# ReachTether Evolution Findings (Doc-Aligned Review)

## Scope
- Source docs reviewed:
  - `Docs/building-complex-apps-on-reachy-mini-with-dotnet.md`
  - `Docs/ReachTether-on-reachy-mini.md`
- Code reviewed:
  - `dotNet/ReachTether.Robot`
  - `dotNet/ReachTether.Audio*`
  - `dotNet/ReachTether.WebRtc`
  - `dotNet/ReachyMini.Sdk`

## Findings (ordered by impact)

1. `ReachTether.Robot` is still a turn-based recorder loop, not a continuous multimodal session engine.
- Current app blocks on `Console.ReadLine()`, captures fixed-duration audio, then runs STT -> chat -> TTS sequentially.
- This diverges from the docs' target model: continuous capture, wake/VAD gating, and explicit barge-in transitions.
- References:
  - `dotNet/ReachTether.Robot/Program.cs:105`
  - `dotNet/ReachTether.Robot/Program.cs:126`
  - `dotNet/ReachTether.Robot/Program.cs:209`

2. Media ownership is not yet modeled as long-lived supervised workers.
- `LocalAudioSession` opens/tears down ALSA devices per capture and playback operation.
- The docs imply explicit resource ownership and bounded, supervised media loops on-device.
- References:
  - `dotNet/ReachTether.Audio.Alsa/LocalAudioSession.cs:56`
  - `dotNet/ReachTether.Audio.Alsa/LocalAudioSession.cs:103`

3. Hot-path allocation and temp-file churn remain high for CM4-class constraints.
- Per-read byte array allocation in ALSA read path.
- Capture aggregation allocates repeatedly and transcription writes/deletes temp WAV each turn.
- This is opposite the doc guidance around pooling and bounded streaming.
- References:
  - `dotNet/ReachTether.Audio.Alsa/AlsaPcmDevice.cs:70`
  - `dotNet/ReachTether.Robot/Program.cs:318`
  - `dotNet/ReachTether.Robot/Program.cs:356`

4. Media, dialogue policy, and robot behavior are still tightly coupled in one top-level loop.
- The docs propose explicit separation into media plane, conversation/session plane, and tools/skills plane.
- Current flow mixes these concerns in the same orchestration block.
- Reference:
  - `dotNet/ReachTether.Robot/Program.cs:76`

5. WebRTC session diagnostics are good, but recovery is not yet supervisor-driven.
- Session moves to `Recovering` on ICE issues and gathers diagnostics.
- There is no owning process-level supervisor to enforce restart policy and lifecycle contracts.
- References:
  - `dotNet/ReachTether.WebRtc/ReachyWebRtcSession.cs:472`
  - `dotNet/ReachTether.WebRtc/ReachyWebRtcSession.cs:99`

6. Daemon endpoint defaults may cause portability mismatch.
- SDK and app defaults use `http://localhost:8080`.
- The docs center daemon REST/WS around `localhost:8000`.
- If intentional in your environment, keep it; otherwise standardize to reduce friction.
- References:
  - `dotNet/ReachyMini.Sdk/Configuration/ReachyMiniOptions.cs:11`
  - `dotNet/ReachTether.Robot/Program.cs:31`

## What is already directionally correct
- ALSA logical device defaults align with Reachy media model:
  - `dotNet/ReachTether.Audio.Alsa/LocalAudioOptions.cs:5`
- Bounded inbound frame queue and session diagnostics are strong foundations:
  - `dotNet/ReachTether.Audio/BoundedAudioFrameQueue.cs:5`
  - `dotNet/ReachTether.WebRtc/ReachyWebRtcSession.cs:99`

## Practical evolution sequence
1. Recast runtime into supervised hosted services:
- `AudioCaptureService`
- `AudioPlaybackService`
- `ConversationStateMachineService`
- `RealtimeTransportService`
- `RobotMotionOrchestratorService`

2. Replace clip-based capture with continuous bounded channels:
- wake-word/VAD gate
- explicit barge-in transitions and cancellation semantics

3. Remove temp-file transcription path and reduce hot-path allocations:
- move to streaming/in-memory request path
- introduce pooled buffers in ALSA read/write hot paths

4. Keep robot control at daemon boundary with explicit arbitration:
- central command queue for robot behaviors
- state subscription feeding policy, not direct loop coupling

5. Add a tools/skills boundary before scaling features:
- plugin-style contracts for tools
- isolate RAG and long-term memory off-device, keep only bounded on-device cache

