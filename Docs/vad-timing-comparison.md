# VAD Timing Comparison: `reachy_mini_conversation_app` vs `ReachTether`

## Scope
This document compares timing and turn-boundary behavior around VAD between:
- Sample app: `reachy_mini_conversation_app`
- Your app: `reachtether/dotNet/ReachTether.Robot` (realtime path)

The goal is to explain why ReachTether appears less responsive than the Python app and seems to cut users off mid-speech more often.

## Findings

### 1) Immediate mic-send disable on speech-stop is a direct cutoff driver
- ReachTether disables mic send immediately on server speech-finished:
  - `reachtether/dotNet/ReachTether.Robot/Realtime/Handlers/SpeechBoundaryHandler.cs:32`
- Sample app keeps appending mic frames while connected, including after `speech_stopped` events:
  - `reachy_mini_conversation_app/src/reachy_mini_conversation_app/openai_realtime.py:294`
  - `reachy_mini_conversation_app/src/reachy_mini_conversation_app/openai_realtime.py:507`

This makes ReachTether less tolerant to micro-pauses and trailing syllables, and is the most direct explanation for mid-speech truncation.

### 2) Stereo downmix strategy can weaken speech signal before VAD sees it
- ReachTether captures stereo (`Channels = 2`) and averages channels before send:
  - `reachtether/dotNet/ReachTether.Robot/Program.cs:68`
  - `reachtether/dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs:406`
  - `reachtether/dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs:573`
- Sample app collapses multi-channel input by selecting channel 0 (not averaging):
  - `reachy_mini_conversation_app/src/reachy_mini_conversation_app/openai_realtime.py:494`
  - `reachy_mini_conversation_app/src/reachy_mini_conversation_app/openai_realtime.py:495`

Averaging can attenuate speech or inject a noisier channel into the signal, making server VAD more likely to decide speech ended early.

### 3) Input sample-rate mismatch remains a likely contributor
- Sample app sends microphone PCM to Realtime at **24 kHz**:
  - `reachy_mini_conversation_app/src/reachy_mini_conversation_app/openai_realtime.py:30`
  - `reachy_mini_conversation_app/src/reachy_mini_conversation_app/openai_realtime.py:244`
- ReachTether captures and sends audio at **16 kHz**:
  - `reachtether/dotNet/ReachTether.Robot/Program.cs:67`
  - `reachtether/dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs:414`

If the server VAD path behaves best at 24 kHz, 16 kHz input can still contribute to earlier speech-stop decisions and choppier segmentation.

### 4) ReachTether treats more realtime events as hard failures
- Any `ConversationErrorUpdate` becomes a turn failure:
  - `reachtether/dotNet/ReachTether.Robot/Realtime/Handlers/ResponseLifecycleHandler.cs:20`
- Sample app explicitly suppresses common benign realtime errors:
  - `reachy_mini_conversation_app/src/reachy_mini_conversation_app/openai_realtime.py:466`
  - ignored: `input_audio_buffer_commit_empty`, `conversation_already_has_active_response`

Result: ReachTether surfaces more user-visible failures for conditions the sample tolerates.

### 5) ReachTether delays assistant audio until input transcript exists
- ReachTether streams assistant audio only when `UserTranscript` is already non-empty:
  - `reachtether/dotNet/ReachTether.Robot/Realtime/Handlers/StreamingAudioHandler.cs:24`
- Sample app streams assistant audio deltas immediately:
  - `reachy_mini_conversation_app/src/reachy_mini_conversation_app/openai_realtime.py:354`

This can make ReachTether feel less responsive even when turn detection is otherwise correct.

### 6) ReachTether has stricter per-turn timeout boundaries
- Listen timeout and response timeout enforce hard turn failures:
  - `reachtether/dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs:391`
  - `reachtether/dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs:441`

These controls are useful, but tighter failure semantics can look like extra interruptions compared with the sample flow.

### 7) Motion VAD constants are effectively matched
For robot talking-gesture/motion VAD (not turn detection), constants align between apps:
- ReachTether:
  - `reachtether/dotNet/ReachTether.Robot/SwayRollRt.cs:12`
- Sample:
  - `reachy_mini_conversation_app/src/reachy_mini_conversation_app/audio/speech_tapper.py:18`

Both use:
- `VAD_DB_ON = -35`
- `VAD_DB_OFF = -45`
- attack `40 ms`
- release `250 ms`

So the interruption issue is likely not from motion VAD thresholds themselves.

## Ranked Likely Contributors (Highest to Lowest)
1. Immediate mic-send disable on server speech-finished.
2. Stereo channel averaging before send (signal attenuation/noise mixing risk).
3. 16 kHz input path in ReachTether vs 24 kHz sample app path.
4. Assistant audio streaming gated on transcript availability.
5. Hard-fail handling for realtime errors that sample app treats as benign.
6. Stricter timeout-driven turn failure behavior.

## VAD Timing Implementation Path (ReachTether)
The sequence below keeps each change independently testable and minimizes regressions.

### Phase 1: Turn-boundary smoothing (highest impact for cutoff)
1. Replace immediate mic-send disable on server speech-finished with a short grace window (`250-350 ms`) to tolerate brief pauses.
2. Cancel the grace cutoff if speech resumes before timeout.
3. Keep this behavior configurable for A/B testing.

Primary touchpoints:
- `reachtether/dotNet/ReachTether.Robot/Realtime/Handlers/SpeechBoundaryHandler.cs`

### Phase 2: Input-signal quality alignment
1. Add an option to send a single capture channel (for A/B) instead of averaging both channels.
2. If averaging is kept, add level/peak logging to verify speech energy is not reduced too aggressively.
3. Confirm whether one channel is consistently cleaner; prefer that channel for realtime input if so.

Primary touchpoint:
- `reachtether/dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs`

### Phase 3: Audio format alignment
1. Align capture/send path to `24_000 Hz` PCM16 for realtime.
2. If capture hardware is fixed at `16_000 Hz`, resample to `24_000 Hz` before `SendInputAudioAsync`.
3. Add startup logging for effective capture rate and outbound rate.

Primary touchpoints:
- `reachtether/dotNet/ReachTether.Robot/Program.cs`
- `reachtether/dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs`

### Phase 4: Assistant response immediacy
1. Remove transcript-required gating for assistant audio playback, or soften it behind a guard flag.
2. Keep shutdown-intent suppression behavior intact.
3. Measure "first assistant audio delta to playback" latency before and after.

Primary touchpoint:
- `reachtether/dotNet/ReachTether.Robot/Realtime/Handlers/StreamingAudioHandler.cs`

### Phase 5: Error-classification hardening
1. Treat known benign realtime errors as non-fatal:
   - `input_audio_buffer_commit_empty`
   - `conversation_already_has_active_response`
2. Log benign errors at debug level; preserve hard-fail behavior for unknown/fatal errors.

Primary touchpoint:
- `reachtether/dotNet/ReachTether.Robot/Realtime/Handlers/ResponseLifecycleHandler.cs`

### Phase 6: Timeout retuning
1. Re-baseline listen and response timeouts after phases 1-5.
2. Increase thresholds only where telemetry shows false failures.
3. Keep per-turn timeout outcomes visible in logs/metrics.

Primary touchpoint:
- `reachtether/dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs`

### Acceptance criteria for VAD timing
- Fewer user-observed mid-thought interruptions in realtime conversations.
- No increase in stuck/open turns after the grace-window change.
- Improved perceived responsiveness at start of assistant speech.
- Lower rate of failure events caused by known benign realtime errors.
- Timeout-driven failures occur mostly on true silence/network/model delays, not brief speech pauses.

## Related Document
Personality tool-use parity planning has been moved to:
- `reachtether/Docs/personality-tool-access-parity.md`
