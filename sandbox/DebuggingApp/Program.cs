using Lua;
using Lua.Debugging;
using Lua.Runtime;
using Lua.Standard;
using System.Runtime.CompilerServices;

Directory.SetCurrentDirectory(ThisPath());
var state = LuaState.Create();
state.OpenBasicLibrary();
var debugger = new SimpleDebugger();
state.Debugger = debugger;

var fileName = "test.lua";
debugger.SetBreakPointAtLine(fileName, 4);

var p = await state.LoadFileAsync(fileName, "bt", null, default);

debugger.SetBreakPointAtLine(fileName, 1);
debugger.SetBreakPointAtLine(fileName, 5);

await state.ExecuteAsync(p);

static string ThisPath([CallerFilePath] string callerFilePath = "")
{
    return Path.GetDirectoryName(callerFilePath)!;
}

class SimpleDebugger : IDebugger
{
    Dictionary<(Prototype, int), Instruction> breakpoints = new();
    Dictionary<string, List<int>> bp = new();
    Dictionary<string, Prototype> protos = new();

    public void RegisterPrototype(Prototype proto)
    {
        if (!proto.ChunkName.StartsWith('@')) return;
        if (bp.TryGetValue(proto.ChunkName, out var list))
        {
            foreach (var line in list)
            {
                SetBreakPointAtLine(proto, line);
            }
        }

        protos[proto.ChunkName] = proto;
    }


    public void PatchAll(Prototype proto)
    {
        for (int i = 0; i < proto.Code.Length; i++)
        {
            SetBreakpoint(proto, i);
        }
    }

    public void SetBreakPointAtLine(string chunkName, int line)
    {
        if (!chunkName.StartsWith('@'))
        {
            chunkName = "@" + chunkName;
        }

        if (bp.TryGetValue(chunkName, out var list))
        {
            if (!list.Contains(line))
                list.Add(line);
        }
        else
        {
            bp[chunkName] = new List<int> { line };
        }

        if (protos.TryGetValue(chunkName, out var proto))
        {
            SetBreakPointAtLine(proto, line);
        }
    }

    public void DeleteBreakPointAtLine(string chunkName, int line)
    {
        if (bp.TryGetValue(chunkName, out var list))
        {
            if (list.Contains(line))
                list.Remove(line);
        }

        if (protos.TryGetValue(chunkName, out var proto))
        {
            for (int i = 0; i < proto.LineInfo.Length; i++)
            {
                if (proto.LineInfo[i] == line)
                {
                    DeleteBreakpoint(proto, i);
                    return;
                }
            }
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
    }

    public void SetBreakpoint(Prototype proto, int instructionIndex)
    {
        if (instructionIndex < 0 || instructionIndex >= proto.Code.Length)
            throw new ArgumentOutOfRangeException(nameof(instructionIndex));

        var key = (proto, instructionIndex);
        if (!breakpoints.ContainsKey(key))
        {
            var oldInstruction = DebugUtility.PatchDebugInstruction(proto, instructionIndex);
            breakpoints[key] = oldInstruction;
        }
    }

    public void DeleteBreakpoint(Prototype proto, int instructionIndex)
    {
        if (instructionIndex < 0 || instructionIndex >= proto.Code.Length)
            throw new ArgumentOutOfRangeException(nameof(instructionIndex));

        var key = (proto, instructionIndex);
        if (!breakpoints.ContainsKey(key))
        {
            throw new InvalidOperationException("No breakpoint set at this location.");
        }
        else
        {
            var oldInstruction = breakpoints[key];
            DebugUtility.PatchInstruction(proto, instructionIndex, oldInstruction);
            breakpoints.Remove(key);
        }
    }

    public Instruction HandleDebugBreak(LuaState thread, int pc, LuaClosure closure)
    {
        var proto = closure.Proto;
        var key = (proto, pc);
        if (breakpoints.TryGetValue(key, out var oldInstruction))
        {
            Console.WriteLine($"Breakpoint hit at {proto.ChunkName}:{proto.LineInfo[pc]} (PC={pc}) {oldInstruction}");
            var f = thread.GetCurrentFrame();
            Console.WriteLine($"Base Info: Base={f.Base}");
            Console.WriteLine("Local Variables:");
            var stack = thread.Stack.AsSpan()[f.Base..];
            for (int i = 0; i < proto.MaxStackSize && i < stack.Length; i++)
            {
                Console.WriteLine($"  R{i}: {stack[i]} ;{DebugUtility.GetLocalVariableName(proto, i, pc)}");
            }

            // Here you can implement more interactive debugging features
            // For simplicity, we just wait for user input to continue
            Console.WriteLine("Press Enter to continue...");
            var s = Console.ReadLine();

            return oldInstruction;
        }

        throw new InvalidOperationException("No breakpoint set at this location.");
    }
}