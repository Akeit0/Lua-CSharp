using System.Text.RegularExpressions;
using Lua;
using Lua.Standard;

// Minimal debug host scaffold. For now, supports running a script with optional
// line breakpoints and an interactive pause on hit. JSON-RPC will be added later.

var (script, cwd, breaks, stdio) = ParseArgs(args);
if (!string.IsNullOrEmpty(cwd))
{
    Directory.SetCurrentDirectory(cwd);
}

Console.Error.WriteLine($"[Lua.DebugServer] Starting (cwd={Directory.GetCurrentDirectory()})");

if (stdio)
{
    await RpcServer.RunAsync();
}
else if (!string.IsNullOrEmpty(script))
{
    await RunScriptAsync(script, breaks);
}
else
{
    Console.Error.WriteLine("[Lua.DebugServer] No --script provided. Idle.");
    // Idle loop to keep the process alive if someone wants to connect later
    await Task.Delay(-1);
}

static async Task RunScriptAsync(string file, List<(string file, int line)> breaks)
{
    var state = LuaState.Create();
    state.OpenBasicLibrary();
    var debugger = new MinimalDebugger();
    state.Debugger = debugger;

    foreach (var (f, l) in breaks)
    {
        debugger.SetBreakPointAtLine(f, l);
    }

    var p = await state.LoadFileAsync(file, "bt", null, default);
    await state.ExecuteAsync(p);

    Console.Error.WriteLine("[Lua.DebugServer] Execution finished.");
}

static (string? script, string? cwd, List<(string file, int line)> breaks, bool stdio) ParseArgs(string[] args)
{
    string? script = null;
    string? cwd = null;
    var breaks = new List<(string file, int line)>();
    bool stdio = false;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--script":
                script = i + 1 < args.Length ? args[++i] : script;
                break;
            case "--cwd":
                cwd = i + 1 < args.Length ? args[++i] : cwd;
                break;
            case "--break":
                if (i + 1 < args.Length)
                {
                    var bp = args[++i];
                    var m = Regex.Match(bp, "^(.*):(\\d+)$");
                    if (m.Success && int.TryParse(m.Groups[2].Value, out var line))
                    {
                        breaks.Add((m.Groups[1].Value, line));
                    }
                }
                break;
            case "--stdio":
                stdio = true;
                break;
        }
    }

    return (script, cwd, breaks, stdio);
}