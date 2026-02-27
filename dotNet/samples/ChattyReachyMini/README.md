# Chatty Reachy Mini

A voice-enabled Reachy Mini assistant prototype built with one AI provider: `openai-dotnet`.

## What It Does

- Captures voice input from Reachy WebRTC audio stream
- Transcribes speech with OpenAI transcription models
- Generates conversational responses with an OpenAI chat model
- Speaks responses with an OpenAI speech model
- Uses Reachy antenna movement to express listening/thinking/speaking states

## Prerequisites

1. Reachy Mini running and reachable over HTTP
2. OpenAI API key
3. Reachy WebRTC signaling endpoint enabled
4. Reachy WebRTC signaling URL and optional auth token configured

## Configuration

Create `appsettings.local.json` in `samples/ChattyReachyMini`:

```json
{
  "ReachyMini": {
    "BaseUrl": "http://localhost:8080",
    "SignalingUrl": "ws://localhost:9000/ws/signaling",
    "RobotId": "reachy_mini",
    "SignalingAccessToken": ""
  },
  "Audio": {
    "FrameDurationMs": 20,
    "JitterBufferMs": 250
  },
  "OpenAI": {
    "ApiKey": "YOUR_OPENAI_API_KEY",
    "ChatModel": "gpt-4o-mini",
    "TranscriptionModel": "gpt-4o-transcribe",
    "SpeechModel": "gpt-4o-mini-tts",
    "SpeechVoice": "alloy",
    "TranscriptionLanguage": "en",
    "RecordingSeconds": 5
  }
}
```

You can also set `OPENAI_API_KEY` as an environment variable instead of storing the key in local config.

`SpeechVoice` options:
- `alloy`
- `echo`
- `fable`
- `onyx`
- `nova`
- `shimmer`

## Run

```bash
cd samples/ChattyReachyMini
dotnet run
```

## Usage

1. App wakes Reachy Mini.
2. Press ENTER to start recording.
3. Speak to Reachy; robot microphone audio is captured for the configured duration (`RecordingSeconds`).
4. Reachy transcribes, responds, and speaks.
5. Say `goodbye`, `bye`, or `exit` to end.

## Notes

- This is a prototype flow optimized for Raspberry Pi Linux.
- Audio capture/playback uses the Reachy WebRTC session path (no `arecord`/`aplay` dependency).
- Keep secrets in `appsettings.local.json` (git-ignored) or environment variables.
