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
      "program": "${workspaceFolder}/sandbox/DebuggingApp/test.lua",
      "cwd": "${workspaceFolder}/sandbox/DebuggingApp",
      "stopOnEntry": true
    }
  ]
}
```

3) Start debugging. When execution stops, inspect Locals/Globals/Upvalues in the Variables view. Edit Local/Upvalue values inline (supported: `true`/`false`, numbers like `3.14`, strings like `hello` or `"hello"`).

4) Bytecode Viewer (optional):

- Command Palette → “Lua: Show Bytecode” to open the panel
- Or enable auto‑open on stop via the setting `Lua‑CSharp > Bytecode Viewer: Open On Stop`
- In the panel, click an instruction row to toggle an instruction‑level breakpoint (●/○ indicator)

## Settings

- `luaCsharp.bytecodeViewer.openOnStop` (boolean, default `false`)
  - Automatically open the Bytecode Viewer when the debugger stops.

## Commands

- `Lua: Show Bytecode` (`lua-csharp.showBytecode`): open/focus the Bytecode Viewer

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
