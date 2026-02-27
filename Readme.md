# ReachTether

ReachTether contains .NET components and samples for controlling a Reachy Mini robot, including SDK usage, API wrapping, and a voice-enabled prototype app. (WIP)

## Current Repository Status

- `dotNet/ReachTether.Robot`: voice-enabled prototype app that chats via OpenAI and speaks through Reachy audio
- `dotNet/ReachyMini.Sdk`: reusable Reachy Mini SDK client library
- `dotNet/src/ReachTether.WebRtc`: WebRTC signaling/session support
- `dotNet/src/ReachTether.Audio` and `dotNet/src/ReachTether.Audio.Alsa`: PCM/WAV + ALSA audio pipeline
- `dotNet/samples`: runnable examples (Basic usage, Web API, Chatty voice assistant)

`dotNet/ReachTether.slnx` includes `ReachTether.Robot` and its referenced libraries.

## Requirements

- .NET 9 SDK
- Reachy Mini reachable over network for robot-connected scenarios
- OpenAI API key for `ReachTether.Robot`

## Run Samples

From repo root:

```powershell
dotnet run --project dotNet/samples/BasicUsage/BasicUsage.csproj
dotnet run --project dotNet/samples/WebApiSample/WebApiSample.csproj
dotnet run --project dotNet/samples/ChattyReachyMini/ChattyReachyMini.csproj
```

## Run ReachTether.Robot

Create a `.env` file in `dotNet/ReachTether.Robot` (or in the runtime working directory):

```bash
OPENAI_API_KEY=your_openai_api_key_here
```

Then run:

```powershell
dotnet run --project dotNet/ReachTether.Robot/ReachTether.Robot.csproj
```

`ReachTether.Robot` does not read the OpenAI API key from `appsettings*.json`; it is loaded from `.env` / environment only.

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
