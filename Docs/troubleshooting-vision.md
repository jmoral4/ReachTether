# Vision Troubleshooting Notes

## Context

We attempted to add robot-camera snapshot support to `ReachTether` through `ReachyMini.Sdk`.

The target path was the Reachy Mini wireless local camera socket:

- source path: `/tmp/reachymini_camera_socket`
- configured source kind: `unix-socket`
- requested format: `1280x720 @ 30 fps`

The design goal was to match the Python Reachy SDK behavior closely enough that a `.NET` snapshot call could feed the camera tool path later.

Most recent conclusion:

- the remaining gap is likely lifecycle, not a missing "start camera" API call
- Python opens the local GStreamer camera path during initialization and keeps it live
- our next .NET fix should be to warm the pipeline on startup and reuse it for later snapshots

## What We Tried

### 1. One-shot `gst-launch` snapshot pipeline

Initial approach:

- shell out to `gst-launch-1.0`
- read from the camera socket
- encode to JPEG in GStreamer
- write one temp file
- read the temp file back into `.NET`

Example failing shape:

```text
unixfdsrc socket-path=/tmp/reachymini_camera_socket ! queue ! v4l2convert ! video/x-raw,format=BGR,width=1280,height=720,framerate=30/1 ! jpegenc snapshot=true ! multifilesink ...
```

Why we moved away from it:

- `exit code 137` in this context was our own timeout killing the process, not a clean camera failure
- this approach never proved that the socket could actually deliver a frame to a consumer
- it did not match the Python SDK design, which keeps a live pipeline and pulls samples from `appsink`
- `gst-launch` is the wrong shape for "latest frame on demand" behavior

Conclusion:

- `shmsrc` was wrong for this socket
- `unixfdsrc` was the correct source family
- the shell-out snapshot design was still not a good long-term capture strategy

### 2. Long-lived in-process GStreamer pipeline with `appsink`

We refactored `CameraClient` to keep a reusable in-process pipeline and pull samples directly from `appsink`.

Why:

- this matches the official Python SDK architecture much more closely
- it removes temp-file polling and external process management
- it is the correct foundation if camera snapshots later become part of a tool or realtime flow

### 3. Multiple in-process pipeline variants

To avoid guessing wrong about the socket payload, we tried four in-process variants:

1. `unixfdsrc -> queue -> v4l2convert -> ... -> appsink`
2. `unixfdsrc -> queue -> jpegparse -> jpegdec -> ... -> appsink`
3. `shmsrc -> jpeg path -> appsink`
4. `shmsrc -> raw path -> appsink`

Observed results:

- both `shmsrc` variants failed immediately while entering `PLAYING`
- error was:

```text
Failed to read from shmsrc ... Error reading control data: -99
```

Why that matters:

- it strongly confirms that `/tmp/reachymini_camera_socket` is not a GStreamer shared-memory socket
- this aligns with the Reachy daemon code, which exports the camera with `unixfdsink`

### 4. Compare with the local Reachy reference repo

We checked the local reference repo at:

- `C:\git\reachy-apps\reachy_mini`

Relevant findings:

- `src/reachy_mini/media/webrtc_daemon.py` exports camera frames through `unixfdsink`
- `src/reachy_mini/media/camera_gstreamer.py` consumes `/tmp/reachymini_camera_socket` using:

```text
unixfdsrc -> queue -> v4l2convert -> appsink
```

- the Python SDK does not place `jpegenc` in that pipeline
- the Python SDK reads raw BGR bytes from `appsink`
- the Python camera backend also starts a GLib main loop thread when opening the pipeline

Why this changed our implementation:

- we added a GLib main loop thread
- we use `jpegenc` in the GStreamer pipeline so no separately licensed .NET image library is required
- we pull the resulting JPEG bytes directly from `appsink`
- we should also move pipeline startup earlier so the socket consumer is already live before the first snapshot request

### 5. Raw `BGR` conversion + JPEG encoding in GStreamer

Latest implementation shape:

- `unixfdsrc -> queue -> v4l2convert -> video/x-raw,format=BGR,... -> jpegenc -> appsink`
- read JPEG bytes from `appsink`

Why:

- the camera path already depends on GStreamer
- this avoids adding a separately licensed image library for a single encoding operation

## What The Current Results Mean

What we know with good confidence:

- `shmsrc` is wrong for this socket
- the Reachy daemon intends this socket to be consumed through `unixfdsrc`
- the official Python path is `unixfdsrc -> queue -> v4l2convert -> appsink`
- our current `.NET` implementation now follows that architecture much more closely than the original shell-out version

What is still failing:

- the `unixfdsrc` pipelines are timing out waiting for a fresh sample

That suggests one of these remaining issues:

- the robot-side socket exists but is not actually delivering frames to this consumer at probe time
- there is still a runtime negotiation or readiness detail we have not reproduced exactly enough from the Python/GLib stack
- the daemon-side producer may not be active yet when our probe runs

## Why We Stopped Adding More Client-Side Guesswork

At this point the important dead ends are already ruled out:

- not a `shmsrc` socket
- not a `gst-launch` lifecycle problem
- not just "missing appsink behavior"
- not just "needs a GLib loop"
- likely not "needs JPEG in the pipeline"

So continuing to add random client-side pipeline variants is low-value.

The remaining uncertainty is now mostly robot-side or daemon-readiness related, not architectural.

There is still one important lifecycle mismatch to close:

- Python starts the local camera consumer during SDK/media initialization
- our `.NET` client originally created the pipeline lazily on first `CaptureSnapshotAsync()`
- that cold-start timing difference is now the most likely app-side explanation for the timeout

## Recommended Next Debugging Step

Validate the camera socket on the robot side using the Python reference SDK or another known-good local consumer on the same machine and at the same time window as the `.NET` probe, after the `.NET` app has already warmed and held the pipeline open.

The key question is:

- can the reference Python camera path read a frame successfully from `/tmp/reachymini_camera_socket` when our `.NET` probe cannot?

If yes:

- the issue is still in our `.NET` interop or pipeline lifecycle details

If no:

- the issue is robot-side socket readiness or daemon-side camera export, not the `.NET` camera client design

## Summary

The main useful outcomes from this work are:

- we replaced the incorrect one-shot `gst-launch` design with a long-lived in-process `appsink` design
- we confirmed `shmsrc` is not the right transport for `/tmp/reachymini_camera_socket`
- we aligned the `.NET` client much more closely with the official Reachy Python SDK
- we identified startup lifecycle as the next app-side fix: warm the pipeline early and keep it open
- if that still fails, further debugging should focus on validating daemon/socket behavior rather than inventing more capture architectures
