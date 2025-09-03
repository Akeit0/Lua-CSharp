using Lua;
using Lua.Debugging;
using Lua.Runtime;

class MinimalDebugger : IDebugger
{
    readonly Dictionary<(Prototype, int), Instruction> breakpoints = new();
    readonly Dictionary<string, List<int>> pending = new();
    readonly Dictionary<string, Prototype> protos = new();
    KeyValuePair<(Prototype proto, int index),Instruction>? stepBreak;
    readonly object sync = new();

    public void RegisterPrototype(Prototype proto)
    {
        if (!proto.ChunkName.StartsWith('@')) return;
        lock (sync)
        {
            protos[proto.ChunkName] = proto;
        }
        // Apply desired user BPs for this chunk (VM thread, non-destructive peek)
        var session = LuaDebugSession.Current;
        if (session != null)
        {
            var set = session.GetDesiredBreakpointsForChunk(proto.ChunkName);
            if (set != null)
            {
                ClearBreakpoints(proto.ChunkName);
                foreach (var line in set) SetBreakPointAtLine(proto, line);
            }
        }

        else
        {
             RpcServer .WriteToConsole($"[Lua.DebugServer] No active debug session");
        }
    }

    public void SetBreakPointAtLine(string chunkName, int line)
    {
        if (!chunkName.StartsWith('@')) chunkName = "@" + chunkName;
        lock (sync)
        {
            if (pending.TryGetValue(chunkName, out var list))
            {
                if (!list.Contains(line)) list.Add(line);
            }
            else
            {
                pending[chunkName] = new List<int> { line };
            }

            if (protos.TryGetValue(chunkName, out var proto)) SetBreakPointAtLine(proto, line);
        }
    }

    public Instruction HandleDebugBreak(LuaState thread, int pc, LuaClosure closure)
    {
        var proto = closure.Proto;
        var key = (proto, pc);
        Instruction oldInstruction;
        lock (sync)
        {
            if (stepBreak is not null && stepBreak.Value.Key == key)
            {
                // This was a temp step breakpoint
                oldInstruction = stepBreak.Value.Value;
                DebugUtility.PatchInstruction(proto, pc, oldInstruction);
                stepBreak = null;
            }
            else if (!breakpoints.TryGetValue(key, out oldInstruction))
            {
                RpcServer.WriteToConsole($"[Lua.DebugServer] Breakpoint hit at {proto.ChunkName.TrimStart('@')}:{proto.LineInfo[pc]} (instruction {pc}) but no breakpoint was registered here.");
                throw new InvalidOperationException("No breakpoint set at this location.");
            }
        }
        // Outside lock to avoid holding during UI roundtrip
        {
            // Apply any pending desired breakpoints (VM thread only)
            var session = LuaDebugSession.Current;
            if (session != null)
            {
                var (dirty, snap) = session.SnapshotDesiredBreakpoints();
                if (dirty)
                {
                    foreach (var kv in snap)
                    {
                        ClearBreakpoints(kv.Key);
                        foreach (var line in kv.Value) SetBreakPointAtLine(kv.Key, line);
                    }
                }
            }

            // If stepping is active and this is NOT a temp step (i.e., a user breakpoint in another proto), auto-continue.
            

            // Capture locals and pause until a 'continue' RPC arrives
            LuaDebugSession.Current?.UpdateStoppedContext(thread, pc, closure);

            var file = proto.ChunkName.TrimStart('@');
            {
                // line is defined above
                var line = proto.LineInfo[pc];

                // Pause
                LuaDebugSession.PauseForBreakpoint(file, line);
            }

            // If any step was active, clean them all up (regardless of where we stopped)

            return oldInstruction;
        }
    }

    public void SetBreakPointAtLine(Prototype proto, int line)
    {
        for (int i = 0; i < proto.LineInfo.Length; i++)
        {
            if (proto.LineInfo[i] == line)
            {
                SetBreakpoint(proto, i);
                return;
            }
        }

        foreach (var p in proto.ChildPrototypes)
        {
            SetBreakPointAtLine(p, line);
        }
    }

    void SetBreakpoint(Prototype proto, int instructionIndex)
    {
        if (instructionIndex < 0 || instructionIndex >= proto.Code.Length)
            throw new ArgumentOutOfRangeException(nameof(instructionIndex));

        var key = (proto, instructionIndex);
        lock (sync)
        {
            if(stepBreak is not null && stepBreak.Value.Key == key)
            {
                stepBreak = null;
            }
            
            if (!breakpoints.ContainsKey(key))
            {
                var oldInstruction = DebugUtility.PatchDebugInstruction(proto, instructionIndex);
                breakpoints[key] = oldInstruction;
            }
        }
    }

    public void ClearBreakpoints(string chunkName)
    {
        if (!chunkName.StartsWith('@')) chunkName = "@" + chunkName;
        lock (sync)
        {
            if (protos.TryGetValue(chunkName, out var proto))
            {
                var paused = LuaDebugSession.Current?.GetPausedLocation() ?? (null, -1);
                for (int i = 0; i < proto.LineInfo.Length; i++)
                {
                    var key = (proto, i);
                    if (breakpoints.TryGetValue(key, out var old))
                    {
                        // Don't remove the breakpoint at the current paused location
                        if (!(paused.proto == proto && paused.pc == i))
                        {
                            DebugUtility.PatchInstruction(proto, i, old);
                            breakpoints.Remove(key);
                        }
                    }
                }
            }
            pending.Remove(chunkName);
        }
    }

    public bool SetStepToNextLine(Prototype proto, int pc)
    {
        
        if (this.stepBreak is {} stepBreak)
        {
            DebugUtility.PatchInstruction(stepBreak .Key.proto, stepBreak .Key.index, stepBreak.Value);
        }
        // Clear any previous temp step patches
        if (pc < 0 || pc >= proto.LineInfo.Length) return false;
        var currentLine = proto.LineInfo[pc];
        var patchedAny = false;
        for (int i = pc + 1; i < proto.LineInfo.Length; i++)
        {
            if (proto.LineInfo[i] != currentLine)
            {
                var key = (proto, i);
                Instruction oldInstruction;
                lock (sync)
                {
                    if (!breakpoints.TryGetValue(key , out oldInstruction))
                    {
                         oldInstruction = DebugUtility.PatchDebugInstruction(proto, i);
                        this.stepBreak = new KeyValuePair<(Prototype, int), Instruction>(key, oldInstruction);
                    }
                    else 
                    {
                        return false;
                    }
                }
                patchedAny = true;
                break;
            }
        }
        return patchedAny;
    }
}
