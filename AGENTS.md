# Repository Guidelines

## Project Structure & Module Organization
`dotNet/` contains the active .NET solution and all code projects. `dotNet/ReachTether.Robot` is the main runtime, `dotNet/ReachTether.Audio` and `dotNet/ReachTether.Audio.Alsa` handle audio, `dotNet/ReachTether.WebRtc` handles signaling/session work, and `dotNet/ReachyMini.Sdk` is the reusable robot SDK. Sample apps live under `dotNet/samples/`. Design notes, deployment docs, and investigation logs live in `Docs/`. Publish output is typically written to `out/`.

## Build, Test, and Development Commands
Run from the repository root unless noted:

```bash
dotnet restore dotNet/ReachTether.slnx
dotnet build dotNet/ReachTether.slnx -c Release
dotnet publish dotNet/ReachTether.Robot/ReachTether.Robot.csproj -c Release -r linux-arm64 --self-contained false -o out/reachrobot
```

In this Codex environment, most `dotnet build`, `restore`, `run`, and `publish` commands should be expected to require elevated execution.

Avoid `dotnet run` as a routine validation step unless the target is a quick sample or a code path that terminates predictably.

## Coding Style & Naming Conventions
Follow `dotNet/ReachyMini.Sdk/.editorconfig`: UTF-8, final newline, spaces not tabs, and 4-space indentation for C#. Keep nullable reference types enabled and prefer explicit, readable public APIs. Use PascalCase for types and public members, camelCase for locals/parameters, and keep filenames aligned with the primary class (`MotionOrchestrator.cs`, `CameraTool.cs`). Place `using` directives outside namespaces and keep `System` imports first.

## Testing Guidelines
There are currently no dedicated test projects in this repository. Until tests are added, validate changes with `dotnet build` and targeted, terminating checks, then note the manual verification path in your PR. When adding tests, place them in a sibling `*.Tests` project under `dotNet/` and use names like `ThingNameTests.cs`.

## Commit & Pull Request Guidelines
Recent commits use short, imperative summaries such as `vision tweaks and logging` and `fixed some bugs in the turn-based image processing path`. Keep commit subjects concise, lowercase is acceptable, and scope each commit to one logical change. PRs should describe the runtime impact, list config changes (`.env`, `appsettings.local.json`, robot endpoint settings), and include logs or screenshots when behavior changes are user-visible.

## Security & Configuration Tips
Do not commit secrets. `OPENAI_API_KEY` belongs in `dotNet/ReachTether.Robot/.env` or your shell environment, not in `appsettings*.json`. Keep machine-specific overrides in `appsettings.local.json`, and verify `ReachyMini:BaseUrl` and ALSA device names before testing against hardware.
