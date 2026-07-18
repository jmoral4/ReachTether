# ReachTether Code Staleness and Completeness Audit

Date: 2026-07-18

## Purpose

This document records a read-only audit of the current ReachTether checkout. The goal was to identify stale dependencies and documentation, broken or misleading defaults, incomplete code paths, risky designs, and missing validation or test coverage before a later remediation session.

The audit covered:

- `ReachTether.Robot`
- `ReachTether.Server` and `ReachTether.Server.Tests`
- `ReachTether.Audio` and `ReachTether.Audio.Alsa`
- `ReachTether.WebRtc`
- `ReachyMini.Sdk`
- sample projects, deployment scripts, configuration, and repository documentation

The checkout already contained uncommitted user changes when the audit began. No attempt was made to modify or revert those changes.

## Executive Summary

The server and memory prototype builds and its 12 tests pass, but the default robot runtime should not be treated as production-ready.

The most urgent problems are:

1. The default realtime voice path uses an OpenAI beta interface that has been removed.
2. The server exposes personal memory, tool execution, and camera artifacts without authentication while its development profile binds to all network interfaces.
3. The server ships a native SQLite dependency with a known high-severity vulnerability.
4. The SDK does not dispose HTTP responses, including responses created by the high-frequency motion loop.
5. Default-enabled tools report successful results even though they are only stubs.
6. Default server settings do not match the deployment path.
7. Request and response bodies containing conversation data are logged to plaintext files by default.

There is also substantial accumulated design debt: broken samples, almost no tests around the physical robot runtime, hundreds of build warnings, duplicated orchestration logic, an effectively disconnected WebRTC subsystem, configuration-only feature placeholders, and documentation that describes states of the repository that are no longer true.

## Priority 0: Runtime Blocker

### RT-001: Default realtime voice path uses a removed beta API

Severity: Critical

Remediation status: Implemented in the current checkout; hardware smoke testing remains required before making realtime the operational default.

Implemented remediation:

- Upgraded `OpenAI` from 2.1.0 to 2.12.0 and migrated from `OpenAI.RealtimeConversation` to the package's GA `OpenAI.Realtime` session, command, item, and server-update types.
- Added an `IRealtimeVoiceSession` boundary and a typed SDK adapter so orchestration code is insulated from future SDK shape changes.
- Migrated session audio, transcription, VAD, tools, function outputs, image input, streaming output audio/text, response lifecycle, errors, cancellation, and WebSocket interruption truncation to the GA shapes.
- Removed the project-wide `OPENAI002` suppression. OpenAI 2.12 still marks its GA Realtime SDK namespace as evaluation, so the unavoidable suppression is scoped to the adapter file only.
- Changed checked-in defaults to `VoicePipeline=legacy` with `gpt-5-mini`; the opt-in realtime model is `gpt-realtime-2.1`.
- Added deterministic fake-session and recorded-protocol tests for speech boundaries, response/item and input-item correlation, streaming audio, transcription, tool calls, interruption, adapter serialization, terminal statuses, and session recreation after fatal errors.

Validation completed:

- The official NuGet feed reports `OpenAI` 2.12.0 as the latest stable package, and restore resolved 2.12.0.
- `dotnet build dotNet/ReachTether.slnx -c Release` succeeded; the only warnings were the pre-existing `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 vulnerability warning.
- `dotnet test dotNet/ReachTether.Server.Tests/ReachTether.Server.Tests.csproj -c Release --no-restore` passed 34 test cases, including 22 realtime pipeline cases.
- No live OpenAI request or hardware smoke test was performed; realtime remains opt-in until that check passes.

Original audit evidence:

- `dotNet/ReachTether.Robot/ReachTether.Robot.csproj:8` suppresses `OPENAI002`.
- `dotNet/ReachTether.Robot/ReachTether.Robot.csproj:12` pins `OpenAI` version `2.1.0`.
- `dotNet/ReachTether.Robot/Program.cs:7` imports `OpenAI.RealtimeConversation`.
- `dotNet/ReachTether.Robot/Program.cs:67` creates a `RealtimeConversationClient`.
- `dotNet/ReachTether.Robot/RobotAppOptions.cs:141` selects realtime when the configured chat model contains `realtime` in `auto` mode.
- `dotNet/ReachTether.Robot/appsettings.json:86-94` defaults to `VoicePipeline=auto` and `gpt-realtime-1.5`.
- The installed OpenAI 2.1.0 package changelog says `RealtimeConversationClient` maps to the `/realtime` beta endpoint and is tagged `Experimental("OPENAI002")`.

OpenAI removed the Realtime beta interface on May 12, 2026. The GA API changed session and event shapes, including output audio events. The current default pipeline should therefore be assumed broken until it is migrated and exercised against the live GA API.

References:

- [OpenAI Realtime beta deprecation](https://developers.openai.com/api/docs/deprecations#2025-09-15-realtime-api-beta)
- [OpenAI beta-to-GA migration guide](https://developers.openai.com/api/docs/guides/realtime#beta-to-ga-migration)

Recommended remediation:

1. Introduce an `IRealtimeVoiceSession` boundary before changing event code.
2. Upgrade the OpenAI package from 2.1.0 to a current supported version; NuGet reported 2.12.0 during this audit.
3. Migrate session configuration and event handling to the GA shapes.
4. Remove the `OPENAI002` suppression when the beta client is gone.
5. Replace the raw realtime image command in `Vision/CameraTool.cs` with the current supported image-input surface where possible.
6. Add recorded-event or fake-session tests for speech boundaries, streaming audio, transcription, tool calls, interruption, response completion, and error recovery.
7. Keep `VoicePipeline=legacy` as the safe operational default until the GA path has passed a hardware smoke test.

## Priority 1: Security, Reliability, and Truthfulness

### SEC-001: Server APIs and UI have no authentication or authorization

Severity: High

Evidence:

- `dotNet/ReachTether.Server/Program.cs:74-201` maps session, memory, administrative archive/restore, tool execution, snapshot upload, and artifact download endpoints.
- There is no `AddAuthentication`, `UseAuthentication`, `UseAuthorization`, or `RequireAuthorization` call in the server.
- `dotNet/ReachTether.Server/Properties/launchSettings.json:11` sets `ASPNETCORE_URLS` to `http://0.0.0.0:5057`.

Impact:

Any host that can reach the server can potentially read or alter personal memory, execute remote tools, upload arbitrary snapshot payloads, and retrieve camera artifacts. The launch profile also uses unencrypted HTTP.

Recommended remediation:

- Bind to loopback by default.
- Add explicit authentication before any non-loopback deployment.
- Separate robot-to-server credentials from human admin authentication.
- Add authorization policies for memory administration, tool execution, snapshot upload, and artifact reads.
- Add TLS or terminate TLS at an authenticated local gateway.
- Treat artifacts and memory as private user data.

### SEC-002: Snapshot upload and memory query inputs lack application-level bounds

Severity: High when the server is network-accessible; Medium on loopback only

Evidence:

- `dotNet/ReachTether.Server/Services/FileSnapshotStore.cs:36-47` decodes the entire base64 payload in memory and writes it to disk without checking decoded size, MIME type, extension, quota, or retention.
- `dotNet/ReachTether.Server/Program.cs:121-129` passes caller-provided `topK` directly to the store.
- SQLite queries in `SqliteSessionStore.cs` use the supplied value as `LIMIT $topK`; negative SQLite limits can mean no limit.

Recommended remediation:

- Define maximum request and decoded artifact sizes.
- Validate allowed content types and image signatures.
- Clamp all `topK` values to a small positive range.
- Add per-session and total artifact quotas plus retention cleanup.
- Return validation errors instead of accepting structurally valid but unsafe requests.

### DEP-001: Known vulnerable SQLite native library

Severity: High

Evidence:

- Restore and build report `NU1903` for `SQLitePCLRaw.lib.e_sqlite3 2.1.11`.
- Advisory: [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q).
- `dotNet/ReachTether.Server/ReachTether.Server.csproj:8` pins `Microsoft.Data.Sqlite` to `10.0.0-preview.7.25380.108` even though .NET 10 packages are stable.

Recommended remediation:

- Upgrade `Microsoft.Data.Sqlite` to a stable patched 10.0.x release. NuGet reported 10.0.10 during the audit.
- Re-run `dotnet list dotNet/ReachTether.slnx package --vulnerable --include-transitive`.
- Add vulnerability auditing to CI.

### SDK-001: HTTP responses are not disposed

Severity: High for long-running hardware operation

Evidence:

- `dotNet/ReachyMini.Sdk/Clients/BaseClient.cs:35-90` creates responses for GET, POST, and DELETE calls without disposing them.
- `dotNet/ReachyMini.Sdk/ReachyMiniClient.cs:81` has the same issue in `HealthCheckAsync`.
- `dotNet/ReachTether.Robot/MotionOrchestrator.cs:93-197` can call `Move.SetTargetAsync` repeatedly at a configured 10-100 Hz.

Impact:

The robot can accumulate response/content resources and eventually suffer connection-pool or memory pressure during extended operation. The motion loop makes this more than a theoretical SDK hygiene issue.

Recommended remediation:

- Dispose each `HttpResponseMessage` after fully reading its content.
- Add a fake-handler test that verifies response disposal for success and failure paths.
- Run an extended motion-loop soak test while monitoring connections, handles, and memory.

### TOOL-001: Stub tools are advertised and return false success

Severity: High

Evidence:

- `dotNet/ReachTether.Robot/Program.cs:107-120` registers `scheduler` and `kinect_shot` when remote tools are enabled.
- `dotNet/ReachTether.Robot/appsettings.json:50-60` enables the server and remote tools by default.
- `dotNet/ReachTether.Server/Services/ToolExecutionService.cs:15-18` routes these names to stubs.
- `ToolExecutionService.BuildStub` returns both payload `ok=true` and response `Ok=true` even though its message says the integration is not implemented.

Impact:

The model can tell a user that a reminder was created or a Kinect image was captured when no action occurred.

Recommended remediation:

- Do not register unavailable tools.
- Make capability discovery depend on actual server health and implementation availability.
- Until implemented, return `Ok=false` with a clear unavailable error.
- Add end-to-end contract tests ensuring a successful tool result corresponds to a completed side effect or retrieved artifact.

### OPS-001: Enabled server defaults do not match deployment

Severity: High operational risk

Evidence:

- `dotNet/ReachTether.Robot/appsettings.json:50-60` enables server persistence, uploads, and remote tools against `http://localhost:5057`.
- `scripts/deploy-mac.sh:10` selects only `ReachTether.Robot.csproj` for publication.
- `scripts/deploy-mac.sh:75-80` publishes only the robot runtime.
- The root README does not provide a server startup or deployment path.

Unless the server is intentionally installed on the robot, `localhost` points at the wrong machine. A normal robot deployment therefore advertises unavailable tools and attempts persistence and uploads against a service that was not deployed.

Recommended remediation:

- Disable `Server.Enabled`, `UploadSnapshots`, and remote tools in the checked-in robot defaults.
- Provide an explicit example configuration for an off-device server URL.
- Add server health/capability negotiation before registering remote tools.
- Document and automate server deployment separately.

### PRIV-001: Conversation request and response bodies are logged by default

Severity: High privacy concern

Evidence:

- `dotNet/ReachTether.Robot/appsettings.json:46-48` enables `LogResponsesApiBodies` with up to 12,000 characters per body.
- `dotNet/ReachTether.Robot/appsettings.json:63-69` enables file logging at Debug.
- `dotNet/ReachTether.Robot/OpenAiTransport.cs:433-447` logs serialized Responses API request and response bodies.

These bodies can contain user speech transcripts, assistant replies, system instructions, tool results, and retrieved memory. Logs are plaintext files in the configured directory.

Recommended remediation:

- Default body logging to false.
- Add structured redaction if diagnostic payload logging is needed.
- Never log API keys, image base64, durable memory contents, or full personal transcripts.
- Document retention and deletion expectations for robot logs.

## Priority 2: Broken and Incomplete Paths

### SAMPLE-001: Both checked-in samples fail to compile

Severity: Medium

Evidence:

- `dotNet/samples/BasicUsage/BasicUsage.csproj:11` references `../../src/ReachyMini.Sdk/ReachyMini.Sdk.csproj`.
- `dotNet/samples/WebApiSample/WebApiSample.csproj:15` contains the same stale reference.
- The SDK now lives at `dotNet/ReachyMini.Sdk`.
- Building `BasicUsage` produced four errors; building `WebApiSample` produced two errors.
- `dotNet/samples/README.md` documents a `ChattyReachyMini` sample that is not present.

Recommended remediation:

- Correct the project references.
- Add both surviving samples to a CI sample-build job or to the solution.
- Remove the missing sample from the README or restore it intentionally.
- Fix the mojibake degree symbol in `BasicUsage/Program.cs`.

### MEM-001: Local embedding provider is a configuration trap, not an implementation

Severity: Medium

Evidence:

- `dotNet/ReachTether.Server/Services/MemoryEmbeddingProviders.cs:13-19` defines `LocalMemoryEmbeddingProviderStub`.
- Setting `Memory:LocalEmbeddings:Enabled=true` makes `IsAvailable` return true, but every embedding call throws `NotSupportedException`.
- `dotNet/ReachTether.Server/appsettings.json:11-14` names local as the preferred provider while disabling it and falling back to OpenAI.

Recommended remediation:

- Remove the enable flag until a real provider exists, or implement the provider.
- Never report an intentionally throwing provider as available.
- Add provider selection and failure/fallback tests covering actual exceptions, not only unavailable flags.

### VISION-001: Ambient context and face tracking are configuration-only placeholders

Severity: Medium

Evidence:

- `RobotAppOptions` parses `AmbientContextEnabled`, `AmbientContextIntervalSeconds`, `FaceTrackingEnabled`, and `FaceTrackingHz`.
- No runtime service consumes those values.
- The checked-in configuration presents the flags as if they were supported features.

Recommended remediation:

- Remove the flags from active configuration until implementations exist, or clearly prefix/document them as unsupported experimental settings.
- When implemented, add hosted-service lifecycle, resource limits, cancellation, and hardware verification.

### PERSONA-001: Personality instructions advertise nonexistent capabilities

Severity: Medium

Evidence:

- `dotNet/ReachTether.Robot/personalities.json:126` tells the `example` personality it can use a `sweep_look` tool.
- No such tool is registered.
- Other personalities instruct the model to enable or disable head tracking, but no corresponding tool exists.
- The current `ToolRouter` exposes all enabled tools globally; there is no per-personality allowlist validation.

Recommended remediation:

- Validate all tool names referenced by personalities at catalog load time.
- Introduce explicit per-personality tool allowlists.
- Remove unsupported capability claims from active prompts.

### CONFIG-001: Robot configuration discards normal environment and command-line overrides

Severity: Medium

Evidence:

- `dotNet/ReachTether.Robot/Program.cs:17` calls `config.Sources.Clear()`.
- It then adds only `appsettings.json` and `appsettings.local.json`.
- `OPENAI_API_KEY` still works because it is read directly from the environment, but normal host environment variables and command-line configuration no longer apply to other settings.

Impact:

Container, service, and deployment environments cannot reliably override settings such as endpoints, audio devices, or feature flags using standard .NET configuration conventions.

Recommended remediation:

- Preserve the default host configuration sources, or explicitly re-add environment variables and command-line arguments after the JSON sources.
- Document precedence and use a prefix for robot-specific environment variables if needed.

## Priority 3: Structural and Maintenance Debt

### TEST-001: Critical robot paths have almost no automated coverage

Severity: High maintenance risk

The 12 passing tests concentrate on server SQLite schema/session/memory behavior, embedding selection, and prompt construction. There are no dedicated tests for:

- realtime event translation or lifecycle
- legacy turn orchestration
- audio capture/playback and cancellation
- motion composition and robot command pacing
- WebRTC signaling/session behavior
- SDK HTTP success/error/disposal behavior
- camera capture and warmup behavior
- server authentication or endpoint authorization
- remote tool contracts and stub truthfulness

`AGENTS.md:23` is itself stale and says no test project exists.

Recommended remediation:

- Add focused unit tests around pure state machines and handlers first.
- Add fake OpenAI/session transports and fake Reachy HTTP handlers.
- Add terminating integration tests for server endpoints.
- Keep hardware-only tests clearly separated and opt-in.

### BUILD-001: Rebuild warning count is too high to be actionable

Severity: Medium

Validation result:

- A forced Release rebuild completed with 0 errors and 327 warnings.
- Approximately 324 warnings were `CS1591` missing XML comments, multiplied across SDK target frameworks.
- One `CS0618` warning identifies obsolete SIPSorcery audio resampling usage.
- Two `NU1903` warnings identify the SQLite vulnerability.

The volume of documentation warnings hides meaningful compatibility and security warnings.

Recommended remediation:

- Either document public SDK members or deliberately suppress `CS1591` at the SDK project level with an explicit rationale.
- Fix the obsolete WebRTC resampling call.
- Enable a controlled warnings-as-errors policy after the baseline is clean.
- Add CI that performs a non-incremental build so warnings are not hidden by cached outputs.

### ARCH-001: Legacy and realtime orchestrators duplicate application lifecycle

Severity: Medium to High

Evidence:

- `InteractionOrchestrator.cs` is approximately 600 lines.
- `RealtimeInteractionOrchestrator.cs` is approximately 980 lines.
- Both duplicate server hydration/persistence, personality switching, robot wake/setup, audio lifecycle, camera startup probing, shutdown parsing, sleep cleanup, and console presentation.

This makes every lifecycle fix a two-path change and increases the chance that legacy and realtime behavior diverge.

Recommended remediation:

- Extract shared robot lifecycle and conversation-turn coordination.
- Keep legacy and realtime implementations behind transport-specific interfaces.
- Move shutdown intent parsing, session persistence, tool routing, and personality switching into shared tested services.
- Replace mixed `Console.WriteLine` and `ILogger` usage with one structured logging path plus a deliberate operator-facing console layer.

### ARCH-002: WebRTC is disconnected but still distorts project boundaries

Severity: Medium

Evidence:

- No active runtime code constructs `ReachyWebRtcSession`.
- `ReachTether.Robot` still references `ReachTether.WebRtc`.
- `ReachTether.Audio.Alsa` references `ReachTether.WebRtc` to implement the WebRTC-owned `IReachySession` abstraction.
- `LocalAudioSession.SendCommandAsync` returns an empty JSON object and performs no command.
- NuGet reported SIPSorcery `8.0.22` while `10.0.12` is available.

Recommended remediation:

- Decide explicitly whether WebRTC remains a supported transport.
- If not, remove it from the active runtime and solution after preserving any useful design notes.
- If yes, add composition/registration and integration tests before upgrading it.
- Move shared audio/session contracts into a lower-level transport-neutral project; ALSA should not depend on WebRTC.
- Split audio streaming from robot command capability instead of forcing a no-op implementation.

### SDK-002: `ThrowOnError=false` violates the SDK's nullable contract

Severity: Medium

Evidence:

- `BaseClient.HandleResponseAsync<TResponse>` returns `default!` for any failed response when `ThrowOnError` is false.

For reference types this silently returns null through a non-nullable `Task<TResponse>`. For value types it returns a value indistinguishable from a legitimate zero/default response.

Recommended remediation:

- Prefer a typed result such as `ReachyResult<T>` or always throw a documented SDK exception.
- Do not use null-forgiving syntax to conceal error-state nullability.

### SERVER-001: Turn promotion uses untracked fire-and-forget work

Severity: Medium

Evidence:

- `dotNet/ReachTether.Server/Program.cs:208-228` starts a new `Task.Run` for persisted-turn promotion and does not track or queue it.

Impact:

Bursts of turns can create unbounded concurrent extraction/embedding calls and SQLite writes. Work can be lost during shutdown because the host does not await outstanding promotions.

Recommended remediation:

- Use a bounded channel and a supervised `BackgroundService`.
- Define retry, deduplication, shutdown drain, and failure persistence behavior.

### SERVER-002: Snapshot manifest persistence is not serialized

Severity: Medium

Evidence:

- `FileSnapshotStore` protects its in-memory list with a lock.
- `PersistManifestAsync` snapshots the list under the lock but performs `File.Create` and serialization after releasing it.
- Concurrent uploads can therefore open and rewrite the same manifest simultaneously.
- The manifest stores absolute file paths, reducing portability when the server data directory is moved.

Recommended remediation:

- Serialize manifest writes with a semaphore.
- Write to a temporary file and atomically replace the prior manifest.
- Store paths relative to the configured storage root.
- Consider making SQLite the artifact metadata source of truth instead of maintaining a second JSON index.

## Documentation Staleness

### DOC-001: Root documentation understates prerequisites and omits active projects

Evidence:

- `Readme.md:40` says .NET SDK 9.0+ is sufficient.
- `ReachTether.Server` and `ReachTether.Server.Tests` target .NET 10.
- The README project inventory omits both server projects while describing the solution as the active projects listed above it.
- There is no `global.json` to pin or document the expected SDK.

Recommended remediation:

- State the actual .NET 10 SDK requirement for the full solution.
- Add the server and server tests to the project overview.
- Add a `global.json` if reproducible SDK selection matters.
- Document robot-only versus full-solution build requirements.

### DOC-002: Research and planning documents describe obsolete repository states

Examples:

- `Docs/architecture-review-and-entity-roadmap-2026-07.md:45` says the `ReachTether.Server` client has zero consumers, but `Program.cs` now registers a typed `IReachTetherServerClient` that is used by session coordination, artifacts, and remote tools.
- `Docs/claudeopus.v.1.1.suggestions.md` says no server project exists.
- `Docs/v1.1-implementation-plan.md` describes the server client as unused.
- `dotNet/samples/README.md` documents a missing sample.

Recommended remediation:

- Add a status banner to historical research documents.
- Maintain one current-state architecture document and one current remediation backlog.
- Avoid presenting model-generated suggestion documents as verified current-state reviews.
- Delete or archive superseded documents after preserving decisions that still matter.

## Dependency Snapshot

The live NuGet outdated check on 2026-07-18 reported these notable direct updates:

| Project | Package | Resolved | Reported latest |
| --- | --- | ---: | ---: |
| ReachTether.Robot | OpenAI | 2.1.0 | 2.12.0 |
| ReachTether.WebRtc | SIPSorcery | 8.0.22 | 10.0.12 |
| ReachTether.Server | Microsoft.Data.Sqlite | 10.0.0-preview.7 | 10.0.10 |
| ReachTether.Server.Tests | Microsoft.NET.Test.Sdk | 17.14.1 | 18.8.1 |
| ReachTether.Server.Tests | xunit | 2.9.0 | 2.9.3 |
| ReachTether.Server.Tests | xunit.runner.visualstudio | 2.8.2 | 3.1.5 |
| ReachyMini.Sdk | SixLabors.ImageSharp | 3.1.11 | 4.0.0 |

Major-version upgrades should not be applied mechanically. The OpenAI and SIPSorcery upgrades in particular require API migration and targeted behavioral tests.

## Validation Results

Commands and results:

```text
dotnet build dotNet/ReachTether.slnx -c Release
Result: succeeded; incremental build initially showed only vulnerability warnings.

dotnet build dotNet/ReachTether.slnx -c Release --no-restore -t:Rebuild
Result: succeeded with 0 errors and 327 warnings.

dotnet test dotNet/ReachTether.Server.Tests/ReachTether.Server.Tests.csproj -c Release --no-build
Result: 12 passed, 0 failed, 0 skipped.

dotnet build dotNet/samples/BasicUsage/BasicUsage.csproj -c Release
Result: failed with 4 errors due to the missing SDK project reference and resulting namespaces.

dotnet build dotNet/samples/WebApiSample/WebApiSample.csproj -c Release
Result: failed with 2 errors due to the missing SDK project reference and resulting namespaces.

dotnet list dotNet/ReachTether.slnx package --outdated --include-transitive
Result: confirmed the dependency versions summarized above.
```

No hardware smoke test or live OpenAI request was performed. Conclusions about hardware behavior are code- and configuration-backed, but must still be verified on the robot after remediation.

## Recommended Remediation Order

### Session 1: Make the checked-in defaults honest and safe

1. Disable the realtime pipeline by default or migrate it immediately.
2. Disable server integration and remote tools by default.
3. Stop advertising stub and nonexistent tools.
4. Disable model body logging by default.
5. Bind the server to loopback and document that it is unauthenticated until SEC-001 is fixed.
6. Upgrade patched SQLite dependencies.

### Session 2: Stabilize resource and build hygiene

1. Dispose SDK HTTP responses.
2. Add SDK HTTP behavior/disposal tests.
3. Repair the samples and sample documentation.
4. Reduce the warning baseline and fix the obsolete WebRTC call.
5. Add CI for restore, rebuild, tests, sample builds, and vulnerable-package checks.

### Session 3: Migrate the voice runtime

1. Introduce provider/session interfaces.
2. Upgrade the OpenAI SDK.
3. Migrate beta realtime events and session configuration to GA.
4. Add deterministic event-stream tests.
5. Run a terminating hardware smoke test covering speech, interruption, a camera tool call, and shutdown.

### Session 4: Secure and operationalize the server

1. Add robot and admin authentication/authorization.
2. Add request validation, quotas, and artifact retention.
3. Replace fire-and-forget promotion with a bounded background queue.
4. Make artifact metadata persistence transactional and portable.
5. Add deployment documentation and health/capability negotiation.

### Session 5: Reduce structural debt

1. Extract shared orchestration lifecycle.
2. Decide whether WebRTC is active or removable.
3. Move shared contracts out of the WebRTC project.
4. Implement or remove placeholder configuration.
5. Add personality/tool validation and allowlists.
6. Consolidate current architecture documentation and mark older research as historical.

## Exit Criteria for a Trustworthy Baseline

The repository should not be considered remediated until:

- the checked-in default voice path uses a supported API;
- the server is loopback-only or authenticated and authorized;
- no known high-severity dependency vulnerability is reported;
- default-enabled tools correspond to real, tested capabilities;
- SDK responses are disposed and the motion path passes a soak test;
- request/response body logging is opt-in and redacted;
- the full solution, tests, and shipped samples build in CI;
- robot/audio/realtime critical paths have deterministic tests;
- documentation accurately describes the current projects, prerequisites, defaults, and deployment topology.
