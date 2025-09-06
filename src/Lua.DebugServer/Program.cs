using System.Text.RegularExpressions;
using Lua;
using Lua.Standard;

// Minimal debug host scaffold. For now, supports running a script with optional
// line breakpoints and an interactive pause on hit. JSON-RPC will be added later.

var (script, cwd, breaks, stdio, tcpHost, tcpPort) = ParseArgs(args);
if (!string.IsNullOrEmpty(cwd))
{
    Directory.SetCurrentDirectory(Path.GetFullPath(cwd));
}

Console.Error.WriteLine($"[Lua.DebugServer] Starting (cwd={Directory.GetCurrentDirectory()})");

if (stdio)
{
    Console.Error.WriteLine("[Lua.DebugServer] Listening on stdio");
    await RpcServer.RunAsync();
}
else if (!string.IsNullOrEmpty(script) && !string.IsNullOrEmpty(tcpHost) && tcpPort > 0)
{
    Console.Error.WriteLine($"[Lua.DebugServer] Listening on TCP {tcpHost}:{tcpPort}");
    await RpcServer.RunTcpAsync(script!, tcpHost!, tcpPort);
}
else if (!string.IsNullOrEmpty(script))
{
    Console.Error.WriteLine($"[Lua.DebugServer] Running script {script}");
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

static (string? script, string? cwd, List<(string file, int line)> breaks, bool stdio, string? tcpHost, int tcpPort) ParseArgs(string[] args)
{
    string? script = null;
    string? cwd = null;
    var breaks = new List<(string file, int line)>();
    bool stdio = false;
    string? tcpHost = null;
    int tcpPort = 0;

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
            case "--tcp":
                // default host:port if provided as next arg "host:port" or only port number
                if (i + 1 < args.Length)
                {
                    var next = args[i + 1];
                    if (next.Contains(':'))
                    {
                        var parts = next.Split(':');
                        if (parts.Length >= 2 && int.TryParse(parts[^1], out var p))
                        {
                            tcpHost = string.Join(':', parts, 0, parts.Length - 1);
                            tcpPort = p;
                            i++;
                        }
                    }
                }

                if (tcpPort == 0)
                {
                    tcpHost = "0.0.0.0";
                    tcpPort = 4711;
                }

                break;
            case "--host":
                tcpHost = i + 1 < args.Length ? args[++i] : tcpHost;
                break;
            case "--port":
                if (i + 1 < args.Length && int.TryParse(args[++i], out var port)) tcpPort = port;
                break;
        }
    }

    return (script, cwd, breaks, stdio, tcpHost, tcpPort);
}