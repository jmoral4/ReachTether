# ReachTether Vision Plan

## Goal

Add a video frame processing layer to ReachTether that runs on the robot in .NET, stays lightweight on ARM, and borrows the right ideas from the Python reference app without copying its heavyweight local-vision stack.

## What the Python app actually does

Reference app: `C:\git\reachy-apps\reachy_mini_conversation_app`

The useful part to copy is the structure:

- `camera_worker.py` keeps only the latest frame in memory behind a lock.
- `tools/camera.py` reads that latest frame on demand.
- The default path is cheap: JPEG-encode the latest frame and send it to the model when needed.
- The expensive parts are optional:
  - `vision/processors.py` loads SmolVLM2 through Torch + Transformers.
  - `vision/yolo_head_tracker.py` uses Ultralytics + Supervision + downloaded YOLO weights.

That split matters. The Python app is not doing continuous heavyweight inference by default. Its default camera path is "latest frame + on-demand analysis."

## What ReachTether has today

Relevant current state in this repo:

- The running robot app in `dotNet/ReachTether.Robot/Program.cs` is HTTP + ALSA based.
- The active runtime does not currently instantiate `ReachyWebRtcSession`.
- `dotNet/ReachTether.WebRtc/ReachyWebRtcSession.cs` negotiates a recv-only H.264 video track, but the current implementation only handles audio frames.
- `dotNet/ReachTether.Robot/OpenAiTransport.cs` already supports `input_image` content for the Responses API.
- `dotNet/ReachTether.Robot/InteractionOrchestrator.cs` can parse tool calls, but tool execution is still stubbed out.

Implication: the MVP is not just "decode frames." We need:

1. A frame source in the robot runtime.
2. A latest-frame buffer service.
3. A camera tool execution path.
4. Image encoding and model call plumbing.

## Recommendation

Build the MVP around snapshot vision, not continuous local inference.

That means:

- Keep one latest frame in memory.
- Let the assistant call a `camera` tool.
- Encode one JPEG on demand.
- Send that image to OpenAI via the existing Responses API support.

Do not copy the Python local SmolVLM2 path into .NET for the first version. It is too heavy for a small ARM box and brings the wrong dependency profile.

## What to copy from Python

Copy these ideas directly:

- A dedicated frame source that updates a single latest-frame buffer.
- Thread-safe snapshot access.
- A small processing abstraction: `GetLatestFrame()` plus optional processors.
- On-demand camera analysis as a tool call.
- Separate "capture" from "analysis" so future face tracking stays optional.

## What not to copy from Python

Do not copy these pieces as-is:

- Torch + Transformers local VLM inference from `vision/processors.py`.
- SmolVLM2-2.2B on-device.
- Ultralytics/Supervision/Hugging Face download flow for the first milestone.
- A continuous every-frame inference loop.

Those are fine for experimentation, but they are not the right first implementation for ReachTether on CM4-class hardware.

## Proposed .NET architecture

Add a small vision subsystem under `ReachTether.Robot`:

```text
ReachTether.Robot/
  Vision/
    IVideoFrameSource.cs
    IVideoFrameBuffer.cs
    VideoFrameBuffer.cs
    VisionOptions.cs
    IImageEncoder.cs
    ImageSharpJpegEncoder.cs
    CameraTool.cs
    ToolDispatcher.cs
```

Core contracts:

```csharp
public sealed record VideoFrame(
    byte[] PixelBytes,
    int Width,
    int Height,
    PixelFormat PixelFormat,
    DateTimeOffset CapturedAt);

public interface IVideoFrameBuffer
{
    VideoFrame? GetLatest();
    void Update(VideoFrame frame);
}

public interface IVideoFrameSource : IHostedService
{
}
```

The buffer should keep only one frame. No deep queue for the MVP.

## Recommended implementation phases

### Phase 1: Add a latest-frame buffer and tool execution

Deliverables:

- `VideoFrameBuffer` singleton with lock-based latest-frame storage.
- `CameraTool` that:
  - reads the latest frame,
  - JPEG-encodes it,
  - sends it to OpenAI with a prompt,
  - returns a short textual result.
- `ToolDispatcher` in the legacy orchestrator so `ToolCallResult` is actually executed.

Why first:

- `OpenAiTransport` already supports image parts.
- This gives you user-visible value quickly.
- It matches the Python app's default behavior.

### Phase 2: Add a real video frame source

Add an `IVideoFrameSource` implementation with a pluggable backend.

Use this order of preference:

1. `ReachyWebRtcVideoFrameSource`
2. `ReachyDaemonSnapshotSource` if the daemon exposes an easier local camera path
3. A thin external decoder wrapper only if the .NET-only path fails

The key design point is to keep the source behind an interface so the rest of the app does not care how frames arrive.

### Phase 3: Wire camera tool into both orchestrators

- Legacy path: execute tool calls returned by `CompleteChatAsync`.
- Realtime path: add the same camera tool behind the realtime session's tool handling.

This repo already has the concept of tool definitions and tool-call parsing. The missing piece is execution.

### Phase 4: Optional lightweight local processing

Only after the MVP works:

- Add face detection or tracking.
- Keep it low-rate, for example 2-5 Hz.
- Feed normalized target offsets into `MotionOrchestrator`.

Do not add ambient scene summarization until capture and camera tool behavior are stable.

## Frame source options

### Option A: Extend `ReachyWebRtcSession`

This is the most natural reuse path in this repo.

Pros:

- You already negotiate a video track in `dotNet/ReachTether.WebRtc/ReachyWebRtcSession.cs`.
- It keeps transport logic inside the existing WebRTC project.
- It should align with the robot's current media architecture better than inventing a parallel camera transport.

Cons:

- Current code does not expose decoded video frames.
- It is not yet verified that the current SIPSorcery setup on your target gives you decoded H.264 frames cleanly on linux-arm64.

Recommendation:

- Treat this as the preferred path, but verify it early with a spike.
- Do not assume the necessary decoded-frame callback already exists in your current dependency stack.

### Option B: Direct daemon/local snapshot source

If Reachy exposes a simpler camera endpoint or local media export, use it for snapshots.

Pros:

- Potentially simpler than full WebRTC video decode.
- Better fit for the MVP, because the MVP only needs the latest frame.

Cons:

- This repo does not currently expose such a path in `ReachyMini.Sdk`.
- It may require daemon/API research or SDK extension work.

Recommendation:

- Worth checking, but do not block the design on it.

### Option C: Thin native decoder wrapper

If WebRTC negotiation works but decoded frames are difficult to access from SIPSorcery, use a small native-assisted decode path behind `IVideoFrameSource`.

Examples:

- FFmpeg-based decode
- GStreamer-based decode

Pros:

- Pragmatic fallback.
- Lets .NET remain the orchestration layer while a mature media stack handles H.264.

Cons:

- Not pure managed .NET.
- Deployment becomes more complex.

Recommendation:

- Acceptable fallback.
- Keep it behind the frame-source interface so it does not leak through the app.

## Image encoding recommendation

Use `SixLabors.ImageSharp`.

Why:

- Managed implementation.
- Good linux-arm64 story.
- Adequate for low-frequency JPEG snapshots.
- Lighter operational burden than OpenCV bindings for the MVP.

Avoid for the MVP:

- `OpenCvSharp`: heavier native dependency surface.
- `SkiaSharp`: viable, but still native-library oriented.
- `System.Drawing.Common`: not the right cross-platform choice here.

## Local processing recommendation

For the first local processor, use ONNX Runtime if you need it.

Preferred candidates:

- A small face detector exported to ONNX
- OpenCV Haar/DNN only if you want the absolute simplest detector and accuracy is secondary

Recommended library:

- `Microsoft.ML.OnnxRuntime`

Why:

- Better fit than porting Python's Torch stack.
- Common deployment path for lightweight inference in .NET.
- Lets you keep CPU-only inference and quantized models.

Avoid first:

- Full local VLM on the robot.
- Anything with multi-GB model weights.

## Concrete MVP design

### 1. New config

Add a `Vision` section in app settings:

```json
"Vision": {
  "Enabled": true,
  "FrameSource": "webrtc",
  "JpegQuality": 85,
  "Model": "gpt-4.1-mini",
  "Detail": "low",
  "Prompt": "Answer the user's camera question briefly and only describe visible facts."
}
```

### 2. New services

- `VideoFrameBuffer`
- `CameraTool`
- `ToolDispatcher`
- `IVideoFrameSource` implementation

### 3. Tool shape

Match the Python tool concept:

```json
{
  "type": "object",
  "properties": {
    "question": {
      "type": "string",
      "description": "What to look for in the current camera frame"
    }
  },
  "required": ["question"]
}
```

### 4. Execution flow

1. Model requests `camera`.
2. `ToolDispatcher` parses the question.
3. `CameraTool` gets the latest frame.
4. `ImageSharpJpegEncoder` creates a JPEG.
5. `OpenAiTransport` sends a user message with:
   - input text from the tool question
   - input image from the JPEG bytes
6. Tool returns a short factual result.
7. Assistant uses that result in its final spoken response.

## Suggested implementation order

1. Add `VisionOptions`, `VideoFrameBuffer`, and `CameraTool`.
2. Add legacy tool execution in `InteractionOrchestrator`.
3. Add a fake/in-memory test frame source so the tool path can be tested without live video.
4. Spike `ReachyWebRtcSession` video extraction.
5. Replace the fake source with the real source.
6. Add realtime tool execution.
7. Consider face tracking only after the camera tool is stable.

## Expected copy vs rewrite

What can be copied conceptually from Python:

- single latest-frame worker
- on-demand camera tool
- optional processor manager
- normalized face target abstraction

What should be rewritten for .NET:

- threading model
- frame storage types
- JPEG pipeline
- tool dispatch
- local model strategy

## Risks

### Risk 1: Video decode path in SIPSorcery is not straightforward on ARM

Mitigation:

- Run a short spike before deeper implementation.
- Keep `IVideoFrameSource` abstract so fallback is cheap.

### Risk 2: Tool execution work grows beyond vision

Mitigation:

- Build a generic `ToolDispatcher` now, not a camera-only hack.

### Risk 3: CPU load interferes with audio or motion

Mitigation:

- Keep only one latest frame.
- No continuous heavy inference.
- Snapshot only on tool calls.
- If tracking is added later, run it at low Hz.

## Final recommendation

The right thing to copy from the Python app is not SmolVLM2 or the YOLO stack. The right thing to copy is the architecture:

- latest frame buffer
- on-demand camera tool
- optional processors layered on later

For ReachTether on a small ARM device, the best MVP is:

1. add a generic tool execution loop,
2. add a latest-frame video source,
3. JPEG-encode snapshots with ImageSharp,
4. send them through the existing Responses API image support,
5. postpone local inference until you have a proven frame source and measured CPU headroom.

That gives you native .NET vision behavior with the smallest risk and the closest functional match to the Python reference app's default camera flow.
