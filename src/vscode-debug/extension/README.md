# Lua-CSharp VS Code Debugger (Skeleton)

This is a minimal scaffold of a VS Code debug extension for the Lua-CSharp runtime.

What's here:
- Debug type `lua-csharp` registered with default launch config.
- Inline debug adapter that simulates a session and terminates.

Next:
- Implement a real adapter that talks to a C# debug host process.
- Wire requests: setBreakpoints, continue, next, stepIn, stepOut, stackTrace, scopes, variables, evaluate.
- Spawn `dotnet run --project src/Lua.DebugServer` with args and exchange JSON-RPC messages.

Refer to `../TODO.md` for the broader plan.

