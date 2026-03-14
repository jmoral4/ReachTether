# PUBLISH, DEPLOYING, AND TESTING ON REACHY MINI ROBOT

## MAC
### PREREQUISITES
- `.NET SDK` installed and available on `PATH`
- `ssh` / `scp` available on `PATH` (included with macOS by default)
- Optional: `brew install --cask dotnet-sdk` if `dotnet` is not already installed

### PUBLISH
Publish for the robot's Linux ARM64 runtime from the repository root:

```bash
dotnet publish dotNet/ReachTether.Robot/ReachTether.Robot.csproj \
    -c Release -r linux-arm64 --self-contained false \
    -o /Users/jmxdev/git/reachtether/out/reachrobot
```

If you are already in the repo root, a relative output path also works:

```bash
dotnet publish dotNet/ReachTether.Robot/ReachTether.Robot.csproj \
    -c Release -r linux-arm64 --self-contained false \
    -o out/reachrobot
```

### DEPLOY
Deploy published artifacts to Reachy using `scp` into the pre-existing `reachrobot` directory:

```bash
scp -r out/reachrobot/. \
    pollen@reachy-mini.local:/home/pollen/reachrobot/
```

> password: `root` (default)

### TESTING
1. Remote into the robot:

```bash
ssh pollen@reachy-mini.local
```

> password: `root` (default)

2. Navigate to the deployed app directory:

```bash
cd /home/pollen/reachrobot/
```

## NOTES
- The target runtime remains `linux-arm64` even when publishing from macOS.
- `reachy-mini.local` depends on local network name resolution via Bonjour/mDNS. This usually works on macOS without extra setup.
