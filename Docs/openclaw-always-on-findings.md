# How OpenClaw Feels "Always On"

This note summarizes how OpenClaw creates an "always awake" feel, based on source and docs review in `C:\git\openclaw`.

## Bottom line

OpenClaw does not make the model itself continuously conscious.

Instead, it wraps ordinary LLM turns inside a long-running gateway process that:

- stays connected to channels in the background
- keeps persistent session state
- routes inbound messages into the same session over time
- runs periodic heartbeat turns
- injects system events into future turns
- schedules background work with cron

The result is a strong illusion of continuity: the user experiences one assistant that is reachable, remembers context, notices things, and occasionally speaks first.

## The main mechanism: a persistent gateway service

OpenClaw is designed around a background Gateway daemon rather than a one-shot CLI invocation.

The docs are explicit:

- the wizard installs a Gateway daemon so it stays running
- the Gateway is the source of truth for sessions, routing, and channel connections

Relevant sources:

- [C:\git\openclaw\README.md](C:\git\openclaw\README.md)
- [C:\git\openclaw\docs\start\getting-started.md](C:\git\openclaw\docs\start\getting-started.md)
- [C:\git\openclaw\docs\index.md](C:\git\openclaw\docs\index.md)

This matters because "always on" starts with an always-running process that owns the transports. Most LLM apps feel transactional because they launch a request, get a response, and stop. OpenClaw keeps the surrounding system alive.

## Persistent sessions make it feel like one ongoing conversation

OpenClaw persists sessions on disk and reuses them over time.

Key behavior from the docs:

- direct chats collapse into an agent main session by default
- group and channel chats get stable session keys
- transcripts and session metadata are stored under `~/.openclaw/agents/<agentId>/sessions/`
- the gateway, not the client, owns session truth

Relevant sources:

- [C:\git\openclaw\docs\concepts\session.md](C:\git\openclaw\docs\concepts\session.md)
- [C:\git\openclaw\docs\concepts\agent.md](C:\git\openclaw\docs\concepts\agent.md)
- [C:\git\openclaw\docs\concepts\multi-agent.md](C:\git\openclaw\docs\concepts\multi-agent.md)

This is a big part of the effect. The model is still stateless between calls, but OpenClaw keeps feeding it the same session history and workspace context, so the user experiences continuity.

## Background channel ownership removes the "open the app first" feeling

OpenClaw connects to WhatsApp, Telegram, Slack, Discord, Signal, and other channels through the gateway. Incoming messages are handled by the auto-reply pipeline, not by a foreground UI session.

That means:

- the assistant can receive messages at any time
- the assistant can reply on the same channel without the user opening a dedicated app
- direct messages feel like messaging a person, not starting a fresh LLM chat

This is a core reason it feels alive: the interaction happens inside existing communication surfaces.

## Heartbeats create periodic awareness

Heartbeat is one of the most important "always on" features.

OpenClaw runs periodic agent turns in the main session. The docs describe heartbeats as periodic checks where the agent can surface anything that needs attention without spamming the user.

Important details:

- default cadence is periodic, e.g. `30m`
- heartbeat runs share the main session context
- they can read `HEARTBEAT.md`
- if nothing needs attention, the agent returns `HEARTBEAT_OK`
- heartbeat-only acknowledgments are suppressed from delivery

Relevant sources:

- [C:\git\openclaw\docs\gateway\heartbeat.md](C:\git\openclaw\docs\gateway\heartbeat.md)
- [C:\git\openclaw\docs\automation\cron-vs-heartbeat.md](C:\git\openclaw\docs\automation\cron-vs-heartbeat.md)
- [C:\git\openclaw\src\infra\heartbeat-wake.ts](C:\git\openclaw\src\infra\heartbeat-wake.ts)

Architecturally, this is clever: OpenClaw can "think" on a timer, but only bother the user when something actually matters.

## System events let background actions show up in the next turn

OpenClaw has a lightweight system-event queue. Components can enqueue human-readable events tied to a session key, and those events are prefixed into a later prompt.

Examples from the code/docs:

- cron jobs can enqueue system events for the main session
- exec/runtime events can enqueue a summary and request a heartbeat wake
- ACP/subagent progress can enqueue updates and wake the parent session

Relevant sources:

- [C:\git\openclaw\src\infra\system-events.ts](C:\git\openclaw\src\infra\system-events.ts)
- [C:\git\openclaw\src\agents\bash-tools.exec-runtime.ts](C:\git\openclaw\src\agents\bash-tools.exec-runtime.ts)
- [C:\git\openclaw\src\agents\acp-spawn-parent-stream.ts](C:\git\openclaw\src\agents\acp-spawn-parent-stream.ts)

This is another strong contributor to the "awake" feeling. Work can finish elsewhere, then the main assistant can naturally mention it as part of the next heartbeat or follow-up.

## Cron gives it scheduled initiative

OpenClaw includes a built-in scheduler in the gateway. Cron jobs persist across restarts and can either:

- enqueue a system event for the main session and optionally wake heartbeat, or
- run an isolated agent turn in `cron:<jobId>`

Relevant sources:

- [C:\git\openclaw\docs\automation\cron-jobs.md](C:\git\openclaw\docs\automation\cron-jobs.md)
- [C:\git\openclaw\docs\automation\cron-vs-heartbeat.md](C:\git\openclaw\docs\automation\cron-vs-heartbeat.md)
- [C:\git\openclaw\src\cron\service.ts](C:\git\openclaw\src\cron\service.ts)

This gives OpenClaw proactive behavior without pretending the LLM is continuously running. It is scheduled re-entry into the same assistant runtime.

## Queueing and session lanes preserve the illusion under load

OpenClaw serializes auto-reply runs per session key and uses queue modes like `collect`, `followup`, and `steer`.

That helps in two ways:

- only one run touches a session at a time, so the conversation feels coherent
- inbound messages can interrupt or guide an ongoing run in a controlled way

Relevant source:

- [C:\git\openclaw\docs\concepts\queue.md](C:\git\openclaw\docs\concepts\queue.md)

This avoids the "multiple stateless completions racing each other" feeling that many agent systems have.

## Why users perceive it as awake rather than transactional

The "always awake" feeling seems to come from combining several small effects:

1. The gateway is always running.
2. Messages arrive through normal human communication channels.
3. Sessions persist and are reused.
4. Heartbeats let it check in or notice things periodically.
5. Cron and system events let background work surface later.
6. Silent heartbeat acknowledgments hide empty maintenance turns.
7. Session-local queueing makes it feel like one mind, not many disconnected API calls.

None of those individually makes an LLM feel alive. Together, they do.

## Practical takeaways for ReachTether

If we want a similar effect, the most transferable ideas are:

- Use a long-running runtime that owns transports and session state.
- Persist per-user or per-conversation sessions and reuse them by default.
- Add periodic heartbeat turns with suppressed no-op output.
- Give background jobs a way to enqueue future system-context into the same session.
- Treat scheduling and event wakeups as first-class features, not bolt-ons.
- Serialize execution per session so the assistant feels singular and continuous.

## Most relevant source files

- [C:\git\openclaw\README.md](C:\git\openclaw\README.md)
- [C:\git\openclaw\docs\start\getting-started.md](C:\git\openclaw\docs\start\getting-started.md)
- [C:\git\openclaw\docs\concepts\session.md](C:\git\openclaw\docs\concepts\session.md)
- [C:\git\openclaw\docs\concepts\agent.md](C:\git\openclaw\docs\concepts\agent.md)
- [C:\git\openclaw\docs\concepts\queue.md](C:\git\openclaw\docs\concepts\queue.md)
- [C:\git\openclaw\docs\gateway\heartbeat.md](C:\git\openclaw\docs\gateway\heartbeat.md)
- [C:\git\openclaw\docs\automation\cron-vs-heartbeat.md](C:\git\openclaw\docs\automation\cron-vs-heartbeat.md)
- [C:\git\openclaw\docs\automation\cron-jobs.md](C:\git\openclaw\docs\automation\cron-jobs.md)
- [C:\git\openclaw\src\infra\heartbeat-wake.ts](C:\git\openclaw\src\infra\heartbeat-wake.ts)
- [C:\git\openclaw\src\infra\system-events.ts](C:\git\openclaw\src\infra\system-events.ts)
- [C:\git\openclaw\src\cron\service.ts](C:\git\openclaw\src\cron\service.ts)
