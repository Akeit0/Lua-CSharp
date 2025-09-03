<div align="right">Lua-CSharp VS Code Debugger</div>

# Execution Model

This document describes the single-threaded execution and debug control model for the Lua-CSharp debugger, and how the debug adapter, RPC server, and VM thread coordinate. It focuses on making step/continue deterministic and thread-safe.

## Core Principles

- Single owner of execution: Only the Lua VM thread (ExecuteAsync/HandleDebugBreak) mutates bytecode, sets/clears step traps, and resumes.
- Command queue + gate: The RPC thread never patches code. It enqueues a requested action (continue/next/…) and signals the VM thread to act.
- Blocking stop loop: On a debug trap, the VM thread captures state, emits a stopped event, then blocks until an action is requested.
- Breakpoint diffs on stop: setBreakpoints only updates desired state; actual patching happens on RegisterPrototype (VM thread) and at the next stop (VM thread) by applying diffs.
- Ordered I/O: RPC writes to stdout are synchronous and serialized to avoid interleaved DAP messages.

## Components

- VS Code adapter: Translates DAP requests to RPC commands; displays stopped state, stack, variables.
- RPC server: Stdio loop parsing JSON-RPC, queues actions, and emits ordered events/responses.
- DebugController (shared state):
  - Holds requested action, pending breakpoints map, and a resume gate (e.g., ManualResetEventSlim).
  - API: `Request(action)`, `UpdateBreakpoints(source, lines)`, `WaitForAction()`, `SnapshotPendingBreakpoints()`.
- MinimalDebugger (VM-threaded): Implements `IDebugger.HandleDebugBreak`. Captures locals, applies breakpoint diffs and step traps, publishes `stopped`, then blocks on the controller for the next action, cleans up step traps, and resumes.

## Architecture (Mermaid)

```mermaid
flowchart LR
  subgraph VSCode[VS Code UI]
    DAP[Debug UI]
  end

  subgraph Adapter[VS Code Adapter]
    ADPT[LoggingDebugSession]
  end

  subgraph Host[Lua Debug Host]
    RPC[RPC Server stdio]
    CTRL[DebugController action + breakpoints + gate]
    DBG[MinimalDebugger IDebugger]
    VM[Lua VM Thread ExecuteAsync]
  end

  DAP -- DAP JSON --> ADPT
  ADPT -- JSON-RPC over stdio --> RPC

  RPC <-- stdout (events) --> ADPT

  RPC -- Request(action/breakpoints) --> CTRL
  VM --> DBG
  DBG <--> CTRL

  DBG -- publish stopped --> RPC
  CTRL -- signal resume --> DBG

  classDef comp fill:#1f6feb,stroke:#0d419d,color:#fff
  classDef host fill:#2da44e,stroke:#116329,color:#fff
  classDef ui fill:#7d8590,stroke:#484f58,color:#fff

  class ADPT comp
  class RPC,CTRL,DBG,VM host
  class DAP ui
```

## Stop/Resume Loop (Mermaid Sequence)

```mermaid
sequenceDiagram
  participant UI as VS Code UI
  participant AD as Adapter
  participant RS as RPC Server
  participant DC as DebugController
  participant MD as MinimalDebugger
  participant VM as Lua VM Thread

  VM->>MD: HandleDebugBreak(proto, pc)
  MD->>MD: Capture locals/frames/globals
  MD->>DC: SnapshotPendingBreakpoints() + Apply diffs (VM thread)
  MD->>RS: event: stopped(reason, file, line)
  RS->>AD: send stopped
  UI->>AD: Continue / Next (DAP)
  AD->>RS: RPC: continue/next
  RS->>DC: Request(action)
  DC-->>MD: WaitForAction() unblocks with action
  alt action == next
    MD->>MD: Patch step traps (VM thread)
  end
  MD->>MD: Cleanup temp traps as needed
  MD-->>VM: return oldInstruction (resume)
```

## Responsibilities

- Adapter (TypeScript)
  - Forwards DAP requests `continue`, `next`, `setBreakpoints`, `disconnect` to RPC.
  - Renders `stopped`, `terminated`, `output`. Fetches locals/globals on demand.

- RPC Server (C#)
  - Single-threaded stdio loop; emits ordered JSON messages with a write lock.
  - Queues actions into `DebugController` and replies to RPC requests.

- DebugController (C#)
  - Thread-safe: holds requested action and desired breakpoints.
  - Provides a blocking `WaitForAction()` for the VM thread during a stop.

- MinimalDebugger (C#)
  - Runs only on the Lua VM thread.
  - Applies breakpoint diffs and step traps.
  - Emits `stopped` and blocks on `WaitForAction()`; on resume, cleans up and returns.

## Step Over (Next)

- Adapter sends `next` over RPC.
- RPC records `RequestedAction = Next` and signals the gate.
- MinimalDebugger (on VM thread), after `WaitForAction()`:
  - Computes candidate instructions in the same prototype whose `LineInfo` differs from the current line.
  - Patches temp debug traps at those candidates to stop at the first actually executed next line.
  - Resumes by returning the original instruction.

## Breakpoints Application

- setBreakpoints updates the desired set in `DebugController` only.
- At the next stop, `MinimalDebugger` reconciles desired vs. actual patches for current and known prototypes.
- `RegisterPrototype` (VM thread) also applies any pending breakpoints for newly seen chunk names.

## Determinism & Safety

- No off-thread patching: RPC never touches bytecode; only the VM thread patches/unpatches.
- Ordered I/O: All events/responses are synchronously written with a lock.
- One stop per trap: `HandleDebugBreak` publishes exactly one `stopped` then blocks until action.
- Cleanup on resume: Temp step traps are removed when resuming to avoid re-triggers.

