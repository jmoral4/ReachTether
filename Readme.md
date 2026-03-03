# ReachTether

ReachTether is a .NET codebase for building voice-first Reachy Mini robot applications.

This README focuses on the core runtime and libraries and intentionally ignores `/dotNet/samples`.

## What Is In This Repo

- `dotNet/ReachTether.Robot`: main robot runtime (hosted services, VAD, OpenAI conversation, speech playback, motion orchestration)
- `dotNet/ReachTether.Audio`: audio primitives (frames, format helpers, WAV conversion)
- `dotNet/ReachTether.Audio.Alsa`: local ALSA audio session and capture/playback device integration
- `dotNet/ReachTether.WebRtc`: WebRTC session and signaling utilities
- `dotNet/ReachyMini.Sdk`: typed Reachy Mini SDK client library
- `dotNet/ReachTether.slnx`: solution file for the active projects above
- `Docs/`: architecture notes, deployment notes, and design research

## Runtime Overview

`ReachTether.Robot` is a long-running host process that:

1. Connects to Reachy Mini over HTTP (`ReachyMini:BaseUrl`).
2. Connects to local audio devices via ALSA.
3. Captures speech input with VAD.
4. Sends transcription/chat/speech requests to OpenAI.
5. Streams/plays audio responses.
6. Applies motion and talking gestures while interacting.

Voice pipeline mode is controlled by `OpenAI:VoicePipeline`:

- `auto`: choose realtime for realtime-style models, otherwise legacy turn-based flow.
- `realtime`: force realtime pipeline.
- `legacy`: force turn-based STT -> chat -> TTS pipeline.

## Prerequisites

- .NET SDK 9.0+ (main projects target `net9.0`)
- Network access to Reachy Mini daemon endpoint (for example `http://reachy-mini.local:8000`)
- OpenAI API key exposed as `OPENAI_API_KEY`
- Linux environment with ALSA devices for robot audio runtime

Notes:

- `ReachyMini.Sdk` multi-targets `net8.0`, `net9.0`, and `net10.0`.
- `OPENAI_API_KEY` is read from environment or `.env`; it is not read from `appsettings*.json`.

## Build

From repository root:

```bash
dotnet restore dotNet/ReachTether.slnx
dotnet build dotNet/ReachTether.slnx -c Release
```

## Run ReachTether.Robot

From repository root:

1. Create `dotNet/ReachTether.Robot/.env`:

```bash
OPENAI_API_KEY=your_openai_api_key_here
```

2. (Optional but recommended) Create `dotNet/ReachTether.Robot/appsettings.local.json` for local overrides:

```json
{
  "ReachyMini": {
    "BaseUrl": "http://reachy-mini.local:8000"
  },
  "Audio": {
    "CaptureDevice": "reachymini_audio_src",
    "PlaybackDevice": "reachymini_audio_sink",
    "Channels": 2
  },
  "OpenAI": {
    "VoicePipeline": "auto",
    "ChatModel": "gpt-realtime-mini",
    "TranscriptionModel": "gpt-4o-mini-transcribe",
    "SpeechModel": "gpt-4o-mini-tts",
    "SpeechVoice": "alloy",
    "TranscriptionLanguage": "en",
    "Realtime": {
      "Model": "gpt-realtime-mini",
      "ResponseTimeoutMs": 45000,
      "OutputSampleRateHz": 24000
    }
  }
}
```

3. Run:

```bash
dotnet run --project dotNet/ReachTether.Robot/ReachTether.Robot.csproj
```

## Configuration Keys

Primary keys consumed by `ReachTether.Robot`:

| Key | Purpose |
| --- | --- |
| `ReachyMini:BaseUrl` | Reachy daemon base URL |
| `Audio:CaptureDevice` | ALSA capture device name |
| `Audio:PlaybackDevice` | ALSA playback device name |
| `Audio:Channels` | Audio channel count (`1` or `2`) |
| `OpenAI:VoicePipeline` | `auto`, `realtime`, or `legacy` |
| `OpenAI:ChatModel` | Chat/realtime selection model |
| `OpenAI:TranscriptionModel` | STT model for legacy pipeline |
| `OpenAI:SpeechModel` | TTS model |
| `OpenAI:SpeechVoice` | Voice (`alloy`, `echo`, `fable`, `onyx`, `nova`, `shimmer`) |
| `OpenAI:TranscriptionLanguage` | STT language hint |
| `OpenAI:Realtime:*` | Realtime model and timeout/sample-rate controls |
| `VAD:*` | Voice activity detection thresholds/timing |
| `Motion:*` | Gesture loop behavior and safety limits |
| `Personality:CatalogPath` | Personality JSON catalog path |
| `Personality:Default` | Default personality id |

## Publish and Deploy (Reachy Mini linux-arm64)

From repository root:

```bash
dotnet publish dotNet/ReachTether.Robot/ReachTether.Robot.csproj \
  -c Release -r linux-arm64 --self-contained false \
  -o out/reachrobot
```

Copy artifacts to robot:

```bash
scp -r out/reachrobot/. pollen@reachy-mini.local:/home/pollen/reachrobot/
```

Run on robot:

```bash
ssh pollen@reachy-mini.local
cd /home/pollen/reachrobot
dotnet ReachTether.Robot.dll
```

## Troubleshooting

- `OPENAI_API_KEY not found`: ensure `.env` exists in working directory or set env var before launch.
- Reachy connection failure: verify `ReachyMini:BaseUrl` and that daemon is reachable.
- ALSA device errors: confirm capture/playback device names on target host.
- Unexpected pipeline mode: set `OpenAI:VoicePipeline` explicitly to `realtime` or `legacy`.

## Additional Docs

- `Docs/ReachTether-on-reachy-mini.md`
- `Docs/reachtether-evolution-findings.md`
- `Docs/architectural-findings-and-roadmap.md`
- `Docs/realtime-immediate-playback-sketch.md`
- `Docs/deploying.md` (contains older notes; prefer commands in this README for current runtime)
