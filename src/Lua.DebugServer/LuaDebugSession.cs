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
    readonly List<(string name, string value)> upvalues = new();
    Prototype? lastProto;
    int lastPc;
    bool isSteppingNext;
    int lastDepth;
    LuaState? lastThread;

    int stepDepth;

    // Desired user breakpoints by chunk with options (condition, hitCondition, logMessage)
    readonly Dictionary<string, Dictionary<int, (string? condition, string? hit, string? log)>> desiredBps = new(StringComparer.Ordinal);
    bool bpDirty;

    public void SetBreakpoints(string source, List<(int line, string? condition, string? hitCondition, string? logMessage)> breakpoints)
    {
        EnsureDebugger();
        // Normalize chunk name
        source = source.Replace("c:", "C:");
        source = "@" + source.Replace("\\", "/");
        lock (locals)
        {
            var map = new Dictionary<int, (string? condition, string? hit, string? log)>();
            foreach (var bp in breakpoints)
                map[bp.line] = (bp.condition, bp.hitCondition, bp.logMessage);
            desiredBps[source] = map;
            bpDirty = true;
        }

        // If the prototype is already registered, apply immediately
        try
        {
            debugger?.ApplyBreakpoints(source, GetDesiredBreakpointsForChunk(source)!);
        }
        catch { }
    }

    // Called from VM thread while paused
    public (bool dirty, Dictionary<string, Dictionary<int, (string? condition, string? hit, string? log)>> snapshot) SnapshotDesiredBreakpoints()
    {
        lock (locals)
        {
            if (!bpDirty)
                return (false, new Dictionary<string, Dictionary<int, (string? condition, string? hit, string? log)>>());
            var snap = new Dictionary<string, Dictionary<int, (string? condition, string? hit, string? log)>>(desiredBps.Count, desiredBps.Comparer);
            foreach (var kv in desiredBps)
                snap[kv.Key] = new Dictionary<int, (string? condition, string? hit, string? log)>(kv.Value);
            bpDirty = false;
            return (true, snap);
        }
    }

    // Non-destructive read for a single chunk (used in RegisterPrototype)
    public Dictionary<int, (string? condition, string? hit, string? log)>? GetDesiredBreakpointsForChunk(string chunkName)
    {
        lock (locals)
        {
            // Normalize incoming chunk key
            var key = chunkName;
            if (!key.StartsWith("@")) key = "@" + key;
            key = key.Replace("\\", "/");

            if (desiredBps.TryGetValue(key, out var set))
                return new Dictionary<int, (string? condition, string? hit, string? log)>(set);

            // Fallback: match by filename (handles short chunk names like @test.lua or differing absolute paths)
            try
            {
                var fileOnly = System.IO.Path.GetFileName(key.TrimStart('@').Replace('/', System.IO.Path.DirectorySeparatorChar));
                foreach (var kv in desiredBps)
                {
                    var kFile = System.IO.Path.GetFileName(kv.Key.TrimStart('@').Replace('/', System.IO.Path.DirectorySeparatorChar));
                    if (string.Equals(fileOnly, kFile, StringComparison.OrdinalIgnoreCase))
                        return new Dictionary<int, (string? condition, string? hit, string? log)>(kv.Value);
                }
            }
            catch { }

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
        state.OpenStandardLibraries();
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
        // Try to adopt existing debugger (attach mode)
        if (MinimalDebugger.Active is not null)
        {
            debugger = MinimalDebugger.Active;
            return;
        }

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
            // Try to resolve to an absolute filesystem path using cwd and package.path
            var resolved = s?.ResolveSourcePath(file) ?? file;
            RpcServer.Publish("stopped", new { reason = publishReason, threadId = 1, file = resolved, line });
        }

        RpcServer.Publish("wait", new { reason = "started", threadId = 1 });
        toWait.Wait();
    }

    string? ResolveSourcePath(string chunk)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(chunk)) return chunk;
            chunk = chunk.Trim();
            // If already an absolute path and exists, return
            if (Path.IsPathRooted(chunk) && File.Exists(chunk)) return Path.GetFullPath(chunk);

            // Try CWD + chunk
            var cwd = Directory.GetCurrentDirectory();
            var direct = Path.Combine(cwd, chunk);
            if (File.Exists(direct)) return Path.GetFullPath(direct);

            // Derive candidate base names
            var withoutExt = Path.HasExtension(chunk) ? Path.ChangeExtension(chunk, null) ?? chunk : chunk;
            string modDots = withoutExt.Replace('\\', '.').Replace('/', '.');
            string modSlashes = modDots.Replace('.', Path.DirectorySeparatorChar);

            // Read package.path if available
            var patterns = new List<string>();
            try
            {
                if (state!.Environment["package"].TryRead<LuaTable>(out var pkg))
                {
                    var pathValue = pkg["path"].ToString();
                    if (!string.IsNullOrEmpty(pathValue))
                    {
                        patterns.AddRange(pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }
                }
            }
            catch { }

            if (patterns.Count == 0)
            {
                patterns.Add("?.lua");
                patterns.Add("?/init.lua");
            }

            foreach (var pat in patterns)
            {
                string Apply(string name)
                {
                    var replaced = pat.Replace("?", name);
                    if (!Path.IsPathRooted(replaced)) replaced = Path.Combine(cwd, replaced);
                    return replaced;
                }

                var c1 = Apply(modDots.Replace('.', Path.DirectorySeparatorChar));
                if (File.Exists(c1)) return Path.GetFullPath(c1);

                var c2 = Apply(modSlashes);
                if (File.Exists(c2)) return Path.GetFullPath(c2);

                var c3 = Apply(withoutExt);
                if (File.Exists(c3)) return Path.GetFullPath(c3);
            }

            return chunk;
        }
        catch
        {
            return chunk;
        }
    }

    public void UpdateStoppedContext(LuaState thread, int pc, LuaClosure closure)
    {
        lock (locals)
        {
            lastThread = thread;
            // Adopt the running state for globals snapshot in attach scenarios
            this.state = thread;
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

            // Snapshot upvalues for current closure
            upvalues.Clear();
            try
            {
                var desc = closure.Proto.UpValues;
                var values = closure.UpValues;
                var count = Math.Min(desc.Length, values.Length);
                for (int i = 0; i < count; i++)
                {
                    string name;
                    try { name = desc[i].Name.ToString(); }
                    catch { name = $"upvalue_{i}"; }

                    if (string.IsNullOrWhiteSpace(name)) name = $"upvalue_{i}";
                    string value;
                    try { value = values[i].GetValue().ToString(); }
                    catch { value = string.Empty; }

                    upvalues.Add((name.Trim(), value));
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

    public object[] GetUpvalues()
    {
        lock (locals)
        {
            return upvalues.Select(v => new { name = v.name, value = v.value }).Cast<object>().ToArray();
        }
    }

    public (bool ok, string? value) SetLocal(string name, string valueText)
    {
        lock (locals)
        {
            if (state is null || lastProto is null) return (false, null);
            var thread = state;
            var f = thread.GetCurrentFrame();
            var baseIndex = f.Base;
            var stack = thread.Stack.AsSpan();
            var pc = lastPc;

            for (int i = 0; i < lastProto.MaxStackSize && (baseIndex + i) < stack.Length; i++)
            {
                var n = DebugUtility.GetLocalVariableName(lastProto, i, pc);
                if (string.IsNullOrWhiteSpace(n)) continue;
                n = n.Trim();
                if (string.Equals(n, name, StringComparison.Ordinal))
                {
                    if (!TryParseLuaValue(valueText, out var v)) return (false, null);
                    stack[baseIndex + i] = v;
                    string s;
                    try { s = stack[baseIndex + i].ToString(); }
                    catch { s = valueText; }

                    return (true, s);
                }
            }

            return (false, null);
        }
    }

    public (bool ok, string? value) SetUpvalue(string name, string valueText)
    {
        lock (locals)
        {
            if (state is null) return (false, null);
            var thread = state;
            var f = thread.GetCurrentFrame();
            if (f.Function is not LuaClosure clo) return (false, null);

            var desc = clo.Proto.UpValues;
            var values = clo.UpValues;
            var count = Math.Min(desc.Length, values.Length);
            for (int i = 0; i < count; i++)
            {
                string n;
                try { n = desc[i].Name.ToString(); }
                catch { n = $"upvalue_{i}"; }

                if (string.IsNullOrWhiteSpace(n)) n = $"upvalue_{i}";
                n = n.Trim();
                if (string.Equals(n, name, StringComparison.Ordinal))
                {
                    if (!TryParseLuaValue(valueText, out var v)) return (false, null);
                    try { values[i].SetValue(v); }
                    catch { return (false, null); }

                    string s;
                    try { s = values[i].GetValue().ToString(); }
                    catch { s = valueText; }

                    return (true, s);
                }
            }

            return (false, null);
        }
    }

    static bool TryParseLuaValue(string text, out LuaValue value)
    {
        text = text ?? string.Empty;
        // bool
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        // number
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            value = d;
            return true;
        }

        // string (strip optional quotes)
        if ((text.StartsWith("\"") && text.EndsWith("\"")) || (text.StartsWith("'") && text.EndsWith("'")))
        {
            text = text.Substring(1, Math.Max(0, text.Length - 2));
        }

        value = text;
        return true;
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

                // Enrich with child prototype index for Closure opcodes
                if (instruction.OpCode == OpCode.Closure)
                {
                    int childIndex = instruction.Bx;
                    instructions.Add(new { index = i, line, text, childIndex });
                }
                else
                {
                    instructions.Add(new { index = i, line, text });
                }
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
        if (debugger is null || lastProto is null) return;
        stepDepth = lastDepth;
        debugger.StartStepIn(lastProto, lastPc);
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

    public object[] GetLocalsForFrame(int frameId)
    {
        var ctx = GetFrameContext(frameId);
        if (ctx.thread is null || ctx.proto is null || ctx.index < 0) return Array.Empty<object>();
        var list = new List<object>();
        try
        {
            var f = ctx.thread.GetCallStackFrames()[ctx.index];
            var stack = ctx.thread.Stack.AsSpan();
            int baseIndex = f.Base;
            var unique = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ctx.proto.MaxStackSize && (baseIndex + i) < stack.Length; i++)
            {
                var name = DebugUtility.GetLocalVariableName(ctx.proto, i, ctx.pc);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    name = name.Trim();
                    if (name.Length == 0 || !unique.Add(name)) continue;
                    string value; try { value = stack[baseIndex + i].ToString(); } catch { value = string.Empty; }
                    list.Add(new { name, value });
                }
            }
        }
        catch { }
        return list.ToArray();
    }

    public object[] GetUpvaluesForFrame(int frameId)
    {
        var ctx = GetFrameContext(frameId);
        if (ctx.closure is null) return Array.Empty<object>();
        var list = new List<object>();
        try
        {
            var desc = ctx.closure.Proto.UpValues;
            var values = ctx.closure.UpValues;
            var count = Math.Min(desc.Length, values.Length);
            for (int i = 0; i < count; i++)
            {
                string name; try { name = desc[i].Name.ToString(); } catch { name = $"upvalue_{i}"; }
                if (string.IsNullOrWhiteSpace(name)) name = $"upvalue_{i}";
                string value; try { value = values[i].GetValue().ToString(); } catch { value = string.Empty; }
                list.Add(new { name = name.Trim(), value });
            }
        }
        catch { }
        return list.ToArray();
    }

    public object? GetBytecodeSnapshotForFrame(int frameId)
    {
        var ctx = GetFrameContext(frameId);
        if (ctx.proto is null) return null;
        var proto = ctx.proto;
        var code = proto.Code;
        var lines = proto.LineInfo;
        var instructions = new List<object>(code.Length);
        for (int i = 0; i < code.Length; i++)
        {
            int line = (i >= 0 && i < lines.Length) ? lines[i] : 0;
            var instruction = code[i];
            if (code[i].OpCode == (OpCode)40 && debugger is not null)
            {
                if (debugger.TryGetPatchedOriginal(proto, i, out var original, out _))
                {
                    instruction = original;
                }
            }
            string text; try { text = instruction.ToString(); } catch { text = instruction.Value.ToString(); }
            if (instruction.OpCode == OpCode.Closure)
            {
                int childIndex = instruction.Bx;
                instructions.Add(new { index = i, line, text, childIndex });
            }
            else
            {
                instructions.Add(new { index = i, line, text });
            }
        }

        var constants = new List<string>();
        try { foreach (var k in proto.Constants) { string v; try { v = k.ToString(); } catch { v = string.Empty; } constants.Add(v); } }
        catch { }
        var localsMeta = new List<object>();
        try { foreach (var lv in proto.LocalVariables) localsMeta.Add(new { lv.Name, lv.StartPc, lv.EndPc }); }
        catch { }
        var upvaluesMeta = new List<object>();
        try { foreach (var uv in proto.UpValues) upvaluesMeta.Add(new { uv.Name, uv.IsLocal, uv.Index }); }
        catch { }
        string chunk = proto.ChunkName.TrimStart('@');
        return new { chunk, pc = ctx.pc, instructions = instructions.ToArray(), constants = constants.ToArray(), locals = localsMeta.ToArray(), upvalues = upvaluesMeta.ToArray() };
    }

    object SnapshotForPrototype(Prototype proto, int pc)
    {
        var code = proto.Code;
        var lines = proto.LineInfo;
        var instructions = new List<object>(code.Length);
        for (int i = 0; i < code.Length; i++)
        {
            int line = (i >= 0 && i < lines.Length) ? lines[i] : 0;
            var instruction = code[i];
            if (code[i].OpCode == (OpCode)40 && debugger is not null)
            {
                if (debugger.TryGetPatchedOriginal(proto, i, out var original, out _))
                {
                    instruction = original;
                }
            }
            string text; try { text = instruction.ToString(); } catch { text = instruction.Value.ToString(); }
            if (instruction.OpCode == OpCode.Closure)
            {
                int childIndex = instruction.Bx;
                instructions.Add(new { index = i, line, text, childIndex });
            }
            else
            {
                instructions.Add(new { index = i, line, text });
            }
        }

        var constants = new List<string>();
        try { foreach (var k in proto.Constants) { string v; try { v = k.ToString(); } catch { v = string.Empty; } constants.Add(v); } }
        catch { }
        var localsMeta = new List<object>();
        try { foreach (var lv in proto.LocalVariables) localsMeta.Add(new { lv.Name, lv.StartPc, lv.EndPc }); }
        catch { }
        var upvaluesMeta = new List<object>();
        try { foreach (var uv in proto.UpValues) upvaluesMeta.Add(new { uv.Name, uv.IsLocal, uv.Index }); }
        catch { }
        string chunk = proto.ChunkName.TrimStart('@');
        return new { chunk, pc, instructions = instructions.ToArray(), constants = constants.ToArray(), locals = localsMeta.ToArray(), upvalues = upvaluesMeta.ToArray() };
    }

    (LuaState? thread, Prototype? proto, LuaClosure? closure, int pc, int index) GetFrameContext(int frameId)
    {
        var t = lastThread;
        if (t is null) return (null, null, null, 0, -1);
        try
        {
            var frames = t.GetCallStackFrames();
            int n = frames.Length;
            int targetId = Math.Max(1, frameId);
            int i = n - targetId;
            if (i < 0 || i >= n) i = n - 1;
            var f = frames[i];
            LuaClosure? clo = f.Function as LuaClosure;
            Prototype? p = clo?.Proto;
            int pc = 0;
            if (i == n - 1) pc = Math.Max(0, lastPc);
            else pc = Math.Max(0, frames[i + 1].CallerInstructionIndex);
            return (t, p, clo, pc, i);
        }
        catch { return (t, null, null, 0, -1); }
    }

    public object[] GetCallStack()
    {
        lock (locals)
        {
            var list = new List<object>();
            try
            {
                if (lastThread is not null)
                {
                    var frames = lastThread.GetCallStackFrames();
                    int n = frames.Length;
                    for (int i = n - 1, id = 1; i >= 0; i--, id++)
                    {
                        var f = frames[i];
                        if (f.Function is LuaClosure clo)
                        {
                            var p = clo.Proto;
                            int pc = 0;
                            if (i == n - 1) pc = Math.Max(0, lastPc);
                            else pc = Math.Max(0, frames[i + 1].CallerInstructionIndex);
                            int line = (pc >= 0 && pc < p.LineInfo.Length) ? p.LineInfo[pc] : 1;
                            var file = p.ChunkName.TrimStart('@');
                            file = ResolveSourcePath(file) ?? file;
                            var name = System.IO.Path.GetFileName(file);
                            list.Add(new { id, name, file, line });
                        }
                    }
                }

                if (list.Count == 0 && lastProto is not null)
                {
                    var file = lastProto.ChunkName.TrimStart('@');
                    file = ResolveSourcePath(file) ?? file;
                    var name = System.IO.Path.GetFileName(file);
                    list.Add(new { id = 1, name, file, line = Math.Max(1, lastPc < lastProto.LineInfo.Length ? lastProto.LineInfo[lastPc] : 1) });
                }
            }
            catch
            {
                /* ignore */
            }

            return list.ToArray();
        }
    }

    public object? FindPrototypeBytecode(string file, int line)
    {
        EnsureDebugger();
        if (debugger is null) return null;
        try
        {
            var res = debugger.FindPrototypeBySource(file, line);
            if (res.proto is null) return null;
            return SnapshotForPrototype(res.proto, res.pc);
        }
        catch { return null; }
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
