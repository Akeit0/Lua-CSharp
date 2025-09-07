using Lua;
using Lua.Debugging;
using Lua.Runtime;

class MinimalDebugger : IDebugger
{
    public static MinimalDebugger? Active;

    public MinimalDebugger()
    {
        Active = this;
    }

    readonly Dictionary<(Prototype, int), Instruction> breakpoints = new();
    readonly Dictionary<(Prototype, int), (string? cond, string? hit, string? log)> breakpointOptions = new();
    readonly Dictionary<(Prototype, int), int> breakpointHitCounts = new();
    readonly Dictionary<string, List<int>> pending = new();
    readonly Dictionary<string, Prototype> protos = new();
    readonly Dictionary<string, HashSet<int>> instrPending = new(StringComparer.Ordinal);
    
    // Expose search by file/line across registered prototypes
    public (Prototype? proto, int pc) FindPrototypeBySource(string file, int line)
    {
        if (string.IsNullOrWhiteSpace(file)) return (null, -1);
        // Normalize incoming path to chunk-style keys used in this debugger
        string norm = file.Replace("\\", "/");
        if (!norm.StartsWith("@")) norm = "@" + norm;

        lock (sync)
        {
            // Collect candidate roots that match exact path
            var candidates = new List<Prototype>();
            foreach (var kv in protos)
            {
                if (string.Equals(kv.Key, norm, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(kv.Value);
                }
            }
            // If none, fallback to filename match
            if (candidates.Count == 0)
            {
                var target = System.IO.Path.GetFileName(norm.TrimStart('@').Replace('/', System.IO.Path.DirectorySeparatorChar));
                foreach (var kv in protos)
                {
                    var name = System.IO.Path.GetFileName(kv.Key.TrimStart('@').Replace('/', System.IO.Path.DirectorySeparatorChar));
                    if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(kv.Value);
                    }
                }
            }

            foreach (var root in candidates)
            {
                var found = FindInPrototypeTree(root, line);
                if (found.proto is not null) return found;
            }

            return (null, -1);
        }
    }

    static (Prototype? proto, int pc) FindInPrototypeTree(Prototype proto, int line)
    {
        // Check children first to prefer most specific function
        foreach (var c in proto.ChildPrototypes)
        {
            var found = FindInPrototypeTree(c, line);
            if (found.proto is not null) return found;
        }

        // Then check this prototype's line table
        int pc = -1;
        for (int i = 0; i < proto.LineInfo.Length; i++)
        {
            if (proto.LineInfo[i] == line) { pc = i; break; }
        }

        return pc >= 0 ? (proto, pc) : (null, -1);
    }
    KeyValuePair<(Prototype proto, int index), Instruction>? stepBreak;
    LuaState? lastThread;
    int pushCount = 0;
    readonly object sync = new();

    enum StepMode { None, Over, In, Out }

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
                foreach (var kv in set) SetBreakPointAtLine(proto, kv.Key, kv.Value.condition, kv.Value.hit, kv.Value.log);
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

    public void SetBreakPointAtLine(string chunkName, int line, string? condition = null, string? hit = null, string? log = null)
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

            if (protos.TryGetValue(chunkName, out var proto)) SetBreakPointAtLine(proto, line, condition, hit, log);
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
                        foreach (var bp in kv.Value)
                            SetBreakPointAtLine(kv.Key, bp.Key, bp.Value.condition, bp.Value.hit, bp.Value.log);
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

            // Evaluate conditional breakpoint and hit counts, and handle logpoints
            bool shouldPause = true;
            (string? cond, string? hit, string? log) opts;
            lock (sync) { breakpointOptions.TryGetValue(key, out opts); }

            // hit count
            int hitCount = 0;
            lock (sync)
            {
                if (!breakpointHitCounts.TryGetValue(key, out hitCount)) hitCount = 0;
                hitCount++;
                breakpointHitCounts[key] = hitCount;
            }

            if (!string.IsNullOrWhiteSpace(opts.hit))
            {
                // RpcServer .WriteLogToConsole($"[Lua.DebugServer] Evaluating hit condition '{opts.hit}' at hit count {hitCount}");
                try
                {
                    if (!EvaluateHitCondition(hitCount, opts.hit!)) shouldPause = false;
                }
                catch (Exception ex)
                {
                    RpcServer.WriteToConsole($"[Lua.DebugServer] hitCondition error: {ex.Message}\n", "stderr");
                    shouldPause = false;
                }
            }

            if (shouldPause && !string.IsNullOrWhiteSpace(opts.cond))
            {
                //RpcServer .WriteLogToConsole($"[Lua.DebugServer] Evaluating condition '{opts.cond}'");
                try { shouldPause = EvaluateCondition(thread, pc, closure, opts.cond!); }
                catch (Exception ex)
                {
                    RpcServer.WriteToConsole($"[Lua.DebugServer] condition error: {ex.Message}\n", "stderr");
                    shouldPause = false;
                }
            }

            // logpoint: if message is present, log and do not pause
            if (shouldPause && !string.IsNullOrWhiteSpace(opts.log))
            {
                //RpcServer .WriteLogToConsole($"[Lua.DebugServer] Hit logpoint: {opts.log}");
                try { LogMessage(thread, pc, closure, opts.log!); }
                catch (Exception ex) { RpcServer.WriteToConsole($"[Lua.DebugServer] logpoint error: {ex.Message}\n", "stderr"); }

                shouldPause = false;
            }

            if (shouldPause)
            {
                // Capture locals and pause until a 'continue' RPC arrives
                LuaDebugSession.Current?.UpdateStoppedContext(thread, pc, closure);
                var file = proto.ChunkName.TrimStart('@');
                var line = proto.LineInfo[pc];
                LuaDebugSession.PauseForBreakpoint(file, line);
            }

            // If any step was active, clean them all up (regardless of where we stopped)

            // stepMode = StepMode.None;
            //RpcServer .WriteToConsole($"[Lua.DebugServer] Resuming from breakpoint at {proto.ChunkName.TrimStart('@')}:{proto.LineInfo[pc]} (instruction {pc})");

            return oldInstruction;
        }
    }

    public void SetBreakPointAtLine(Prototype proto, int line, string? condition = null, string? hit = null, string? log = null)
    {
        for (int i = 0; i < proto.LineInfo.Length; i++)
        {
            if (proto.LineInfo[i] == line)
            {
                SetBreakpoint(proto, i, condition, hit, log);
                return;
            }
        }

        foreach (var p in proto.ChildPrototypes)
        {
            SetBreakPointAtLine(p, line, condition, hit, log);
        }
    }

    void SetBreakpoint(Prototype proto, int instructionIndex, string? condition = null, string? hit = null, string? log = null)
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
                breakpointOptions[key] = (condition, hit, log);
                breakpointHitCounts[key] = 0;
            }
            else
            {
                breakpointOptions[key] = (condition, hit, log);
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
                            breakpointOptions.Remove(key);
                            breakpointHitCounts.Remove(key);
                        }
                    }
                }
            }

            pending.Remove(chunkName);
        }
    }

    // Apply desired breakpoints for an already-registered chunk
    public void ApplyBreakpoints(string chunkName, Dictionary<int, (string? condition, string? hit, string? log)> lines)
    {
        if (!chunkName.StartsWith('@')) chunkName = "@" + chunkName;
        lock (sync)
        {
            if (!protos.TryGetValue(chunkName, out var proto))
            {
                // Fallback: match by file name
                var targetName = System.IO.Path.GetFileName(chunkName.TrimStart('@').Replace('\\', '/'));
                foreach (var kv in protos)
                {
                    var name = System.IO.Path.GetFileName(kv.Key.TrimStart('@').Replace('\\', '/'));
                    if (string.Equals(name, targetName, StringComparison.Ordinal))
                    {
                        proto = kv.Value;
                        break;
                    }
                }

                if (proto is null) return;
            }

            // Clear existing for this chunk (except current paused location)
            ClearBreakpoints(chunkName);
            foreach (var kv in lines)
            {
                SetBreakPointAtLine(proto, kv.Key, kv.Value.condition, kv.Value.hit, kv.Value.log);
            }
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

    public bool SetStepToNextLine(Prototype proto, int pc, bool stepIn = false)
    {
        pushCount = 0;
        // Restore any previous step trap and remove it from the breakpoint map
        DeleteStepBreak();
        stepMode = StepMode.Over;
        // Clear any previous temp step patches
        if (pc < 0 || pc >= proto.LineInfo.Length) return false;

        var currentInstruction = GetOriginalInstruction(proto, pc);

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
            if (stepIn)
            {
                var instruction = GetOriginalInstruction(proto, i);
                if (instruction.OpCode is OpCode.Call or OpCode.TailCall)
                {
                    return false;
                }
            }

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
        RpcServer.WriteToConsole($"[Lua.DebugServer] OnPushCallStackFrame (stepMode={stepMode})");

        if (stepMode == StepMode.In)
        {
            // Arm a step at the very start of the callee (PC = 0)
            var f = thread.GetCurrentFrame();
            if (f.Function is LuaClosure clo)
            {
                var p = clo.Proto;
                LuaDebugSession.Current?.UpdateStoppedContext(thread, 0, clo);
                var file = p.ChunkName.TrimStart('@');
                var line = p.LineInfo[0];
                LuaDebugSession.PauseForBreakpoint(file, line);
            }
            else
            {
                //RpcServer.WriteLogToConsole($"[Lua.DebugServer] OnPushCallStackFrame: callee is not a LuaClosure");
                // var caller = thread.GetCallStackFrames()[^2];
                // if (caller.Function is LuaClosure callerClosure)
                // {
                //     LuaDebugSession.Current?.UpdateStoppedContext(thread, f.CallerInstructionIndex, callerClosure);
                //
                //     var callerProto = callerClosure.Proto;
                //     var file = callerProto.ChunkName.TrimStart('@');
                //     {
                //         // line is defined above
                //         var line = callerProto.LineInfo[f.CallerInstructionIndex];
                //         
                //         // Pause
                //         LuaDebugSession.PauseForBreakpoint(file, line);
                //     }
                // }
            }
        }

        if (stepMode == StepMode.Over)
        {
            pushCount++;
        }
    }

    public void OnPopCallStackFrame(LuaState thread , ref CallStackFrame poppedFrame)
    {
        //RpcServer.WriteToConsole($"[Lua.DebugServer] OnPopCallStackFrame (stepMode={stepMode})");

        if (stepMode != StepMode.None)
        {
            if (stepMode == StepMode.Over)
            {
                pushCount--;
                if (pushCount > 0) return;
                stepMode = StepMode.None;
                return;
            }
            // After pop, current frame is caller; arm a step at the next different line after the call site
            var f = poppedFrame;
            var caller = thread.GetCurrentFrame();
            if (f.Function is LuaClosure && caller.Function is LuaClosure clo)
            {
                var p = clo.Proto;
                var callPc = f.CallerInstructionIndex;
                LuaDebugSession.Current?.UpdateStoppedContext(thread, callPc, clo);
                var file = p.ChunkName.TrimStart('@');
                var line = p.LineInfo[callPc];
                RpcServer.WriteLogToConsole ($"[Lua.DebugServer] Step-Out to {file}:{line} (instruction {callPc})");
                LuaDebugSession.PauseForBreakpoint(file, line);
            }
            else
            {
                RpcServer.WriteLogToConsole($"[Lua.DebugServer] OnPopCallStackFrame: caller is not a LuaClosure");
            }
        }
    }

    // Helpers to start step-in/out from session
    public void StartStepIn(Prototype proto, int pc)
    {
        lock (sync)
        {
            SetStepToNextLine(proto, pc, stepIn: true);
            stepMode = StepMode.In;
            //RpcServer .WriteToConsole($"[Lua.DebugServer] Step-In armed");
        }
    }

    public void StartStepOut()
    {
        lock (sync)
        {
            DeleteStepBreak();
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

    public Instruction GetOriginalInstruction(Prototype proto, int index)
    {
        lock (sync)
        {
            if (stepBreak is { } sb && sb.Key.proto == proto && sb.Key.index == index)
            {
                return sb.Value;
            }

            if (breakpoints.TryGetValue((proto, index), out var old))
            {
                return old;
            }
        }

        return proto.Code[index];
    }

    bool EvaluateCondition(LuaState thread, int pc, LuaClosure closure, string condition)
    {
        condition = condition.Trim();
        if (condition.Length == 0) return true;

        // Simple forms: name, name == literal, name ~= literal, name != literal, numeric comparisons
        // Extract lhs, op, rhs
        ReadOnlySpan<char> ident = condition;
        string? op = null;
        ReadOnlySpan<char> rhs = default;
        foreach (var o in compareOps)
        {
            var idx = condition.IndexOf(o, StringComparison.Ordinal);
            if (idx > 0)
            {
                ident = condition.AsSpan(0, idx).Trim();
                op = o;
                rhs = condition.AsSpan(idx + o.Length).Trim();
                break;
            }
        }

        //RpcServer .WriteLogToConsole($"[Lua.DebugServer] Evaluating condition: ident='{ident}' op='{op}' rhs='{rhs}'");
        var val = GetValueByName(thread, pc, closure, ident);
        if (val is null)
        {
            // unknown name -> do not break
            return false;
        }

        if (op is null)
        {
            // truthiness: false or nil are false, everything else true
            try
            {
                if (val.Value.Type.ToString() == "Nil") return false;
            }
            catch { }

            if (val.Value.TryRead<bool>(out var b)) return b;
            return true;
        }

        // Parse RHS literal
        if (rhs.IsEmpty) return false;
        LuaValue? lit = null;
        if ((rhs.StartsWith("\"") && rhs.EndsWith("\"")) || (rhs.StartsWith("'") && rhs.EndsWith("'")))
        {
            lit = rhs.Slice(1, Math.Max(0, rhs.Length - 2)).ToString();
        }
        else if (rhs is "true") lit = true;
        else if (rhs is "false") lit = false;
        else if (rhs is "nil") lit = null;
        else if (double.TryParse(rhs, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) lit = d;
        else lit = rhs.ToString(); // bare string

        if (lit is null)
        {
            // compare with nil based on ToString/Type
            try
            {
                if (val.Value.Type.ToString() == "Nil") return op is "==";
                else return op is "!=" or "~=";
            }
            catch { return false; }
        }
        else
        {
            // Compare
            if (lit.Value.TryRead<double>(out var nd))
            {
                if (!val.Value.TryRead<double>(out var vd)) return false;
                return op switch
                {
                    "==" => vd == nd,
                    "!=" or "~=" => vd != nd,
                    ">" => vd > nd,
                    "<" => vd < nd,
                    ">=" => vd >= nd,
                    "<=" => vd <= nd,
                    _ => false
                };
            }

            if (lit.Value.TryRead<bool>(out var nb))
            {
                if (!val.Value.TryRead<bool>(out var vb)) return false;
                return op switch
                {
                    "==" => vb == nb,
                    "!=" or "~=" => vb != nb,
                    _ => false
                };
            }
        }


        // string
        var ls = lit.Value.ToString() ?? string.Empty;
        if (!val.Value.TryRead<string>(out var vs)) vs = val.Value.ToString();
        return op switch
        {
            "==" => string.Equals(vs, ls, StringComparison.Ordinal),
            "!=" or "~=" => !string.Equals(vs, ls, StringComparison.Ordinal),
            _ => false
        };
    }

    LuaValue? GetValueByName(LuaState thread, int pc, LuaClosure closure, ReadOnlySpan<char> name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        var proto = closure.Proto;
        var f = thread.GetCurrentFrame();
        var baseIndex = f.Base;
        var stack = thread.Stack.AsSpan();
        for (int i = 0; i < proto.MaxStackSize && (baseIndex + i) < stack.Length; i++)
        {
            var n = DebugUtility.GetLocalVariableName(proto, i, pc);
            if (!string.IsNullOrEmpty(n) && (n.Trim().SequenceEqual(name)))
            {
                return stack[baseIndex + i];
            }
        }

        // upvalues
        try
        {
            var desc = closure.Proto.UpValues;
            var values = closure.UpValues;
            var count = Math.Min(desc.Length, values.Length);
            for (int i = 0; i < count; i++)
            {
                string n = desc[i].Name;
                if (!string.IsNullOrEmpty(n) && (n.Trim().SequenceEqual(name)))
                {
                    return values[i].GetValue();
                }
            }
        }
        catch { }

        // globals
        try
        {
            foreach (var kv in thread.Environment)
            {
                if (kv.Key.TryRead<string>(out var n) && n.SequenceEqual(name))
                {
                    return kv.Value;
                }
            }
        }
        catch { }

        return null;
    }

    static readonly string[] compareOps = new[] { ">=", "<=", "==", "!=", "^=", ">", "<" };

    bool EvaluateHitCondition(int hitCount, string expr)
    {
        expr = expr.Trim();
        if (int.TryParse(expr, out var n)) return hitCount == n;
        foreach (var op in compareOps)
        {
            var idx = expr.IndexOf(op, StringComparison.Ordinal);
            if (idx > 0)
            {
                var rhsText = expr.AsSpan()[(idx + op.Length)..].Trim();
                if (!int.TryParse(rhsText, out var rhs)) return false;
                return op switch
                {
                    ">=" => hitCount >= rhs,
                    "<=" => hitCount <= rhs,
                    "==" => hitCount == rhs,
                    "~=" or "!=" => hitCount != rhs,
                    ">" => hitCount > rhs,
                    "<" => hitCount < rhs,
                    _ => false,
                };
            }
        }

        return false;
    }

    void LogMessage(LuaState thread, int pc, LuaClosure closure, string template)
    {
        // Replace {name} with value from locals/upvalues/globals
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < template.Length;)
        {
            var c = template[i];
            if (c == '{')
            {
                var j = template.IndexOf('}', i + 1);
                if (j > i + 1)
                {
                    var name = template.AsSpan(i + 1, j - i - 1).Trim();
                    var val = GetValueByName(thread, pc, closure, name);
                    string text;
                    if (val is null) text = "nil";
                    else
                    {
                        try { text = val.Value.ToString(); }
                        catch { text = ""; }
                    }

                    sb.Append(text);
                    i = j + 1;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        RpcServer.WriteToConsole(sb.ToString(), "stdout");
    }
}