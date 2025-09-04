# Lua-CSharp Bytecode Viewer — Implementation Status and Next Steps

## Implemented

- Bytecode snapshot RPC
  - `getBytecode` in `src/Lua.DebugServer/RpcServer.cs` returns:
    - `chunk`: current chunk name (without `@`)
    - `pc`: current instruction index
    - `instructions`: `{ index, line, text }[]` — always original instruction text
    - `constants`: `string[]`
    - `locals`: `{ name, startPc, endPc }[]`
    - `upvalues`: `{ name, isLocal, index }[]`
  - Snapshot logic in `LuaDebugSession.GetBytecodeSnapshot()` normalizes any debug trap `(OpCode)40` back to the original instruction via `MinimalDebugger.TryGetPatchedOriginal(...)`.

- VS Code webview panel (inline adapter)
  - Opens a “Lua Bytecode” webview on each `stopped` event.
  - Renders:
    - Instructions with current PC highlight
    - Constants, Locals, Upvalues sections
  - Auto-scrolls to center the current instruction row on render.
  - Uses `buildHtml()` in `src/vscode-debug/extension/src/adapter.ts`.

- Instruction-level breakpoints (ILB)
  - Debug server RPCs in `RpcServer`:
    - `getInstrBreakpoints { chunk }` → `{ breakpoints: number[] }`
    - `setInstrBreakpoint { chunk, index, enabled }` → `{}`
  - `MinimalDebugger`:
    - `SetInstructionBreakpoint`, `ClearInstructionBreakpoint`, `GetInstructionBreakpoints`
    - `instrPending` stores ILBs to apply when a prototype registers
    - Applies ILBs in `RegisterPrototype`
  - Adapter integration:
    - Webview shows per-instruction markers: ● (has ILB) / ○ (no ILB)
    - Click instruction row to toggle ILB (posts message → RPC → re-render)
    - Webview scripting enabled via `{ enableScripts: true }`

- Stepping and locals/globals
  - Step next/in/out RPCs wired
  - Locals/Globals variables requests implemented and shown in the Variables view

## Usage

- Build extension: `cd src/vscode-debug/extension && npm ci && npm run build`
- Launch debug: use the provided `Lua-CSharp: Launch` configuration
- On a stop:
  - Bytecode panel appears with current PC highlighted
  - Click an instruction row to toggle an instruction-level breakpoint

## Notes

- Instruction text is always the original opcode, never the patched debug trap.
- Chunk name normalization happens on the server (`@` is added where needed).
- Webview requires scripts; this is enabled in the panel creation.

## Next Steps

- UX polish
  - Command: `Lua: Show Bytecode` to manually open/focus the panel
  - Auto-refresh panel when moving between stack frames (future call-stack support)
  - Double-click to open source and navigate to line (if available)
  - Keyboard navigation and find within the panel

- ILB persistence and mapping
  - Persist instruction-level breakpoints across sessions (per file/chunk)
  - Optionally show source-level breakpoints mapped to instruction rows
  - Visual toggle to filter child prototypes or jump between prototypes

- Richer data
  - Show child prototypes with navigation (expand/collapse or dropdown)
  - Render constant types distinctly (strings vs numbers, escaping)
  - Hover tooltips for instruction operands (A/B/C/Bx/SBx semantics)

- Stability and performance
  - Throttle frequent re-renders on rapid stepping
  - Handle large chunks efficiently (virtualized list if needed)
  - Better error messages and graceful fallback when no paused context

- Test coverage
  - Unit tests for RPC serialization (server)
  - Basic adapter tests for message handling (e.g., using vscode-test)

- Future server features
  - Expose current frame and support switching frames in adapter (affects locals, pc, proto)
  - Optional RPC for “list prototypes” to allow browsing other functions in the chunk

---

This document tracks the current implementation and near-term improvements for the Lua-CSharp bytecode viewer and instruction-level breakpoint support.
