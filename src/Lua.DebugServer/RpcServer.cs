using System.Text.Json;
using System.Text.Json.Serialization;

static class RpcServer
{
    static readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false };

    static readonly object writeLock = new();

    public static async Task RunAsync()
    {
        // Send an initial output event so the client knows we're alive
        WriteLogToConsole("[Lua.DebugServer] ready");

        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            //WriteToConsole( $"=> {line}");
            try
            {
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var method = root.GetProperty("method").GetString();
                var @params = root.TryGetProperty("params", out var p) ? p : default;
                if(method is "next" or "continue" or "stepIn" or "stepOut")WriteLogToConsole($"<= {method}");

                switch (method)
                {
                    case "ping":
                        WriteResponse(id, new { message = "pong" });
                        break;
                    case "initialize":
                        if (LuaDebugSession.Current is null)
                        {
                            LuaDebugSession.Current = new LuaDebugSession();
                        }

                        WriteEvent("initialized", new { });
                        WriteResponse(id, new { capabilities = new { } });
                        break;
                    case "setBreakpoints":
                        HandleSetBreakpoints(id, @params);
                        break;
                    case "launch":
                        await HandleLaunchAsync(id, @params);
                        break;
                    case "continue":
                        HandleContinue(id);
                        break;
                    case "next":
                        HandleNext(id);
                        break;
                    case "stepIn":
                        HandleStepIn(id);
                        break;
                    case "stepOut":
                        HandleStepOut(id);
                        break;
                    case "getLocals":
                        HandleGetLocals(id);
                        break;
                    case "getGlobals":
                        HandleGetGlobals(id);
                        break;
                    case "terminate":
                        WriteResponse(id, new { });
                        return;
                    default:
                        WriteResponse(id, error: new { message = $"Unknown method: {method}" });
                        break;
                }
            }
            catch (Exception ex)
            {
                WriteEvent("output", new { category = "stderr", output = ex + "\n" });
            }
        }
    }

    public static void Publish(string ev, object body) => WriteEvent(ev, body);

    static void WriteResponse(string? id, object? result = null, object? error = null)
    {
        var payload = new { type = "response", id, result, error };
        Write(payload);
    }

    static void WriteEvent(string ev, object body)
    {
        var payload = new { type = "event", @event = ev, body };
        Write(payload);
    }

    public static void WriteToConsole(string text,string category="console")
    {
        WriteEvent("output", new { category = category, output = text + "\n" });
    }
    
    public static void WriteLogToConsole(string text)
    {
        WriteEvent("output", new { category = "important", output = text + "\n" });
    }

    static void Write(object payload)
    {
        var json = JsonSerializer.Serialize(payload, options);
        lock (writeLock)
        {
            Console.Out.WriteLine(json);
            Console.Out.Flush();
        }
    }

    // Handlers
    static void HandleSetBreakpoints(string? id, JsonElement @params)
    {
        var source = @params.GetProperty("source").GetString() ?? string.Empty;
        var lines = new List<int>();
        if (@params.TryGetProperty("lines", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
                if (el.TryGetInt32(out var l))
                    lines.Add(l);
        }

        if (LuaDebugSession.Current is null)
        {
            LuaDebugSession.Current = new LuaDebugSession();
        }

        LuaDebugSession.Current.SetBreakpoints(source, lines);
        WriteResponse(id, new { breakpoints = lines.Select(l => new { verified = true, line = l }).ToArray() });
    }

    static async Task HandleLaunchAsync(string? id, JsonElement @params)
    {
        var program = @params.GetProperty("program").GetString() ?? string.Empty;
        var cwd = @params.TryGetProperty("cwd", out var cEl) ? cEl.GetString() : null;
        var stopOnEntry = @params.TryGetProperty("stopOnEntry", out var sEl) && sEl.GetBoolean();

        if (LuaDebugSession.Current is null)
        {
            LuaDebugSession.Current = new LuaDebugSession();
        }

        await LuaDebugSession.Current.LaunchAsync(program, cwd, stopOnEntry);
        WriteResponse(id, new { });
    }

    static void HandleContinue(string? id)
    {
        LuaDebugSession.Current?.Continue(true);
        WriteResponse(id, new { allThreadsContinued = true });
    }

    static void HandleGetLocals(string? id)
    {
        var locals = LuaDebugSession.Current?.GetLocals() ?? Array.Empty<object>();
        WriteResponse(id, new { variables = locals });
    }

    static void HandleNext(string? id)
    {
        var session = LuaDebugSession.Current;
        if (session is null) return;
        session.StepNext();
        session.Continue();
        WriteResponse(id, new { });
    }

    static void HandleStepIn(string? id)
    {
        var session = LuaDebugSession.Current;
        if (session is null) return;
        session.StepIn();
        session.Continue();
        WriteResponse(id, new { });
    }

    static void HandleStepOut(string? id)
    {
        var session = LuaDebugSession.Current;
        if (session is null) return;
        session.StepOut();
        session.Continue();
        WriteResponse(id, new { });
    }

    static void HandleGetGlobals(string? id)
    {
        var globals = LuaDebugSession.Current?.GetGlobals() ?? Array.Empty<object>();
        WriteResponse(id, new { variables = globals });
    }
}