# Lua‑CSharp VS Code Debugger

Debug Lua scripts running on the Lua‑CSharp runtime. Includes a Bytecode Viewer, instruction‑level breakpoints, and runtime locals/upvalues inspection and editing.

## Features

- Launch Lua‑CSharp debug host (`dotnet run --project src/Lua.DebugServer`) using stdio transport
- Source breakpoints, continue, step next/in/out
- Stack trace (minimal single frame for now)
- Variables view:
  - Locals and Globals
  - Upvalues (runtime values for current closure)
  - Edit Locals and Upvalues inline (supports boolean, number, string)
- Bytecode Viewer (optional panel)
  - Shows instructions with current PC highlight; auto‑scrolls to current
  - Displays constants, locals (proto), and upvalues (proto metadata)
  - Instruction‑level breakpoints (ILB): click an instruction row to toggle
  - Always shows original instruction text (patched traps are normalized on server)

## Requirements

- .NET SDK (for `dotnet run`)
- Node.js (to build the extension)

## Build

```
cd src/vscode-debug/extension
npm ci
npm run build
```

If you see an esbuild platform mismatch (e.g. Windows node_modules on WSL), remove `node_modules` and run `npm ci` on your current platform.

## Usage

1) Open this repo in VS Code.

2) Configure a launch (defaults are provided):

```jsonc
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Lua-CSharp: Launch",
      "type": "lua-csharp",
      "request": "launch",
      "program": "${workspaceFolder}/test.lua",
      "cwd": "${workspaceFolder}",
      "stopOnEntry": true
    }
  ]
}
```

3) Start debugging. When execution stops, inspect Locals/Globals/Upvalues in the Variables view. Edit Local/Upvalue values inline (supported: `true`/`false`, numbers like `3.14`, strings like `hello` or `"hello"`).

4) Bytecode Viewer:

- Command Palette → “Lua: Bytecode Viewer” toggles the panel at any time. If no debug session is active, it toggles a placeholder view.
- When a Lua‑CSharp debug session starts, if the placeholder is already open it will switch to the live viewer. If the panel is not open, it will not auto‑open on start.
- To also pop open on every stop (when already live), enable `Lua‑CSharp > Bytecode Viewer: Open On Stop`.
- When the debug session ends, the live viewer turns back into a placeholder and stays open.
- In the panel, click an instruction row to toggle an instruction‑level breakpoint (●/○ indicator)

### Status Bar

- A status bar button “Bytecode” triggers the same toggle behavior as the command.

### Debugger Options

- Command Palette → “Lua: Debugger Options” opens a picker for Step Over granularity:
  - Line: step over per source line
  - Instruction: step over per bytecode instruction
  Requires an active Lua‑CSharp debug session.

## Settings

- `luaCsharp.bytecodeViewer.openOnStop` (boolean, default `false`)
  - Automatically open the Bytecode Viewer when the debugger stops.

## Commands

- `Lua: Bytecode Viewer` (`lua-csharp.showBytecode`): toggle the viewer. With an active debug session it toggles the live viewer; without a session it toggles a placeholder.
- `Lua: Debugger Options` (`lua-csharp.debugOptions`): configure Step Over granularity.

## Notes & Limitations

- Current stackTrace is minimal (single frame from last stop). Call stack and frame selection are planned.
- Instruction‑level breakpoints are per chunk and persist while the session is active; they are reapplied when prototypes register.
- The server normalizes any patched debug traps back to original instructions in snapshots.

## Troubleshooting

- Esbuild platform mismatch: remove `node_modules` and run `npm ci` on the current platform.
- Ensure `.NET SDK` is installed and accessible as `dotnet`.
- If the Bytecode Viewer doesn’t react to clicks, ensure scripts are enabled (they are by default in this extension).

## Roadmap

See `../ROADMAP.md` for prioritized next steps: commands & keyboard UX, robust path mapping, full call stacks, TCP attach, richer viewer data, packaging, and tests.
