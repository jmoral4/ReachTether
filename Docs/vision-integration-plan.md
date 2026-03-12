# Vision Integration Plan: SDK Camera Path, No WebRTC

## Overview

This document replaces the earlier WebRTC-based approach.

For `ReachTether`, vision should be built around the same high-level idea that works in the Python
reference app:

1. Get a snapshot from the robot through the supported robot API.
2. Keep vision on-demand unless a later feature proves continuous processing is affordable.
3. Feed that image into the active OpenAI conversation flow.

WebRTC is explicitly out of scope for this plan. The current .NET app does not use
`ReachyWebRtcSession`, and prior debugging showed that path was expensive and unreliable for this
project. We should not make vision dependent on RTP/H.264 decode.

The design target is a pull-based camera API in `ReachyMini.Sdk`, equivalent in spirit to the Python
app's `reachy_mini.media.get_frame()`.

---

## 1. What the Python Reference App Actually Does

### 1.1 Frame Source

The Python app does not use WebRTC for camera frames. It pulls frames directly from the robot SDK:

- `camera_worker.py` calls `reachy_mini.media.get_frame()`
- the latest frame is stored under a lock
- consumers call `get_latest_frame()`

This is the key architectural point. The working reference path is "SDK media API -> frame buffer",
not "WebRTC video track -> decode callback".

### 1.2 Camera Tool

When the model calls the `camera` tool:

1. The tool fetches the latest frame from `CameraWorker`.
2. It JPEG-encodes the frame with OpenCV.
3. It base64-encodes the JPEG bytes.
4. It returns `{"b64_im": "<base64>"}`.

The realtime session then injects that image into the OpenAI conversation as an `input_image` item
and asks the model to continue.

### 1.3 Optional Features

The Python app has two optional layers on top of the basic camera tool:

- local ambient scene description via `vision/processors.py`
- face tracking via `vision/yolo_head_tracker.py`

Those are useful future references, but they are not required for the MVP.

---

## 2. What ReachTether Actually Looks Like Today

### 2.1 Active Robot App Path

The current .NET robot app is built around:

- `ReachyMini.Sdk` for daemon/move/state REST calls
- `LocalAudioSession` for ALSA capture/playback
- `OpenAI` realtime or Responses API for conversation

The hosted robot app does not currently register or use `ReachyWebRtcSession`.

### 2.2 Existing Useful Pieces

The codebase already has some pieces that help:

- `OpenAiTransport` can already serialize image content for the Responses API
- `MotionOrchestrator` already exists for robot motion output
- `RobotAppOptions` already has the pattern we should follow for new `Vision` settings

### 2.3 Missing Pieces

What is missing for vision today:

- camera/media support in `ReachyMini.Sdk`
- a vision abstraction in `ReachTether.Robot`
- tool execution in the legacy chat path
- tool registration and tool-call handling in the .NET realtime path
- any image capture, caching, or JPEG conversion path

---

## 3. Design Decision

### 3.1 Hard Constraint

Do not use WebRTC for vision.

### 3.2 Camera Source

Add or expose a camera snapshot capability through `ReachyMini.Sdk`, backed by the daemon/API that
the Python SDK uses for `reachy_mini.media.get_frame()`.

Preferred order:

1. If the daemon already exposes an HTTP endpoint for camera snapshots or frames, add a .NET client
   for that endpoint.
2. If the daemon exposes another non-WebRTC API that is already used by the Python SDK, mirror that.
3. If no such daemon API exists, add one to the daemon and then add it to `ReachyMini.Sdk`.

The important part is that the .NET app uses a supported robot API surface, not RTP decode.

### 3.3 MVP Behavior

For the MVP, the `camera` tool should behave like the Python reference app:

- capture a recent image
- return or inject that image into the active model flow
- let the model answer the user's visual question

Do not start with periodic inference or tracking. First prove that single-image capture works
reliably in the current app.

---

## 4. Target Architecture

```text
Reachy Mini Daemon / Camera API
        |
        v
ReachyMini.Sdk
  - CameraClient or MediaClient
  - CaptureSnapshotAsync()
        |
        v
ReachTether.Robot/Vision
  - ICameraSnapshotProvider
  - CameraSnapshotService
  - optional latest-frame cache
        |
        +--> camera tool (legacy Responses flow)
        |
        +--> camera tool (OpenAI realtime flow)
        |
        +--> future face tracking / ambient context
```

This keeps the transport simple:

- robot camera access stays in the SDK layer
- robot app code consumes snapshots, not streams
- OpenAI integration consumes JPEG images, not raw H.264 or YUV

---

## 5. Implementation Phases

### Phase 0 - Add Camera Access to `ReachyMini.Sdk`

Goal: make camera capture a first-class capability in the .NET SDK.

Possible shape:

```csharp
public sealed record CameraSnapshot(
    byte[] ImageBytes,
    string MediaType,
    DateTimeOffset CapturedAt);

public sealed class CameraClient
{
    public Task<CameraWarmupResult> WarmupAsync(CancellationToken cancellationToken = default);
    public Task<CameraSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default);
}
```

Notes:

- Prefer returning JPEG directly if the daemon can provide it.
- If the daemon only returns raw pixel data, convert once inside the SDK or robot layer.
- Open the camera pipeline during robot startup and keep it alive for app lifetime.
- Do not add a continuous background frame pump/cache until measurements show startup warmup alone is insufficient.

Exit criteria:

- from a small console/sample app, capture one real image from the robot successfully
- save or inspect the bytes to verify the image is valid

Effort: medium, but this is the real gating step.

### Phase 1 - Add Vision Abstractions to `ReachTether.Robot`

Goal: isolate camera capture from OpenAI/tooling concerns.

Suggested files:

```text
ReachTether.Robot/
  Vision/
    VisionOptions.cs
    CameraSnapshot.cs
    ICameraSnapshotProvider.cs
    CameraSnapshotService.cs
```

Suggested service contract:

```csharp
public interface ICameraSnapshotProvider
{
    Task<CameraSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default);
}
```

Notes:

- If the SDK already returns JPEG bytes, this layer can stay very small.
- If capture latency is high, this service may optionally keep a short-lived cached snapshot.
- Prefer a short-lived latest-frame cache over a full polling loop if we need more warmup than startup-open provides.

Effort: small.

### Phase 2 - Add Vision Configuration

Goal: introduce configuration without touching unrelated app settings.

Add a `Vision` section to `appsettings.json` and parse it in `RobotAppOptions`.

Example:

```json
"Vision": {
  "Enabled": true,
  "SnapshotCacheMs": 500,
  "AmbientContextEnabled": false,
  "AmbientContextIntervalSeconds": 5.0,
  "FaceTrackingEnabled": false,
  "FaceTrackingHz": 5
}
```

Effort: small.

### Phase 3 - Enable the Camera Tool in the Legacy Responses Path

Goal: make the non-realtime chat path capable of handling tool calls with images.

Current state:

- `OpenAiTransport` already supports tool definitions and can serialize `input_image`
- `InteractionOrchestrator` currently reports that tool execution is not enabled

Required work:

1. Register a `camera` tool definition.
2. When the model requests `camera`, capture a snapshot.
3. Continue the conversation by adding the image and the tool result in a way the model can use.

Recommended behavior:

- keep the `camera` tool schema close to Python:

```json
{
  "type": "object",
  "properties": {
    "question": {
      "type": "string",
      "description": "What to look for in the current camera image."
    }
  },
  "required": ["question"]
}
```

- after capture, add a `UserChatMessage` containing:
  - the image
  - the question text

That preserves the Python behavior better than a separate hard-coded "describe the scene" API call.

Effort: medium.

### Phase 4 - Enable the Camera Tool in the .NET Realtime Path

Goal: add the same capability to `RealtimeInteractionOrchestrator`.

Current state:

- the .NET realtime path configures audio/text only
- it does not register tools
- it does not process function-call events

Required work:

1. Add tool definitions when configuring the realtime session.
2. Handle function-call events from the OpenAI .NET realtime client.
3. For `camera`:
   - capture a snapshot
   - add an `input_image` conversation item
   - trigger a follow-up response

This should mirror the Python flow conceptually, even if the exact .NET SDK event types differ.

Effort: medium to large, depending on the OpenAI .NET realtime surface.

### Phase 5 - Optional Snapshot Cache or Polling Worker

Goal: only if needed, reduce latency by keeping a recent image in memory.

This should be introduced only after measuring capture latency.

If needed:

- add a lightweight background worker
- refresh at a low rate
- store only the latest frame or JPEG

Do not assume a 25 Hz worker is necessary just because Python has one. The .NET app may be fine with
pull-on-demand snapshots.

Effort: small to medium.

### Phase 6 - Optional Ambient Scene Context

Goal: periodically summarize the scene without an explicit tool call.

Possible approaches:

- remote vision call on a timer
- local ONNX model only if it is proven practical on the target hardware

Recommendation:

- start with remote vision if this feature becomes important
- defer local inference until there is evidence the CPU and memory budget can handle it

Effort: medium to large.

### Phase 7 - Optional Face Tracking

Goal: derive motion offsets from camera images and feed them into the motion system.

Possible implementation:

- capture snapshots at a controlled rate
- run a detector
- map face position to motion offsets
- blend with the existing motion output path

Important note:

`MotionOrchestrator` currently consumes talking gestures from assistant audio, not look-target input.
Face tracking will require a new motion input path or a composition layer, not just a small add-on.

Effort: large.

---

## 6. Suggested File Layout

```text
ReachyMini.Sdk/
  Clients/
    CameraClient.cs
  Models/
    CameraModels.cs

ReachTether.Robot/
  Vision/
    VisionOptions.cs
    CameraSnapshot.cs
    ICameraSnapshotProvider.cs
    CameraSnapshotService.cs
    CameraTool.cs
    RealtimeCameraToolHandler.cs
    SceneContextService.cs        # optional
    FaceTrackingService.cs        # optional
```

---

## 7. OpenAI Integration Notes

### Legacy Responses Path

This path already has the better base for image support:

- `OpenAiTransport` can turn image content into `input_image`
- tool definitions already exist in local types

The missing part is orchestrator-side tool execution.

### Realtime Path

This path needs new work:

- register tools
- handle function-call events
- inject image items
- resume the response after the tool returns

The Python app is the behavioral reference for this flow.

### Do Not Start With a Separate Vision-Only Chat Call

A dedicated `DescribeSceneAsync()` call can be useful later, but it should not be the MVP.

Why:

- the Python app lets the active model ask a specific visual question
- that preserves conversational context better
- it avoids splitting reasoning across two different model calls unless we actually need that

---

## 8. Package Summary

| Package | Purpose | Phase |
|---|---|---|
| `OpenAI` | Existing OpenAI chat/realtime integration | 3, 4 |
| `SixLabors.ImageSharp` | Only if we must convert raw pixels to JPEG in .NET | 0 or 1 |
| `Microsoft.ML.OnnxRuntime` | Optional local inference or tracking later | 6, 7 |

Do not add WebRTC decode packages for this feature.

---

## 9. Risks and Mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| No camera endpoint exists behind the Python SDK media API | Medium | Inspect the daemon/Python SDK implementation first and add a supported daemon API if needed |
| Snapshot capture is too slow for conversational use | Medium | Add a short-lived cache or low-rate polling worker after measuring |
| Realtime .NET SDK tool handling is awkward or incomplete | Medium | Deliver the legacy Responses path first, then add realtime support |
| Image conversion is needed and color format is unclear | Low | Prefer daemon-provided JPEG; otherwise validate conversion with saved sample images |
| Ambient local inference is too heavy on target hardware | High | Keep it out of the MVP; use remote vision only if needed |

---

## 10. Recommended Implementation Order

```text
Phase 0: ReachyMini.Sdk camera snapshot support
    |
    +--> Phase 1: ReachTether.Robot camera abstraction
            |
            +--> Phase 2: Vision options/config
            |
            +--> Phase 3: Legacy Responses camera tool
            |
            +--> Phase 4: Realtime camera tool
                    |
                    +--> Phase 5: optional cache/polling
                    +--> Phase 6: optional ambient scene context
                    +--> Phase 7: optional face tracking
```

The MVP is Phases 0 through 4.

That gives us:

- no WebRTC dependency
- a camera path that matches the Python app's real transport model
- a clear way to add image-aware tool use to both conversation modes

---

## 11. Python to .NET Equivalence Table

| Python component | .NET target | Notes |
|---|---|---|
| `reachy_mini.media.get_frame()` | `ReachyMini.Sdk.CameraClient.CaptureSnapshotAsync()` | Same architectural role |
| `CameraWorker.get_latest_frame()` | optional `CameraSnapshotService` cache | Only add if latency requires it |
| `cv2.imencode('.jpg', frame)` | no-op if daemon returns JPEG; otherwise ImageSharp encode | Prefer JPEG from source |
| `tools/camera.py` | `CameraTool` | Keep `question` parameter |
| `conversation.item.create(input_image)` | Responses image content or realtime image item injection | Depends on OpenAI client surface |
| `vision/processors.py` | optional `SceneContextService` | Future feature |
| `yolo_head_tracker.py` | optional `FaceTrackingService` | Future feature |
