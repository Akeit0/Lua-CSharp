using Lua;
using Lua.Debugging;
using Lua.Runtime;
using Lua.Standard;

sealed class LuaDebugSession
{
    public static LuaDebugSession? Current { get; set; }

    LuaState? state;
    MinimalDebugger? debugger;
    TaskCompletionSource<bool>? pauseTcs;
    readonly List<(string name, string value)> locals = new();
    readonly List<(string name, string value)> globals = new();
    Prototype? lastProto;
    int lastPc;
    bool isSteppingNext;
    int lastDepth;

    int stepDepth;

    // Desired user breakpoints by chunk, updated from RPC thread, applied on VM thread at stop/register
    readonly Dictionary<string, HashSet<int>> desiredBps = new(StringComparer.Ordinal);
    bool bpDirty;

    public void SetBreakpoints(string source, List<int> lines)
    {
        EnsureDebugger();
        // Normalize chunk name
        source = source.Replace("c:", "C:");
        source = "@" + source.Replace("\\", "/");
        lock (locals)
        {
            desiredBps[source] = new HashSet<int>(lines);
            bpDirty = true;
        }
    }

    // Called from VM thread while paused
    public (bool dirty, Dictionary<string, HashSet<int>> snapshot) SnapshotDesiredBreakpoints()
    {
        lock (locals)
        {
            if (!bpDirty)
                return (false, new Dictionary<string, HashSet<int>>());
            var snap = new Dictionary<string, HashSet<int>>(desiredBps.Count, desiredBps.Comparer);
            foreach (var kv in desiredBps)
                snap[kv.Key] = new HashSet<int>(kv.Value);
            bpDirty = false;
            return (true, snap);
        }
    }

    // Non-destructive read for a single chunk (used in RegisterPrototype)
    public HashSet<int>? GetDesiredBreakpointsForChunk(string chunkName)
    {
        lock (locals)
        {
            if (desiredBps.TryGetValue(chunkName, out var set))
            {
                return new HashSet<int>(set);
            }

            return null;
        }
    }

    public async Task LaunchAsync(string program, string? cwd, bool stopOnEntry)
    {
        if (!string.IsNullOrEmpty(cwd)) Directory.SetCurrentDirectory(cwd);
        // Reuse existing debugger so pre-launch breakpoints persist
        debugger ??= new MinimalDebugger();

        var state = LuaState.Create();
        state.Platform = state.Platform with { StandardIO = new DebugIO() };
        state.OpenBasicLibrary();
        state.Debugger = debugger;
        this.state = state;
        // if (stopOnEntry)
        // {
        //     await PauseAndWait("entry", program, 1);
        // }

        var p = await state.LoadFileAsync(program, "bt", null, default);
        _ = Task.Run(async () =>
        {
            try
            {
                await state.ExecuteAsync(p);
                RpcServer.Publish("terminated", new { });
            }
            catch (Exception ex)
            {
                RpcServer.Publish("output", new { category = "stderr", output = (ex.InnerException?.StackTrace ?? "") + "\n" });
                RpcServer.Publish("output", new { category = "stderr", output = ex + "\n" });
                RpcServer.Publish("terminated", new { });
            }
        });
    }

    public void Continue(bool skipStepBreak = false)
    {
        if (skipStepBreak)
        {
            debugger?.DeleteStepBreak();
        }

        pauseTcs?.TrySetResult(true);
        RpcServer.Publish("continued", new { threadId = 1, allThreadsContinued = true });
    }


    void EnsureDebugger()
    {
        if (this.state != null && debugger != null) return;
        var state = LuaState.Create();
        state.OpenBasicLibrary();
        var dbg = new MinimalDebugger();
        state.Debugger = dbg;
        this.state = state;
        debugger = dbg;
    }

    public static void PauseForBreakpoint(string file, int line, string reason = "breakpoint")
    {
        var s = Current;

        if (s is null)
        {
            RpcServer.Publish("output", new { category = "stderr", output = "[Lua.DebugServer] Warning: Breakpoint hit but no debug session is active.\n" });
            return;
        }

        Task toWait;
        string? publishReason = null;
        lock (s.locals) // use _locals list as a simple sync object; replaced below by _sync if added
        {
            if (s.pauseTcs is null || s.pauseTcs.Task.IsCompleted)
            {
                s.pauseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                publishReason = s.isSteppingNext ? "step" : reason;
                s.isSteppingNext = false;
            }

            toWait = s.pauseTcs.Task;
        }

        if (publishReason is not null)
        {
            RpcServer.Publish("stopped", new { reason = publishReason, threadId = 1, file, line });
        }

        RpcServer.Publish("wait", new { reason = "started", threadId = 1 });
        toWait.Wait();
    }

    public void UpdateStoppedContext(LuaState thread, int pc, LuaClosure closure)
    {
        lock (locals)
        {
            locals.Clear();
            var proto = closure.Proto;
            var f = thread.GetCurrentFrame();
            var stack = thread.Stack.AsSpan()[f.Base..];
            var unique = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < proto.MaxStackSize && i < stack.Length; i++)
            {
                var name = DebugUtility.GetLocalVariableName(proto, i, pc);
                if (!string.IsNullOrEmpty(name))
                {
                    var key = name.Trim();
                    if (key.Length == 0) continue;
                    if (!unique.ContainsKey(key))
                    {
                        unique[key] = stack[i].ToString();
                    }
                }
            }

            foreach (var kv in unique)
            {
                locals.Add((kv.Key, kv.Value));
            }

            proto.ChunkName.TrimStart('@');
            lastProto = proto;
            lastPc = pc;
            lastDepth = thread.CallStackFrameCount;
            // Snapshot globals as of stop
            globals.Clear();
            try
            {
                foreach (var kv in state?.Environment ?? new LuaTable())
                {
                    if (kv.Key.TryRead<string>(out var name) && !string.IsNullOrWhiteSpace(name))
                    {
                        name = name.Trim();
                        if (name.Length == 0) continue;
                        string value;
                        try { value = kv.Value.ToString(); }
                        catch { value = ""; }

                        globals.Add((name, value));
                    }
                }
            }
            catch
            {
                /* ignore snapshot errors */
            }
        }
    }

    public object[] GetLocals()
    {
        lock (locals)
        {
            return locals.Select(v => new { name = v.name, value = v.value }).Cast<object>().ToArray();
        }
    }

    public object[] GetGlobals()
    {
        lock (locals)
        {
            return globals.Select(v => new { name = v.name, value = v.value }).Cast<object>().ToArray();
        }
    }

    public (Prototype? proto, int pc) GetPausedLocation()
    {
        lock (locals)
        {
            return (lastProto, lastPc);
        }
    }

    public object? GetBytecodeSnapshot()
    {
        lock (locals)
        {
            if (lastProto is null) return null;
            var proto = lastProto;
            var code = proto.Code;
            var lines = proto.LineInfo;
            var instructions = new List<object>(code.Length);
            for (int i = 0; i < code.Length; i++)
            {
                int line = (i >= 0 && i < lines.Length) ? lines[i] : 0;
                var instruction = code[i];
                // If this is a patched debug trap, replace with original instruction for display


                if (code[i].OpCode == (OpCode)40 && debugger is not null)
                {
                    if (debugger.TryGetPatchedOriginal(proto, i, out var original, out _))
                    {
                        instruction = original;
                    }
                }


                string text;
                try { text = instruction.ToString(); }
                catch { text = instruction.Value.ToString(); }

                instructions.Add(new { index = i, line, text });
            }

            // Best-effort metadata
            var constants = new List<string>();
            try
            {
                foreach (var k in proto.Constants)
                {
                    string v;
                    try { v = k.ToString(); }
                    catch { v = string.Empty; }

                    constants.Add(v);
                }
            }
            catch { }

            var localsMeta = new List<object>();
            try
            {
                foreach (var lv in proto.LocalVariables)
                {
                    localsMeta.Add(new { lv.Name, lv.StartPc, lv.EndPc });
                }
            }
            catch { }

            var upvaluesMeta = new List<object>();
            try
            {
                foreach (var uv in proto.UpValues)
                {
                    upvaluesMeta.Add(new { uv.Name, uv.IsLocal, uv.Index });
                }
            }
            catch { }

            string chunk = proto.ChunkName.TrimStart('@');
            return new
            {
                chunk,
                pc = lastPc,
                instructions = instructions.ToArray(),
                constants = constants.ToArray(),
                locals = localsMeta.ToArray(),
                upvalues = upvaluesMeta.ToArray(),
            };
        }
    }

    public void StepNext()
    {
        if (debugger is null || lastProto is null) return;
        isSteppingNext = true;
        stepDepth = lastDepth;
        debugger.SetStepToNextLine(lastProto, lastPc);
    }

    public void StepIn()
    {
        if (debugger is null) return;
        debugger.StartStepIn();
    }

    public void StepOut()
    {
        if (debugger is null) return;
        debugger.StartStepOut();
    }

    public bool ShouldSkipBreak(int currentDepth)
    {
        lock (locals)
        {
            return isSteppingNext && currentDepth > stepDepth;
        }
    }

    public bool IsSteppingActive()
    {
        lock (locals)
        {
            return isSteppingNext;
        }
    }

    public void SetInstructionBreakpoint(string chunkName, int index, bool enabled)
    {
        EnsureDebugger();
        if (debugger is null) return;
        if (enabled) debugger.SetInstructionBreakpoint(chunkName, index);
        else debugger.ClearInstructionBreakpoint(chunkName, index);
    }

    public int[] GetInstructionBreakpoints(string chunkName)
    {
        EnsureDebugger();
        return debugger?.GetInstructionBreakpoints(chunkName) ?? Array.Empty<int>();
    }
}