# ReachTether

ReachTether contains .NET components and samples for controlling a Reachy Mini robot, including SDK usage, API wrapping, and a voice-enabled prototype app. (WIP)

## Current Repository Status

- `dotNet/ReachTether.Robot`: scaffold console app (`Hello, World!`) targeting `net10.0`
- `dotNet/ReachyMini.Sdk`: reusable Reachy Mini SDK client library
- `dotNet/src/ReachyMini.WebRtc`: WebRTC signaling/session support
- `dotNet/src/ReachyMini.Audio` and `dotNet/src/ReachyMini.Audio.Alsa`: PCM/WAV + ALSA audio pipeline
- `dotNet/samples`: runnable examples (Basic usage, Web API, Chatty voice assistant)

`dotNet/ReachTether.slnx` currently includes only `ReachTether.Robot`. The SDK and sample apps are built from their own project files.

## Requirements

- .NET 9 SDK for SDK/samples (`net9.0` projects)
- .NET 10 SDK for `ReachTether.Robot` (`net10.0`)
- Reachy Mini reachable over network for robot-connected scenarios
- OpenAI API key for `ChattyReachyMini`

## Run Samples

From repo root:

```powershell
dotnet run --project dotNet/samples/BasicUsage/BasicUsage.csproj
dotnet run --project dotNet/samples/WebApiSample/WebApiSample.csproj
dotnet run --project dotNet/samples/ChattyReachyMini/ChattyReachyMini.csproj
```

## ChattyReachyMini Configuration

Create `dotNet/samples/ChattyReachyMini/appsettings.local.json` with at least:

- `ReachyMini:BaseUrl`
- `OpenAI:ApiKey` (or set `OPENAI_API_KEY` env var)

Optional settings include `SignalingUrl`, `RobotId`, `Audio` capture/playback devices, and model/voice selection.

## Build and Deploy ChattyReachyMini to Reachy Mini (linux-arm64)

Publish:

```powershell
dotnet publish dotNet/samples/ChattyReachyMini/ChattyReachyMini.csproj -c Release -r linux-arm64 --self-contained false
```

Copy published files to robot:

```powershell
scp -r "C:/git/reachy-apps/reachtether/dotNet/samples/ChattyReachyMini/bin/Release/net9.0/linux-arm64/publish/." "pollen@reachy-mini.local:/home/pollen/reachdotnet/"
```

Run on robot:

```bash
ssh pollen@reachy-mini.local
cd /home/pollen/reachdotnet/
dotnet ChattyReachyMini.dll
```

For additional deployment notes, see `Docs/deploying.md`.
