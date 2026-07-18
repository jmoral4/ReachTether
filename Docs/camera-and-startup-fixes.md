# ReachTether Camera and Robot Startup Fixes

## Status

Implemented on 2026-07-18. The Release solution build and `linux-arm64` publish pass. Deployment and the hardware checks below still need to be completed on the robot.

## 1. Enable motors before waking the robot

ReachTether currently calls `Move.WakeUpAsync()` while the robot can still be in motor mode `disabled`. The daemon accepts the wake move and returns HTTP 200, but the robot does not physically move while torque is disabled.

The failure was reproduced manually:

1. `POST /api/move/play/wake_up` returned a move UUID and completed, but the head remained tucked.
2. `GET /api/motors/status` reported `{"mode":"disabled"}`.
3. After `POST /api/motors/set_mode/enabled`, the same wake command physically woke the robot.

Update both production startup paths:

- `dotNet/ReachTether.Robot/InteractionOrchestrator.cs`
- `dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs`

Required startup order:

1. Confirm that a backend-protected endpoint is available.
2. Call `reachyClient.Motors.SetModeAsync(MotorControlMode.Enabled, ...)`.
3. Verify that motor mode is `enabled`.
4. Call `reachyClient.Move.WakeUpAsync(...)`.
5. Wait for the returned wake move UUID to leave `/api/move/running` instead of relying only on a fixed delay.
6. Continue with audio, camera, neutral pose, and conversation startup.

The application should fail startup with a clear message if enabling the motors or completing the wake move fails.

## 2. Do not constrain the Unix-socket camera feed to 30 FPS

The daemon's physical camera captures at 1280x720 and 30 FPS, but daemon version 1.9.0 caps its local IPC feed at 10 FPS. ReachTether currently requires 30 FPS in the downstream caps:

```text
video/x-raw,format=BGR,width=1280,height=720,framerate=30/1
```

That requirement cannot be negotiated with the daemon's IPC feed and produces:

```text
streaming stopped, reason not-negotiated (-4)
```

Update the Unix-socket pipeline in:

- `dotNet/ReachyMini.Sdk/Clients/CameraClient.cs`

Change its BGR caps to omit framerate:

```text
video/x-raw,format=BGR,width=1280,height=720
```

Keep the requested BGR format and resolution. Allow the IPC producer to determine the delivered frame rate. This matches the Reachy Mini 1.9 `GStreamerCamera`, which intentionally omits a framerate constraint because the daemon may serve IPC frames below the camera capture rate.

The `Vision:Framerate` setting may remain useful for other camera source kinds and diagnostics, but it must not be forced onto the Unix-socket IPC consumer.

## 3. Clean up failed GStreamer pipelines correctly

When camera startup fails, ReachTether releases GStreamer elements while they are still in `PLAYING`, `PAUSED`, or `READY`. This generates critical disposal warnings and can leave resources in an uncertain state.

Update the camera startup failure and disposal paths in `CameraClient.cs` to:

1. Set the complete pipeline state to `GST_STATE_NULL`.
2. Wait for or otherwise confirm the state transition as appropriate.
3. Release the appsink and pipeline references only after the pipeline is null.
4. Preserve the original negotiation or capture exception as the reported failure.

This cleanup issue is secondary to the caps mismatch, but it should be fixed in the same camera change.

## Validation

After implementing the changes:

1. Build the solution in Release configuration.
2. Publish `ReachTether.Robot` for `linux-arm64`.
3. Deploy the contents of `out/reachrobot` to `/home/pollen/reachrobot`.
4. Reboot the robot or explicitly place motor mode in `disabled` before testing startup.
5. Start ReachTether without first using Reachy Mini Control.
6. Confirm that ReachTether enables the motors and the head physically wakes.
7. Confirm that startup waits for the wake move to complete.
8. Confirm that the vision warmup captures at least one frame without `not-negotiated (-4)`.
9. Confirm that no GStreamer disposal warnings occur.
10. Complete one spoken interaction and verify clean sleep/shutdown behavior.

## Confirmed unrelated observations

- The Wireless daemon starts onboard automatically; the desktop control app is not required to own the daemon.
- `backend_status.ready=false` in daemon version 1.9.0 is not a reliable readiness signal. A successful backend-protected request such as `GET /api/motors/status` is the practical readiness check.
- The earlier `Missing motors` error was cleared by a cold power cycle. A subsequent scan found motor IDs 10 through 18, and the daemon successfully initialized every configured motor.
