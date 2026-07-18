Your agent’s diagnosis is basically right.

The big mistake in the rough .NET version is not “the detector is a little weak.” It is that the **architecture is wrong for tracking**. The Python app is a **continuous perception → geometry → motion** pipeline, while the rough .NET version sounds more like **occasional heuristic detection → guessed yaw/pitch jump**. That difference matters more than the specific model. A proper .NET port should preserve the Python architecture first, then swap in the lightest detector that still works. The Python-style approach maps well to .NET because there are solid ONNX Runtime options, and there are current .NET-native YOLO wrappers if you want to avoid Python entirely. ONNX Runtime is current and supports CPU plus other execution providers, and YoloDotNet is explicitly positioned as a lightweight, pure-.NET YOLO inference library built on ONNX Runtime. ([NuGet][1])

## What I would design in .NET

Use **four loops / services**, not one monolith:

1. **Camera loop**
   Continuously grabs frames at camera speed.

2. **Perception loop**
   Runs face detection at a lower rate on downscaled frames, chooses the best target, and outputs a normalized image-space point plus confidence.

3. **Geometry / targeting loop**
   Converts image coordinates into desired head pose using camera intrinsics or an approximation of the robot’s `look_at_image` behavior.

4. **Motion loop**
   Runs fast and continuously, smoothing and sending target head pose updates.

That is the .NET equivalent of the Python design your agent described.

---

## The key design principle

Do **not** let the detector directly command motors.

Instead, the detector should only publish something like:

```csharp
public readonly record struct VisualTarget(
    double NormalizedX,   // -1 left, +1 right
    double NormalizedY,   // -1 up, +1 down
    double Confidence,
    double RelativeSize,
    DateTime TimestampUtc);
```

Then a separate targeting layer turns that into head commands.

That separation buys you:

* easy detector swapping
* smoother motion
* less jitter
* graceful degradation if frames drop
* simpler testing

---

## Recommended .NET architecture

### 1) `ICameraSource`

Produces frames.

```csharp
public interface ICameraSource : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken ct);
    bool TryGetLatestFrame(out VideoFrame frame);
}
```

Important: keep only the **latest** frame. Do not build up a queue. For tracking, stale frames are poison.

---

### 2) `IHeadDetector`

Abstract detector backend.

```csharp
public interface IHeadDetector : IAsyncDisposable
{
    ValueTask<DetectionResult?> DetectAsync(VideoFrame frame, CancellationToken ct);
}
```

Where:

```csharp
public sealed record DetectionResult(
    Rect BoundingBox,
    double Confidence,
    PointF CenterNormalized,
    double AreaNormalized);
```

Backends:

* `YoloFaceDetector`
* `YuNetFaceDetector`
* later maybe `MediaPipeFaceDetector`

For your use case, I would start with **YOLO face detection** or **YuNet face detection**, not full person detection.

Why:

* face center is the best cue for “look at the human”
* smaller search space
* less compute
* less false activation from random objects

If you want minimal dependency footprint in .NET, **YoloDotNet** is a serious option because it is pure .NET on top of ONNX Runtime and intentionally avoids heavy frameworks like OpenCV. ([NuGet][2])

If you want the simplest path to a tiny face detector, OpenCV’s `FaceDetectorYN` / YuNet remains attractive because OpenCV describes YuNet as a lightweight face detector. ([NuGet][3])

---

### 3) `ITargetSelector`

If multiple faces exist, choose one.

```csharp
public interface ITargetSelector
{
    DetectionResult? SelectBest(IReadOnlyList<DetectionResult> detections, DetectionResult? previous);
}
```

Typical policy:

* prefer previous target if still present
* otherwise maximize:

  * confidence
  * size
  * closeness to image center

For a social robot, this prevents “jumping heads” between people.

A simple score:

```text
score =
  0.50 * confidence +
  0.30 * normalizedArea +
  0.20 * centerBias
```

With temporal stickiness bonus if it matches prior target.

---

### 4) `ILookAtProjector`

This is the most important missing piece from the rough .NET version.

Do **not** manually scale image X/Y to yaw/pitch as your primary model.

Instead, build a service that converts image coordinates to a 3D look direction:

```csharp
public interface ILookAtProjector
{
    HeadPose Project(PointF centerNormalized, CameraCalibration calibration);
}
```

Where `HeadPose` might be:

```csharp
public readonly record struct HeadPose(
    double YawRad,
    double PitchRad,
    double RollRad = 0);
```

### First-pass geometry model

Even if you do not have the robot SDK equivalent of `look_at_image`, you can approximate it correctly enough:

1. Convert normalized image coordinates to pixel coordinates.
2. Use camera field of view or intrinsics.
3. Compute angular error from optical center.

Example:

```csharp
double yaw = Math.Atan((px - cx) / fx);
double pitch = -Math.Atan((py - cy) / fy);
```

Where:

* `cx, cy` = principal point
* `fx, fy` = focal lengths in pixels

If you do not know intrinsics yet, estimate from horizontal and vertical field of view:

```csharp
fx = width / (2 * tan(hfov / 2))
fy = height / (2 * tan(vfov / 2))
```

This is vastly better than “multiply X by 0.3 radians.”

---

### 5) `HeadTrackingController`

Consumes the selected target and maintains smoothed desired pose.

```csharp
public sealed class HeadTrackingController
{
    public void UpdateVisualTarget(DetectionResult detection);
    public HeadPose GetDesiredPose(DateTime nowUtc);
}
```

This layer should handle:

* exponential smoothing
* deadband
* max angular velocity
* target timeout
* scan behavior when lost

Rules I would use:

* ignore detections below confidence threshold
* require 2 consecutive hits before locking
* maintain lock for 300 to 700 milliseconds after last detection
* when target is lost, hold briefly, then return to neutral or sweep

---

### 6) `IMotionSink`

Sends actual robot motion commands at a steady rate.

```csharp
public interface IMotionSink
{
    ValueTask SendHeadTargetAsync(HeadPose pose, CancellationToken ct);
}
```

Run this at a high rate, for example **50 to 100 Hertz** if the robot and transport can take it. The detector does not need to run that fast. Your agent’s Python description of a slower vision loop feeding a faster motor loop is exactly the right pattern.

---

## Concrete timing I would use

For a low-powered robot:

* **Camera capture**: 15 to 30 frames per second
* **Detection**: 4 to 10 Hertz initially
* **Motion output**: 50 Hertz
* **Frame resolution for detection**: 320×240 or 416×234
* **Full-resolution preview or logging**: optional, separate path

This is the important point:
you do **not** need 25 Hertz detection if the motion loop is fast and the target signal is smoothed. You just need the detector to be reliable and fresh enough.

---

## Detector choice for your case

### Best first implementation: tiny face detector

I would start with:

* **YoloDotNet + a small face model**
* or **OpenCvSharp + YuNet**

Why not MediaPipe first?

* MediaPipe is very good in practice, but in .NET it is usually a more awkward integration path than YOLO/ONNX or OpenCV.
* For your specific need, the cleanest .NET engineering story is usually ONNX Runtime or OpenCV.

If your robot is very weak, YuNet may be the lightest practical face-first option. If you want the cleanest all-.NET packaging and future flexibility, YoloDotNet is appealing. YoloDotNet specifically supports ONNX Runtime-backed inference and multiple execution providers while staying pure .NET. ([NuGet][2])

---

## How I would mirror the Python behavior

Your agent said the Python app does this:

* detector returns face center
* robot `look_at_image(...)` maps image position to pose
* motion loop adds offsets and continuously sends `set_target`

The .NET equivalent should be:

### Perception result

```csharp
public sealed record TrackingObservation(
    PointF FaceCenterNormalized,
    double Confidence,
    double FaceSizeNormalized,
    DateTime TimestampUtc);
```

### Projection result

```csharp
public sealed record TrackingCommand(
    HeadPose DesiredPose,
    double Confidence,
    DateTime TimestampUtc);
```

### Motion loop

* reads most recent `TrackingCommand`
* smooths against current pose
* rate-limits movement
* sends target every cycle

That is the direct architectural port.

---

## Suggested project structure

```text
Robot.Vision/
  ICameraSource.cs
  VideoFrame.cs
  IHeadDetector.cs
  DetectionResult.cs
  YoloFaceDetector.cs
  YuNetFaceDetector.cs
  ITargetSelector.cs
  StickyTargetSelector.cs

Robot.Tracking/
  CameraCalibration.cs
  ILookAtProjector.cs
  PinholeLookAtProjector.cs
  HeadPose.cs
  HeadTrackingController.cs
  TrackingObservation.cs
  TrackingCommand.cs

Robot.Motion/
  IMotionSink.cs
  ReachyMotionSink.cs
  MotionLoopService.cs

Robot.App/
  TrackingHostedService.cs
  Program.cs
```

If you are already using `BackgroundService`, this maps naturally to hosted services.

---

## The one thing I would not do

Do **not** put detection inside the same loop that talks to the robot motors.

That creates:

* variable loop timing
* jitter under CPU load
* backpressure if inference stalls
* stale motor commands

Instead use a “latest value wins” model between loops.

---

## Recommended control logic

A good low-compute tracker often needs only this:

### Detection phase

* downscale frame
* detect faces
* select best face
* output center

### Tracking phase

* smooth center:

  ```csharp
  smoothedX = alpha * newX + (1 - alpha) * prevX;
  smoothedY = alpha * newY + (1 - alpha) * prevY;
  ```
* convert to yaw/pitch via projector
* clamp to max range
* apply dead zone around center
* emit pose

### Motion phase

* send pose continuously
* if target missing for 0.5 seconds, freeze
* if missing for 2 seconds, return neutral or scan

That will feel much more “alive” than the current approach.

---

## Practical first version

If I were implementing this for you in stages:

### Phase 1

Replace heuristic detector with a real face detector.

* YoloDotNet or YuNet
* detect every 200 to 300 milliseconds
* publish face center

### Phase 2

Replace “scale to yaw/pitch” with camera geometry.

* use estimated field of view if needed
* add calibration later

### Phase 3

Split motion loop from vision loop.

* motion at 50 Hertz
* perception at 5 to 10 Hertz

### Phase 4

Add sticky target selection and loss recovery.

* do not switch targets too easily
* scan when lost

That gets you most of the Python app’s behavioral benefits.

---

## My strongest recommendation

If the goal is **“small, robust, all-.NET, good enough for a robot to face a person”**, I would choose:

**Option A: YoloDotNet + tiny face model + pinhole projector + 50 Hertz motion loop**
Best all-around .NET architecture choice. ([NuGet][2])

If the robot is extremely resource constrained:

**Option B: OpenCvSharp + YuNet + pinhole projector + 50 Hertz motion loop**
Likely lighter on the actual face-detection side, though a bit less “pure .NET.” OpenCV documents YuNet as its lightweight face detection path. ([NuGet][3])

The bigger win, though, is not the model.
It is **porting the Python pipeline shape**:

* continuous camera
* real detector
* image-to-pose geometry
* separate fast motor loop

That is the design I would build.

I can sketch the actual C# interfaces and a minimal `BackgroundService` implementation next, including the latest-frame handoff and the pinhole `look_at_image` math.

[1]: https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime "
        NuGet Gallery
        \| Microsoft.ML.OnnxRuntime 1.24.3
    "
[2]: https://www.nuget.org/packages/YoloDotNet "
        NuGet Gallery
        \| YoloDotNet 4.2.0
    "
[3]: https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime?utm_source=chatgpt.com "Microsoft.ML.OnnxRuntime 1.24.3"
