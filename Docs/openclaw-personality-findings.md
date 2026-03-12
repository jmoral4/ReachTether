# OpenClaw Personality Findings

This note summarizes how OpenClaw appears to implement the personality people respond to, based on source review in `C:\git\openclaw`.

## Bottom line

OpenClaw does not seem to rely on one magic hardcoded "system prompt personality block."

Instead, its personality is implemented as a layered system:

1. A small OpenClaw-owned base system prompt establishes the assistant role and runtime behavior.
2. Workspace bootstrap files, especially `SOUL.md` and `IDENTITY.md`, carry most of the persona, tone, and style.
3. Runtime-specific prompt fragments add situational behavior for group chats, session resets, channels, and tool/runtime constraints.
4. Plugins can prepend or append system-context, with guardrails around prompt injection.

That architecture makes the personality feel persistent and configurable without baking everything into one monolithic prompt string.

## Core implementation

The base prompt is built in [C:\git\openclaw\src\agents\system-prompt.ts](C:\git\openclaw\src\agents\system-prompt.ts). The root identity line is:

- `You are a personal assistant running inside OpenClaw.`

From there, OpenClaw assembles structured sections for tools, safety, workspace, docs, sandbox, date/time, messaging, reactions, heartbeats, and injected context.

Two details matter a lot for personality:

- Under `# Project Context`, OpenClaw injects workspace files directly into the prompt.
- If `SOUL.md` is present, the prompt explicitly says to embody its persona and tone and avoid stiff, generic replies.

Relevant implementation/docs:

- [C:\git\openclaw\src\agents\system-prompt.ts](C:\git\openclaw\src\agents\system-prompt.ts)
- [C:\git\openclaw\docs\concepts\system-prompt.md](C:\git\openclaw\docs\concepts\system-prompt.md)

## Where the personality really lives

### `SOUL.md`

This appears to be the main persona file. The default template in [C:\git\openclaw\docs\reference\templates\SOUL.md](C:\git\openclaw\docs\reference\templates\SOUL.md) includes guidance like:

- be genuinely helpful, not performatively helpful
- have opinions
- be resourceful before asking
- avoid corporate/drone/sycophant behavior
- be concise when needed, thorough when it matters

This is a strong clue that OpenClaw's likable personality is intentionally implemented through a durable persona document, not just by model choice.

### `IDENTITY.md`

`IDENTITY.md` is parsed by [C:\git\openclaw\src\agents\identity-file.ts](C:\git\openclaw\src\agents\identity-file.ts). It supports fields like:

- `name`
- `emoji`
- `theme`
- `creature`
- `vibe`
- `avatar`

Those values are then used in config/UI/message-prefix behavior; see [C:\git\openclaw\src\agents\identity.ts](C:\git\openclaw\src\agents\identity.ts) and [C:\git\openclaw\docs\cli\agents.md](C:\git\openclaw\docs\cli\agents.md).

`IDENTITY.md` is less about detailed conversational style than `SOUL.md`, but it helps give the assistant a named identity and consistent presentation.

### `AGENTS.md`

The default template in [C:\git\openclaw\docs\reference\templates\AGENTS.md](C:\git\openclaw\docs\reference\templates\AGENTS.md) reinforces the personality by telling the agent to:

- read `SOUL.md` every session
- participate in group chats like a human
- avoid dominating conversation
- react naturally
- stay silent when it would interrupt the vibe

So the personality is reinforced both by direct persona text and by behavioral operating instructions.

## Runtime prompt layering

OpenClaw adds situation-specific prompt material at runtime rather than trying to encode everything in one static prompt.

### Group and channel overlays

In [C:\git\openclaw\src\auto-reply\reply\get-reply-run.ts](C:\git\openclaw\src\auto-reply\reply\get-reply-run.ts), `extraSystemPrompt` is assembled from:

- inbound meta prompt
- group chat context
- group intro
- `GroupSystemPrompt`

Channel-specific group prompts are sourced from channel config. One example is [C:\git\openclaw\src\discord\monitor\inbound-context.ts](C:\git\openclaw\src\discord\monitor\inbound-context.ts), where Discord channel config can contribute `systemPrompt`.

This likely helps OpenClaw feel socially appropriate in shared spaces without changing its core persona.

### New session behavior

[C:\git\openclaw\src\auto-reply\reply\session-reset-prompt.ts](C:\git\openclaw\src\auto-reply\reply\session-reset-prompt.ts) tells the agent on `/new` or `/reset` to:

- execute session startup
- greet the user in its configured persona, if provided
- be itself and use its defined voice, mannerisms, and mood

That is a direct mechanism for making personality show up immediately at session start.

### Plugin system-context

Plugins can inject `prependSystemContext` and `appendSystemContext`; see:

- [C:\git\openclaw\src\plugins\types.ts](C:\git\openclaw\src\plugins\types.ts)
- [C:\git\openclaw\src\agents\pi-embedded-runner\run\attempt.ts](C:\git\openclaw\src\agents\pi-embedded-runner\run\attempt.ts)

OpenClaw also has a policy switch to block prompt-mutating hooks with `allowPromptInjection=false`; see [C:\git\openclaw\src\plugins\registry.ts](C:\git\openclaw\src\plugins\registry.ts).

So personality can be extended by plugins, but the project has explicit infrastructure for controlling that.

## Why it probably works well

A few design choices likely explain why people perceive OpenClaw as having a strong personality:

- The persona is written in natural language files humans can tune, especially `SOUL.md`.
- The system prompt explicitly tells the model to embody that persona and avoid generic assistant voice.
- Session startup and group-chat guidance make the style visible in actual use, not just in theory.
- Identity is persistent across sessions because it lives in workspace files, not only in transient chat history.
- Personality is separated into stable identity (`SOUL.md`, `IDENTITY.md`) and situational overlays (`extraSystemPrompt`, group prompts, plugins).

## Practical takeaways for ReachTether

If we want similar results, the most transferable ideas are:

- Keep the base system prompt short and operational.
- Put personality into a separate durable persona document rather than burying it in code.
- Add an explicit instruction to embody that persona and avoid generic assistant phrasing.
- Separate stable identity from situational behavior.
- Add session-start instructions so the agent consistently "arrives" in character.
- Add channel/group-specific overlays for social context instead of one-size-fits-all prompting.

## Most relevant source files

- [C:\git\openclaw\src\agents\system-prompt.ts](C:\git\openclaw\src\agents\system-prompt.ts)
- [C:\git\openclaw\docs\concepts\system-prompt.md](C:\git\openclaw\docs\concepts\system-prompt.md)
- [C:\git\openclaw\docs\reference\templates\SOUL.md](C:\git\openclaw\docs\reference\templates\SOUL.md)
- [C:\git\openclaw\docs\reference\templates\AGENTS.md](C:\git\openclaw\docs\reference\templates\AGENTS.md)
- [C:\git\openclaw\src\agents\identity-file.ts](C:\git\openclaw\src\agents\identity-file.ts)
- [C:\git\openclaw\src\agents\identity.ts](C:\git\openclaw\src\agents\identity.ts)
- [C:\git\openclaw\src\auto-reply\reply\get-reply-run.ts](C:\git\openclaw\src\auto-reply\reply\get-reply-run.ts)
- [C:\git\openclaw\src\auto-reply\reply\session-reset-prompt.ts](C:\git\openclaw\src\auto-reply\reply\session-reset-prompt.ts)
- [C:\git\openclaw\src\discord\monitor\inbound-context.ts](C:\git\openclaw\src\discord\monitor\inbound-context.ts)
- [C:\git\openclaw\src\plugins\types.ts](C:\git\openclaw\src\plugins\types.ts)
- [C:\git\openclaw\src\plugins\registry.ts](C:\git\openclaw\src\plugins\registry.ts)
