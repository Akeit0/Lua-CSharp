using System.Diagnostics;
using Lua.CodeAnalysis.Syntax;
using Lua.CodeAnalysis.Compilation;
using Lua.Runtime;
using Lua;
using Lua.Standard;
var state = LuaState.Create();
state.OpenStandardLibraries();

state.Environment["vec3"] = new LVec3();

var debug = new LuaTable();
debug["traceback"] = new LuaFunction("logtrace", (context, mem, ct) =>
{
    
    mem.Span[0] = context.State.GetTraceback().ToString();
    return new ValueTask<int>(1);
});
state.Environment["debug"] = debug;
try
{
    var source = File.ReadAllText(FileHelper.GetAbsolutePath("test.lua"));


    var syntaxTree = LuaSyntaxTree.Parse(source, "test.lua");

    Console.WriteLine("Source Code " + new string('-', 50));

    var debugger = new DisplayStringSyntaxVisitor();
    Console.WriteLine(debugger.GetDisplayString(syntaxTree));

    var chunk = LuaCompiler.Default.Compile(syntaxTree, "test.lua");

    DebugChunk(chunk, 0);

    Console.WriteLine("Output " + new string('-', 50));

    var results = new LuaValue[64];

    //for (int i = 0; i < 100; i++)
    //{
    //var timer = Stopwatch.GetTimestamp();
    var resultCount = await state.RunAsync(chunk, results);
    // Console.WriteLine($"Elapsed: {(Stopwatch.GetTimestamp() - timer) / (double)Stopwatch.Frequency} seconds");
    // }


    Console.WriteLine("Result " + new string('-', 50));

    for (int i = 0; i < resultCount; i++)
    {
        Console.WriteLine(results[i]);
    }

    Console.WriteLine("End " + new string('-', 50));
    using NLua.Lua nLuaState = new();
//
    foreach (var o in nLuaState.DoString(source))
    {
        Console.WriteLine(o);
    }

    Console.WriteLine("NLua Result " + new string('-', 50));
}
catch (Exception ex)
{
    Console.WriteLine(ex);
}

static void DebugChunk(Chunk chunk, int id)
{
    Console.WriteLine($"Chunk[{id}] {chunk.Name}" + new string('=', 50));
    Console.WriteLine($"Parameters:{chunk.ParameterCount}");

    Console.WriteLine("Instructions " + new string('-', 50));
    var index = 0;
    foreach (var inst in chunk.Instructions.ToArray())
    {
        Console.WriteLine($"[{index}]\t{chunk.SourcePositions[index]}\t\t{inst}");
        index++;
    }

    Console.WriteLine("Constants " + new string('-', 50));
    index = 0;
    foreach (var constant in chunk.Constants.ToArray())
    {
        Console.WriteLine($"[{index}]\t{constant}");
        index++;
    }

    Console.WriteLine("UpValues " + new string('-', 50));
    index = 0;
    foreach (var upValue in chunk.UpValues)
    {
        Console.WriteLine($"[{index}]\t{upValue.Name}\t{(upValue.IsInRegister ? 1 : 0)}\t{upValue.Index}");
        index++;
    }

    Console.WriteLine();

    var nestedChunkId = 0;
    foreach (var localChunk in chunk.Functions)
    {
        DebugChunk(localChunk, nestedChunkId);
        nestedChunkId++;
    }
}

public class Box<T>(T value)
{
    public T Value = value;
    public static implicit operator T(Box<T> box) => box.Value;
}