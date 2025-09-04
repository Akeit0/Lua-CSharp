using Lua;
using Lua.Debugging;
using Lua.Runtime;

class MinimalDebugger : IDebugger
{
    readonly Dictionary<(Prototype, int), Instruction> breakpoints = new();
    readonly Dictionary<string, List<int>> pending = new();
    readonly Dictionary<string, Prototype> protos = new();
    readonly Dictionary<string, HashSet<int>> instrPending = new(StringComparer.Ordinal);
    KeyValuePair<(Prototype proto, int index), Instruction>? stepBreak;
    LuaState? lastThread;
    readonly object sync = new();

    enum StepMode { None, In, Out }

    StepMode stepMode = StepMode.None;

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
            RpcServer.WriteToConsole($"[Lua.DebugServer] No active debug session");
        }

        // Apply any pending instruction index breakpoints
        lock (sync)
        {
            if (instrPending.TryGetValue(proto.ChunkName, out var set))
            {
                foreach (var idx in set)
                {
                    if (idx >= 0 && idx < proto.Code.Length)
                        SetBreakpoint(proto, idx);
                }
            }
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
        lastThread = thread;
        var proto = closure.Proto;
        var key = (proto, pc);
        Instruction oldInstruction;
        bool byStep = false;
        lock (sync)
        {
            if (stepBreak is not null && stepBreak.Value.Key == key)
            {
                // This was a temp step breakpoint
                oldInstruction = stepBreak.Value.Value;
                DebugUtility.PatchInstruction(proto, pc, oldInstruction);
                // // Remove from canonical map if present
                // lock (sync)
                // {
                //     breakpoints.Remove(key);
                // }
                stepBreak = null;
                byStep = true;
                //RpcServer .WriteToConsole($"[Lua.DebugServer] Step breakpoint hit at {proto.ChunkName.TrimStart('@')}:{proto.LineInfo[pc]} (instruction {pc})");
            }
            else if (!breakpoints.TryGetValue(key, out oldInstruction))
            {
                RpcServer.WriteLogToConsole($"[Lua.DebugServer] Breakpoint hit at {proto.ChunkName.TrimStart('@')}:{proto.LineInfo[pc]} (instruction {pc}) but no breakpoint was registered here.");
                throw new InvalidOperationException("No breakpoint set at this location.");
            }
        }

        RpcServer.WriteLogToConsole($"[Lua.DebugServer] Breakpoint hit at {Path.GetFileName(closure.Proto.ChunkName.TrimStart('@'))}:{closure.Proto.LineInfo[pc]} (instruction {oldInstruction}:{pc}) by {(byStep ? "step" : "breakpoint")}");

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

            // // If stepping is active and this is NOT the step temp breakpoint, auto-continue (ignore user BPs while stepping)
            // bool isStep = false;
            // lock (sync)
            // {
            //     if (stepBreak is { } sb && sb.Key == key) isStep = true;
            // }
            // if (!isStep && stepMode != StepMode.None)
            // {
            //     RpcServer .WriteToConsole($"[Lua.DebugServer] Auto-resuming due to active step mode {stepMode} at {proto.ChunkName.TrimStart('@')}:{proto.LineInfo[pc]} (instruction {pc})");
            //     return oldInstruction;
            // }

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

            // stepMode = StepMode.None;
            //RpcServer .WriteToConsole($"[Lua.DebugServer] Resuming from breakpoint at {proto.ChunkName.TrimStart('@')}:{proto.LineInfo[pc]} (instruction {pc})");

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
            if (stepBreak is not null && stepBreak.Value.Key == key)
            {
                DeleteStepBreak();
            }

            if (!breakpoints.ContainsKey(key))
            {
                var oldInstruction = DebugUtility.PatchDebugInstruction(proto, instructionIndex);
                breakpoints[key] = oldInstruction;
            }
        }
    }

    public void SetInstructionBreakpoint(string chunkName, int index)
    {
        if (!chunkName.StartsWith('@')) chunkName = "@" + chunkName;
        lock (sync)
        {
            if (protos.TryGetValue(chunkName, out var proto))
            {
                SetBreakpoint(proto, index);
            }

            if (!instrPending.TryGetValue(chunkName, out var set))
            {
                set = new HashSet<int>();
                instrPending[chunkName] = set;
            }

            set.Add(index);
        }
    }

    public void ClearInstructionBreakpoint(string chunkName, int index)
    {
        if (!chunkName.StartsWith('@')) chunkName = "@" + chunkName;
        lock (sync)
        {
            if (protos.TryGetValue(chunkName, out var proto))
            {
                var key = (proto, index);
                if (breakpoints.TryGetValue(key, out var old))
                {
                    DebugUtility.PatchInstruction(proto, index, old);
                    breakpoints.Remove(key);
                }
            }

            if (instrPending.TryGetValue(chunkName, out var set))
            {
                set.Remove(index);
                if (set.Count == 0) instrPending.Remove(chunkName);
            }
        }
    }

    public int[] GetInstructionBreakpoints(string chunkName)
    {
        if (!chunkName.StartsWith('@')) chunkName = "@" + chunkName;
        lock (sync)
        {
            if (!protos.TryGetValue(chunkName, out var proto))
                return instrPending.TryGetValue(chunkName, out var set) ? set.ToArray() : Array.Empty<int>();

            var list = new List<int>();
            foreach (var kv in breakpoints.Keys)
            {
                if (kv.Item1 == proto) list.Add(kv.Item2);
            }

            return list.ToArray();
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

    public void DeleteStepBreak()
    {
        lock (sync)
        {
            if (stepBreak is { } sb)
            {
                DebugUtility.PatchInstruction(sb.Key.proto, sb.Key.index, sb.Value);
                stepBreak = null;
            }

            stepMode = StepMode.None;
        }
    }

    public bool SetStepToNextLine(Prototype proto, int pc)
    {
        // Restore any previous step trap and remove it from the breakpoint map
        DeleteStepBreak();
        // Clear any previous temp step patches
        if (pc < 0 || pc >= proto.LineInfo.Length) return false;

        var currentInstruction = proto.Code[pc];
        if (currentInstruction.OpCode == (OpCode)40)
        {
            currentInstruction = breakpoints[(proto, pc)];
        }

        var nextPc = pc + 1;
        var state = this.lastThread!;
        var stack = state.Stack.AsSpan();
        int frameBase = state.GetCurrentFrame().Base;
        switch (currentInstruction.OpCode)
        {
            case OpCode.Jmp: nextPc += currentInstruction.SBx; break;
            case OpCode.ForPrep: nextPc += currentInstruction.SBx; break;
            case OpCode.TForLoop:
                {
                    var forState = stack[currentInstruction.A + frameBase + 1];
                    if (forState.Type != LuaValueType.Nil)
                    {
                        nextPc += currentInstruction.SBx;
                    }

                    break;
                }
            case OpCode.ForLoop:
                var limit = stack[currentInstruction.A + frameBase + 1].Read<double>();
                var step = stack[currentInstruction.A + frameBase + 2].Read<double>();
                var index = stack[currentInstruction.A + frameBase].Read<double>() + step;

                if (step >= 0 ? index <= limit : limit <= index)
                {
                    nextPc += currentInstruction.SBx;
                }

                break;
            case OpCode.Return:
                return false;
        }

        var currentLine = proto.LineInfo[pc];
        for (int i = nextPc; i < proto.LineInfo.Length; i++)
        {
            if (proto.LineInfo[i] != currentLine)
            {
                var key = (proto, i);
                lock (sync)
                {
                    if (!breakpoints.TryGetValue(key, out var oldInstruction))
                    {
                        // Patch the step trap and store the original instruction in the breakpoint map
                        oldInstruction = DebugUtility.PatchDebugInstruction(proto, i);
                        this.stepBreak = new KeyValuePair<(Prototype, int), Instruction>(key, oldInstruction);
                        //RpcServer.WriteToConsole($"[Lua.DebugServer] Step-To-Next-Line armed at {proto.ChunkName.TrimStart('@')}:{proto.LineInfo[i]} (instruction {i}) {oldInstruction}");
                    }
                    else
                    {
                        // RpcServer.WriteToConsole($"[Lua.DebugServer] Step-To-Next-Line: already has a breakpoint at {proto.ChunkName.TrimStart('@')}:{proto.LineInfo[i]} (instruction {i}) {oldInstruction}");
                    }
                }

                return true;
            }
        }

        return false;
    }

    // New IDebugger methods for call stack notifications
    public void OnPushCallStackFrame(LuaState thread)
    {
        //RpcServer .WriteToConsole($"[Lua.DebugServer] OnPushCallStackFrame (stepMode={stepMode})");

        if (stepMode == StepMode.In)
        {
            // Arm a step at the very start of the callee (PC = 0)
            var f = thread.GetCurrentFrame();
            if (f.Function is LuaClosure clo)
            {
                var p = clo.Proto;
                var key = (p, 0);
                lock (sync)
                {
                    if (stepBreak is not null)
                    {
                        if (stepBreak.Value.Key == key) return;
                        DebugUtility.PatchInstruction(stepBreak.Value.Key.proto, stepBreak.Value.Key.index, stepBreak.Value.Value);
                    }

                    if (!breakpoints.TryGetValue(key, out var oldInstruction))
                    {
                        oldInstruction = DebugUtility.PatchDebugInstruction(p, 0);
                        stepBreak = new KeyValuePair<(Prototype, int), Instruction>(key, oldInstruction);
                        //RpcServer.WriteToConsole($"[Lua.DebugServer] Step-In armed at {p.ChunkName.TrimStart('@')}:{p.LineInfo[0]} (instruction 0) {oldInstruction}");
                    }
                    else
                    {
                        //RpcServer.WriteToConsole($"[Lua.DebugServer] Step-In: callee already has a breakpoint at {p.ChunkName.TrimStart('@')}:{p.LineInfo[0]} (instruction 0) {oldInstruction}");
                    }

                    // Keep stepMode active until the trap fires
                }
            }
        }
    }

    public void OnPopCallStackFrame(LuaState thread)
    {
        //RpcServer .WriteToConsole($"[Lua.DebugServer] OnPopCallStackFrame (stepMode={stepMode})");

        if (stepMode == StepMode.Out)
        {
            if (stepBreak is not null)
            {
                DebugUtility.PatchInstruction(stepBreak.Value.Key.proto, stepBreak.Value.Key.index, stepBreak.Value.Value);
                stepBreak = null;
            }

            // After pop, current frame is caller; arm a step at the next different line after the call site
            var f = thread.GetCurrentFrame();
            var caller = thread.GetCallStackFrames()[^2];
            if (caller.Function is LuaClosure clo)
            {
                var p = clo.Proto;
                var pc = Math.Max(0, f.CallerInstructionIndex);
                int i = pc + 1;
                var key = (p, i);
                Instruction oldInstruction;
                lock (sync)
                {
                    if (!breakpoints.TryGetValue(key, out oldInstruction))
                    {
                        oldInstruction = DebugUtility.PatchDebugInstruction(p, i);
                        stepBreak = new KeyValuePair<(Prototype, int), Instruction>(key, oldInstruction);
                        //RpcServer.WriteToConsole($"[Lua.DebugServer] Step-Out armed at {p.ChunkName.TrimStart('@')}:{p.LineInfo[i]} (instruction {i}) {oldInstruction}");
                    }
                    else
                    {
                        //  RpcServer .WriteToConsole($"[Lua.DebugServer] Step-Out: caller already has a breakpoint at {p.ChunkName.TrimStart('@')}:{p.LineInfo[i]} (instruction {i}) {oldInstruction}");
                    }
                    // Keep stepMode active until the trap fires
                }
            }
            else
            {
                RpcServer.WriteLogToConsole($"[Lua.DebugServer] OnPopCallStackFrame: caller is not a LuaClosure");
            }
        }
    }

    // Helpers to start step-in/out from session
    public void StartStepIn()
    {
        lock (sync)
        {
            if (stepBreak is { } sb)
            {
                DebugUtility.PatchInstruction(sb.Key.proto, sb.Key.index, sb.Value);
                stepBreak = null;
            }

            stepMode = StepMode.In;

            //RpcServer .WriteToConsole($"[Lua.DebugServer] Step-In armed");
        }
    }

    public void StartStepOut()
    {
        lock (sync)
        {
            if (stepBreak is { } sb)
            {
                DebugUtility.PatchInstruction(sb.Key.proto, sb.Key.index, sb.Value);
                stepBreak = null;
            }

            stepMode = StepMode.Out;
        }
    }

    // Expose patched original instruction info for UI snapshot
    public bool TryGetPatchedOriginal(Prototype proto, int index, out Instruction original, out bool isStep)
    {
        lock (sync)
        {
            if (stepBreak is { } sb && sb.Key.proto == proto && sb.Key.index == index)
            {
                original = sb.Value;
                isStep = true;
                return true;
            }

            if (breakpoints.TryGetValue((proto, index), out var old))
            {
                original = old;
                isStep = false;
                return true;
            }
        }

        original = default;
        isStep = false;
        return false;
    }
}