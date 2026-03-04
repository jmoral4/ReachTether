# Personality Tool Access Parity (Reference: `reachy_mini_conversation_app`, Target: `ReachTether`)

## Goal Clarification
- The goal is to implement tool-use parity inside `reachtether/dotNet/ReachTether.Robot`.
- `reachy_mini_conversation_app` is reference behavior, not a migration target in this task.
- Non-goal: moving ReachTether personalities into Python profile files.

## Current ReachTether Reality
- In `ReachTether.Robot`, `personalities.json` currently carries `id`, `displayName`, `switchPhrases`, and `instructions`; tool access is prompt text only, not an enforced allowlist.
  - `reachtether/dotNet/ReachTether.Robot/personalities.json`
  - `reachtether/dotNet/ReachTether.Robot/PersonalityCatalog.cs`
- In chat mode, model tool-call requests are recognized but not executed yet (`"tool execution is not enabled yet"`).
  - `reachtether/dotNet/ReachTether.Robot/InteractionOrchestrator.cs`
- In realtime mode, session options configure instructions/voice/audio/turn detection but do not register tools.
  - `reachtether/dotNet/ReachTether.Robot/RealtimeInteractionOrchestrator.cs` (`BuildSessionOptions`)

## Reference Behavior in `reachy_mini_conversation_app`
- Per-profile tool allowlist via `profiles/<profile>/tools.txt`, loaded at startup by `tools/core_tools.py`.
- Realtime session updates include tool definitions (`tools: get_tool_specs()`).
- Runtime tool calls execute on `response.function_call_arguments.done`; outputs are posted via `function_call_output`.
  - `reachy_mini_conversation_app/src/reachy_mini_conversation_app/tools/core_tools.py`
  - `reachy_mini_conversation_app/src/reachy_mini_conversation_app/openai_realtime.py`

## ReachTether Implementation Path
1. Add per-personality allowlists:
   - Extend `personalities.json` with `tools: []`.
   - Extend `PersonalityDefinition` and catalog loading to parse and validate allowed tools.
2. Add a tool registry and dispatcher:
   - Define tool specs (name/description/JSON schema).
   - Implement runtime executors with allowlist enforcement.
3. Wire tools into realtime:
   - Include tool definitions in `BuildSessionOptions`.
   - On personality switch, reconfigure both instructions and tool set.
   - Handle function-call events, execute tool, send `function_call_output`, and continue response flow.
4. Wire tools into chat:
   - Pass tool definitions into `CompleteChatAsync(...)`.
   - Execute returned tool calls and continue model loop until final assistant text.
5. Add guardrails and observability:
   - Structured errors for unknown/disallowed tools.
   - Timeout and exception handling around tool execution.
   - Logging for tool name, args, result status, and latency.

## Done Criteria
- Personality switch changes both instructions and effective tool access.
- Disallowed tools are rejected even if requested by the model.
- Realtime and chat paths execute allowed tools end-to-end.
- No runtime dependency on Python profile/tool files.
