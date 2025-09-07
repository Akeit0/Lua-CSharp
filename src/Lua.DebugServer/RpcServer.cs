using System.Text.Json;
using System.Text.Json.Serialization;

static class RpcServer
{
    static readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false };

    static readonly object writeLock = new();
    static TextReader input = Console.In;
    static TextWriter output = Console.Out;

    public static void UseIO(TextReader reader, TextWriter writer)
    {
        input = reader;
        output = writer;
    }

    public static async Task RunAsync()
    {
        // Send an initial output event so the client knows we're alive
        WriteLogToConsole("[Lua.DebugServer] ready");

        string? line;
        while ((line = await input.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var method = root.GetProperty("method").GetString();
                var @params = root.TryGetProperty("params", out var p) ? p : default;

                switch (method)
                {
                    case "ping":
                        WriteResponse(id, new { message = "pong" });
                        break;
                    case "initialize":
                        LuaDebugSession.Current ??= new();

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
                        HandleGetLocals(id, @params);
                        break;
                    case "getGlobals":
                        HandleGetGlobals(id);
                        break;
                    case "getUpvalues":
                        HandleGetUpvalues(id, @params);
                        break;
                    case "setLocal":
                        HandleSetLocal(id, @params);
                        break;
                    case "setUpvalue":
                        HandleSetUpvalue(id, @params);
                        break;
                    case "getInstrBreakpoints":
                        HandleGetInstrBreakpoints(id, @params);
                        break;
                    case "setInstrBreakpoint":
                        HandleSetInstrBreakpoint(id, @params);
                        break;
                    case "getBytecode":
                        HandleGetBytecode(id, @params);
                        break;
                    case "getStack":
                        HandleGetStack(id);
                        break;
                    case "findPrototype":
                        HandleFindPrototype(id, @params);
                        break;
                    case "getOptions":
                        HandleGetOptions(id);
                        break;
                    case "setStepOverMode":
                        HandleSetStepOverMode(id, @params);
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

    public static void WriteToConsole(string text, string category = "console")
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
            output.WriteLine(json);
            output.Flush();
        }
    }

    public static async Task RunTcpAsync(string program, string host, int port, CancellationToken cancellationToken = default)
    {
        var ip = System.Net.IPAddress.TryParse(host, out var parsed) ? parsed : System.Net.IPAddress.Any;
        var listener = new System.Net.Sockets.TcpListener(ip, port);

        listener.Start();
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
            UseIO(reader, writer);

            await RunAsync();
        }
        finally
        {
            listener.Stop();
        }
    }

    // Handlers
    static void HandleSetBreakpoints(string? id, JsonElement @params)
    {
        var source = @params.GetProperty("source").GetString() ?? string.Empty;
        var list = new List<(int line, string? condition, string? hitCondition, string? logMessage)>();
        if (@params.TryGetProperty("breakpoints", out var bpsArr))
        {
            foreach (var el in bpsArr.EnumerateArray())
            {
                var line = el.GetProperty("line").GetInt32();
                string? cond = null;
                if (el.TryGetProperty("condition", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                    cond = cEl.GetString();
                string? hit = null;
                if (el.TryGetProperty("hitCondition", out var hEl) && hEl.ValueKind == JsonValueKind.String)
                    hit = hEl.GetString();
                string? log = null;
                if (el.TryGetProperty("logMessage", out var lEl) && lEl.ValueKind == JsonValueKind.String)
                    log = lEl.GetString();
                list.Add((line, cond, hit, log));
            }
        }
        else if (@params.TryGetProperty("lines", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
                if (el.TryGetInt32(out var l))
                    list.Add((l, null, null, null));
        }

        LuaDebugSession.Current ??= new LuaDebugSession();

        LuaDebugSession.Current.SetBreakpoints(source, list);
        WriteResponse(id, new { breakpoints = list.Select(l => new { verified = true, line = l.line }).ToArray() });
    }

    static async Task HandleLaunchAsync(string? id, JsonElement @params)
    {
        var program = @params.GetProperty("program").GetString() ?? string.Empty;
        var cwd = @params.TryGetProperty("cwd", out var cEl) ? cEl.GetString() : null;
        var stopOnEntry = @params.TryGetProperty("stopOnEntry", out var sEl) && sEl.GetBoolean();

        LuaDebugSession.Current ??= new LuaDebugSession();

        await LuaDebugSession.Current.LaunchAsync(program, cwd, stopOnEntry);
        WriteResponse(id, new { });
    }

    static void HandleContinue(string? id)
    {
        LuaDebugSession.Current?.Continue(true);
        WriteResponse(id, new { allThreadsContinued = true });
    }

    static void HandleGetLocals(string? id, JsonElement @params)
    {
        object[] locals;
        if (@params.ValueKind != JsonValueKind.Undefined && @params.TryGetProperty("frameId", out var fEl) && fEl.TryGetInt32(out var fid))
            locals = LuaDebugSession.Current?.GetLocalsForFrame(fid) ?? Array.Empty<object>();
        else
            locals = LuaDebugSession.Current?.GetLocals() ?? Array.Empty<object>();
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

    static void HandleGetOptions(string? id)
    {
        var mode = MinimalDebugger.Active?.GetStepOverMode().ToString() ?? "Line";
        WriteResponse(id, new { stepOverMode = mode });
    }

    static void HandleSetStepOverMode(string? id, JsonElement @params)
    {
        var text = @params.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? (m.GetString() ?? "Line") : "Line";
        StepOverMode mode;
        if (!Enum.TryParse<StepOverMode>(text, ignoreCase: true, out mode)) mode = StepOverMode.Line;
        MinimalDebugger.Active?.SetStepOverMode(mode);
        WriteResponse(id, new { stepOverMode = mode.ToString() });
    }

    static void HandleGetUpvalues(string? id, JsonElement @params)
    {
        object[] upvalues;
        if (@params.ValueKind != JsonValueKind.Undefined && @params.TryGetProperty("frameId", out var fEl) && fEl.TryGetInt32(out var fid))
            upvalues = LuaDebugSession.Current?.GetUpvaluesForFrame(fid) ?? Array.Empty<object>();
        else
            upvalues = LuaDebugSession.Current?.GetUpvalues() ?? Array.Empty<object>();
        WriteResponse(id, new { variables = upvalues });
    }

    static void HandleSetLocal(string? id, JsonElement @params)
    {
        var name = @params.GetProperty("name").GetString() ?? string.Empty;
        var value = @params.GetProperty("value").GetString() ?? string.Empty;
        var res = LuaDebugSession.Current?.SetLocal(name, value) ?? (false, null);
        if (!res.ok || res.value is null)
        {
            WriteResponse(id, error: new { message = $"failed to set local '{name}'" });
        }
        else
        {
            WriteResponse(id, new { value = res.value });
        }
    }

    static void HandleSetUpvalue(string? id, JsonElement @params)
    {
        var name = @params.GetProperty("name").GetString() ?? string.Empty;
        var value = @params.GetProperty("value").GetString() ?? string.Empty;
        var res = LuaDebugSession.Current?.SetUpvalue(name, value) ?? (false, null);
        if (!res.ok || res.value is null)
        {
            WriteResponse(id, error: new { message = $"failed to set upvalue '{name}'" });
        }
        else
        {
            WriteResponse(id, new { value = res.value });
        }
    }

    static void HandleGetBytecode(string? id, JsonElement @params)
    {
        object? result;
        if (@params.ValueKind != JsonValueKind.Undefined && @params.TryGetProperty("frameId", out var fEl) && fEl.TryGetInt32(out var fid))
            result = LuaDebugSession.Current?.GetBytecodeSnapshotForFrame(fid);
        else
            result = LuaDebugSession.Current?.GetBytecodeSnapshot();
        if (result is null)
        {
            WriteResponse(id, error: new { message = "no paused location" });
            return;
        }

        WriteResponse(id, result);
    }

    static void HandleGetStack(string? id)
    {
        var frames = LuaDebugSession.Current?.GetCallStack() ?? Array.Empty<object>();
        WriteResponse(id, new { frames });
    }

    static void HandleGetInstrBreakpoints(string? id, JsonElement @params)
    {
        var chunk = @params.GetProperty("chunk").GetString() ?? string.Empty;
        var bps = LuaDebugSession.Current?.GetInstructionBreakpoints(chunk) ?? Array.Empty<int>();
        WriteResponse(id, new { breakpoints = bps });
    }

    static void HandleSetInstrBreakpoint(string? id, JsonElement @params)
    {
        var chunk = @params.GetProperty("chunk").GetString() ?? string.Empty;
        var index = @params.GetProperty("index").GetInt32();
        var enabled = @params.TryGetProperty("enabled", out var e) && e.GetBoolean();
        LuaDebugSession.Current?.SetInstructionBreakpoint(chunk, index, enabled);
        WriteResponse(id, new { });
    }

    static void HandleFindPrototype(string? id, JsonElement @params)
    {
        if (!@params.TryGetProperty("file", out var f) || f.ValueKind != JsonValueKind.String)
        {
            WriteResponse(id, error: new { message = "missing file" });
            return;
        }

        if (!@params.TryGetProperty("line", out var l) || !l.TryGetInt32(out var line))
        {
            WriteResponse(id, error: new { message = "missing line" });
            return;
        }

        var file = f.GetString() ?? string.Empty;
        var result = LuaDebugSession.Current?.FindPrototypeBytecode(file, line);
        if (result is null)
        {
            WriteResponse(id, error: new { message = "not found" });
            return;
        }

        WriteResponse(id, result);
    }
}
