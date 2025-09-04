# Lua-CSharp VS Code Debug Extension — Implementation + Roadmap

## Implemented

- Debug adapter (inline)
  - Launch via `dotnet run` of `src/Lua.DebugServer` with stdio transport.
  - Handles: initialize, configurationDone, launch, continue, next, stepIn, stepOut.
  - Threads: single thread reported; basic stackTrace from last stopped location.
  - Variables: Locals and Globals exposed through RPC (`getLocals`, `getGlobals`).

- Bytecode Viewer (webview)
  - Opens on each `stopped` event, highlights current PC, auto-scrolls to it.
  - Renders instruction list (original instruction text), constants, locals, upvalues.
  - Instruction-level breakpoints (ILB):
    - Markers per instruction (● has BP / ○ no BP).
    - Click a row to toggle instruction breakpoint (RPC: `setInstrBreakpoint`).
    - Fetches active ILBs (RPC: `getInstrBreakpoints`).
  - Panel created with `{ enableScripts: true }` and runs fully client-side HTML.

- Server-side support (cross-cut)
  - Bytecode snapshot: `getBytecode` (chunk, pc, instructions, constants, locals, upvalues).
  - ILB management in `MinimalDebugger` with persistence across prototype registration.
  - Normalizes patched debug traps back to original instruction text in snapshots.

- Quality-of-life
  - Output piping from server to VS Code Output window.
  - Basic path normalization when setting file breakpoints (chunk names `@...`).

## Roadmap (Next Steps)

- P1: Commands and UX
  - Add commands: `Lua: Show Bytecode`, `Lua: Toggle Instruction Breakpoint`, `Lua: Refresh Bytecode`.
  - Double-click instruction to open mapped source line; add keyboard navigation (↑/↓, Enter toggles ILB, F to focus find).
  - Persist ILBs in `workspaceState` and restore on session start.

- P1: Breakpoints and Mapping
  - Verify/unverify lifecycle for source BPs; support hit conditions and logpoints.
  - Robust chunk ↔ file path mapping (Windows/WSL/multi-root) with normalization.

- P1: Call stacks and scopes
  - Implement full `stackTrace`, multiple frames, and frame selection in the panel.
  - Update the Bytecode Viewer when the selected frame changes.

- P2: Attach + Transport
  - Add TCP attach mode to server and adapter (`host`, `port`); reconnect and timeouts.
  - Settings for transport selection: stdio (default) vs TCP.

- P2: Data depth and navigation
  - Prototype browsing (parent/children) within the viewer.
  - Distinguish constant types and add operand tooltips (A/B/C/Bx/SBx semantics).

- P2: Packaging and docs
  - Extension README with screenshots and settings; CHANGELOG and versioning.
  - Fix cross-platform build friction for `esbuild` (ensure platform-specific install on CI).

- P3: Performance and stability
  - Throttle panel re-renders during rapid stepping; virtualize long instruction lists.
  - Better errors for missing paused context or unloaded protos.

- P3: Advanced debugging
  - Function breakpoints; `evaluate` request (REPL), hover, watch expressions.

## Notes

- Build: `cd src/vscode-debug/extension && npm ci && npm run build`.
- The server requires .NET; adapter spawns `dotnet run --project src/Lua.DebugServer`.
- Webview script execution is required; enabled via `{ enableScripts: true }`.

---

This document tracks the current state of the VS Code extension and prioritizes next steps for a complete, polished debugging experience.
