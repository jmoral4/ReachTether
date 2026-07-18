# ReachTether Priority 0 Review Findings

Source checkout reviewed: `C:\git\reachy-apps\reachtether`

Review target: Priority 0 remediation recorded in `Docs/code-staleness-and-completeness-audit-2026-07-18.md`

The remediation should not be accepted yet. The review found three runtime correctness defects and one material test gap.

## 1. Critical: interrupted or cancelled responses can execute tools

Evidence:

- `dotNet/ReachTether.Robot/Realtime/OpenAiRealtimeVoiceSession.cs:223-234` maps both `RealtimeServerUpdateResponseFunctionCallArgumentsDone` and `RealtimeServerUpdateResponseOutputItemDone` directly to `RealtimeFunctionCallEvent`.
- `dotNet/ReachTether.Robot/Realtime/RealtimeVoiceContracts.cs:97-102` does not preserve the response ID, item ID, or item/response status.
- `dotNet/ReachTether.Robot/Realtime/Handlers/FunctionCallHandler.cs:19-35` immediately executes every newly observed function-call ID.
- GA Realtime documents that both function-call-arguments-done and output-item-done events can also be emitted when a response is interrupted, incomplete, or cancelled.

Impact:

A user barge-in can cause incomplete or abandoned function arguments to execute. This includes remote tools such as the scheduler, so the behavior can produce external side effects rather than merely a bad response.

Recommended remediation:

- Preserve response ID, item ID, and status through the adapter contract.
- Collect pending function calls by response and item ID.
- Execute a call only after a matching `response.done` reports successful completion and the function-call item is complete.
- Do not treat `response.function_call_arguments.done` alone as authorization to execute a tool.
- Add tests for interrupted, incomplete, and cancelled tool-call responses.

Reference: <https://developers.openai.com/api/reference/resources/realtime>

## 2. High: barge-in contaminates the replacement assistant response

Evidence:

- `dotNet/ReachTether.Robot/Realtime/Handlers/SpeechBoundaryHandler.cs:47-50` clears `ActiveResponseId` during barge-in.
- `dotNet/ReachTether.Robot/Realtime/Handlers/StreamingAudioHandler.cs:77-80` treats a missing active response ID as matching every response.
- `dotNet/ReachTether.Robot/Realtime/Handlers/StreamingAudioHandler.cs:42-68` appends matching transcript and text events to a single `AssistantText` buffer.
- `dotNet/ReachTether.Robot/Realtime/Handlers/ResponseLifecycleHandler.cs:14-24` does not clear or segment `AssistantText` when the replacement response begins.
- GA Realtime emits final transcript/text events for cancelled responses.

Impact:

Trailing events from the interrupted response are accepted after `ActiveResponseId` is cleared. A subsequent response then appends to the same buffer, so displayed and persisted assistant text can combine the interrupted answer with its replacement.

Recommended remediation:

- Require exact response-ID matching for response-scoped output events.
- Retain interrupted response IDs in an ignored/cancelled set until their terminal events arrive.
- Clear or segment assistant text when beginning the replacement response.
- Add an event-sequence regression test that sends trailing cancelled-response events followed by a replacement response.

## 3. High: asynchronous input transcripts are not correlated to their audio item

Evidence:

- GA transcription completion and failure events include an `item_id` and run asynchronously with response generation.
- `dotNet/ReachTether.Robot/Realtime/OpenAiRealtimeVoiceSession.cs:177-186` discards the transcription item ID.
- `dotNet/ReachTether.Robot/Realtime/RealtimeVoiceContracts.cs:42-52` has no item ID on transcription events.
- `dotNet/ReachTether.Robot/Realtime/Handlers/TranscriptionHandler.cs:14-16` blindly overwrites `UserTranscript` with whichever completion arrives.
- `dotNet/ReachTether.Robot/Realtime/Handlers/ResponseLifecycleHandler.cs:75-88` accepts any nonblank transcript when completing the active response.

Impact:

With barge-in or closely spaced utterances, a late transcript from an interrupted input item can satisfy completion for a newer response. The wrong user text can be displayed or persisted, and an unrelated late transcript could incorrectly trigger shutdown-intent handling.

Recommended remediation:

- Carry the transcription `item_id` through the adapter contract.
- Track the active input item from the speech-started/speech-stopped events.
- Apply transcription success or failure only to its matching input item.
- Add tests with two input items whose transcription events arrive out of order.

## 4. Medium: tests bypass the migration's highest-risk adapter code

Evidence:

- The eight tests in `dotNet/ReachTether.Server.Tests/RealtimePipelineTests.cs` construct internal events directly and use `FakeRealtimeVoiceSession`.
- No test calls `OpenAiRealtimeVoiceSession.MapServerUpdate` or exercises GA session configuration, typed image serialization, response cancellation commands, or audio truncation command serialization.
- `RealtimeError_ProducesFailedTurnResult` only proves that an error creates a failed result. It does not exercise session reset or recovery, despite the audit describing error-recovery coverage.

Impact:

The GA SDK adapter can map or serialize an event incorrectly while all eight tests remain green. The current suite therefore does not fully substantiate the Priority 0 migration or the audit's error-recovery claim.

Recommended remediation:

- Add adapter-level mapping and configuration tests using SDK model factories or recorded protocol events.
- Cover image input, cancellation, truncation, function-call correlation, and terminal response statuses.
- Extract or expose enough of the orchestration loop to test that fatal stream/API failures dispose and recreate the session.
- Update the audit wording unless recovery is actually exercised.

## Validation Notes

- The review was read-only against the ReachTether checkout.
- `git diff --check` passed.
- The build and test suite were not independently rerun because the active writable workspace was `C:\git\LogisticaBelli`.
- The ReachTether audit reports a successful Release build and 20 passing tests, including eight realtime pipeline tests.
- No live OpenAI request or hardware smoke test was performed.
