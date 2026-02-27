# ReachyMini.Sdk Samples

This folder contains sample projects demonstrating how to use `ReachyMini.Sdk`.

## Basic Usage

A simple console app showing basic SDK operations:
- Get daemon status
- Wake up robot
- Get robot state
- List apps
- Put robot to sleep

Run:
```bash
cd samples/BasicUsage
dotnet run
```

## Web API Sample

An ASP.NET Core minimal API wrapping `ReachyMini.Sdk`:
- Dependency injection configuration
- Configuration from `appsettings.json`
- REST endpoints
- Swagger docs

Run:
```bash
cd samples/WebApiSample
dotnet run
```

Then open: `http://localhost:5000/swagger`

API endpoints:
- `GET /api/status` - Get daemon status
- `POST /api/robot/wakeup` - Wake up robot
- `POST /api/robot/sleep` - Put robot to sleep
- `GET /api/robot/state` - Get full robot state
- `POST /api/robot/goto` - Move robot to position
- `GET /api/apps` - List installed apps
- `POST /api/daemon/start` - Start daemon
- `POST /api/daemon/stop` - Stop daemon

## Chatty Reachy Mini

A voice-enabled assistant sample using a single OpenAI provider (`openai-dotnet`):
- Voice capture from Reachy WebRTC audio stream
- Speech-to-text via OpenAI transcription models
- Chat via OpenAI chat models
- Text-to-speech via OpenAI speech models
- Reachy expressive antenna movements

Requirements:
- OpenAI API key
- Reachy Mini robot reachable over network
- WebRTC signaling endpoint and media/command channel enabled on Reachy Mini

Run:
```bash
cd samples/ChattyReachyMini
dotnet run
```

See [ChattyReachyMini/README.md](ChattyReachyMini/README.md) for setup details.

## Prerequisites

- .NET 9.0 SDK
- Reachy Mini robot (update `ReachyMini:BaseUrl` as needed)

## Configuration

Update robot URL in:
- `BasicUsage/Program.cs`
- `WebApiSample/appsettings.json` (`ReachyMini:BaseUrl`)
- `ChattyReachyMini/appsettings.local.json` (`ReachyMini:BaseUrl`)
