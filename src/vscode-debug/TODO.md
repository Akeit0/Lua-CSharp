# Lua-CSharp VS Code Debugger — TODO

This tracker outlines the plan to build a VS Code debugger for the Lua-CSharp runtime.

## Goals

- Breakpoints, stepping, pause/continue for Lua code running on Lua-CSharp
- Call stack, scopes, and variables (locals/upvalues) inspection
- Evaluate expressions in the current stack frame
- Launch and attach modes, basic source path mapping
- Work cross‑platform with minimal dependencies

## References

- Current simple debugger: `sandbox/DebuggingApp` (see `Program.cs` and `SimpleDebugger`)
- VS Code DAP demo: `vscode-mock-debug-custom-runtimes` (symlink at repo root)
- DAP docs: https://microsoft.github.io/debug-adapter-protocol/specification

## High‑Level Architecture

- VS Code Extension (TypeScript) registering a debug type `lua-csharp`.
- Debug Adapter (TypeScript) using `@vscode/debugadapter` + `@vscode/debugprotocol`.
- Runtime Bridge in .NET (C#) hosting `LuaState` and an enhanced `IDebugger` implementation to support step/break/inspect.
  - Launch mode: adapter spawns the .NET host via `dotnet` (stdio IPC).
  - Attach mode: adapter connects over TCP (configurable port) to an already running host embedded in an app.
- Message protocol between Adapter and .NET host:
  - Prefer JSON‑RPC 2.0 over stdio (newline‑delimited) for simplicity.
  - Host raises events (stopped, output, terminated) and responds to commands (setBreakpoints, continue, next, stepIn, stepOut, stackTrace, scopes, variables, evaluate, pause, disconnect).

## Deliverables

1) VS Code extension + adapter under `src/vscode-debug/extension`
- `package.json` (contributes.debuggers, configuration schema, activationEvents)
- `src/extension.ts` (registers debug adapter, resolves configs)
- `src/adapter.ts` (DebugSession implementation)
- Build config: `tsconfig.json`, `esbuild` or `webpack` bundling script
- README with `launch.json` examples

2) .NET Debug Host under `src/Lua.DebugServer`
- Console app that can:
  - Load/execute Lua scripts via `LuaState`
  - Implement an `IDebugger` that supports breakpoints and stepping
  - Serve JSON‑RPC for DAP‑like commands and events
- Optionally: library `Lua.DebugAdapter` to embed in existing apps for attach

3) Sample integration and tests
- Launch config to run `sandbox/DebuggingApp/test.lua`
- Minimal end‑to‑end debug sanity: hit breakpoint, step, read locals

## Work Plan (Phases)

1. Scaffolding
- Create extension skeleton (`lua-csharp` debug type)
- Add DebugAdapter class with stubs for all DAP requests
- Wire simple logging and error handling

2. Debug Host (C#) MVP
- New project `src/Lua.DebugServer` (console)
- Extend `SimpleDebugger` or implement a new `IDebugger` to support stepping and retrieving locals/call stack
- Define JSON‑RPC schema and stdio loop
- Implement commands: initialize, launch, setBreakpoints, continue, next, stepIn, stepOut, stackTrace, scopes, variables, evaluate, pause, disconnect
- Emit events: initialized, stopped, output, terminated, exited

3. Adapter <-> Host wiring
- In launch: spawn `dotnet run --project src/Lua.DebugServer` with args (script, cwd)
- In attach: connect over TCP to host in target process
- Translate DAP requests to host JSON‑RPC and back
- Path/URI normalization and chunk name mapping (prepend `@`)

4. Core debug features
- Breakpoints by file/line (translate to prototypes)
- Single thread model with a single threadId
- Step over/in/out by instrumenting instructions around current PC
- Stack frames from Lua call frames; map to file:line using `Prototype.ChunkName` and `LineInfo`
- Locals/upvalues via stack and `DebugUtility.GetLocalVariableName`
- Evaluate expressions in current frame (basic expression evaluator in Lua)

5. Polishing
- Robust error messages surfaced in VS Code
- Source path mapping settings (e.g., `sourceRoot`, `pathMappings`)
- Stop on uncaught errors (exception breakpoints)
- Docs and examples in `src/vscode-debug/README.md`

## Open Questions / Risks

- Best stepping strategy: patch next instruction vs. run‑to‑location vs. line granularity?
- Handling dynamically generated chunks (`ChunkName` without `@`) and REPL code
- Multi‑threading: current runtime appears single‑threaded per `LuaState`; confirm semantics
- Performance impact of instruction patching at scale; consider lazy patching
- Windows path normalization and case sensitivity for `ChunkName`

## Milestones

- M1: Extension and adapter scaffold builds, activates
- M2: Debug Host responds to setBreakpoints/launch/continue; breakpoint hit
- M3: Stepping and stackTrace working
- M4: Variables/scopes and evaluate working
- M5: Attach mode to external app
- M6: Documentation and sample configs complete

## Next Actions (Immediate)

- Scaffold VS Code extension under `src/vscode-debug/extension`
- Create `src/Lua.DebugServer` project with a minimal stdio JSON‑RPC loop
- Port logic from `sandbox/DebuggingApp/SimpleDebugger` and expose required inspection APIs
- Wire launch config to run `sandbox/DebuggingApp/test.lua` and hit a breakpoint

