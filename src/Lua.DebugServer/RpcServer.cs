using System.Text.Json;
using System.Text.Json.Serialization;
using Cysharp.IO;
using System.Diagnostics.CodeAnalysis;
using System.Text.Unicode;

static class RpcServer
{
    static readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false };

    static readonly object writeLock = new();
    static Stream? inputStream;

    [field: AllowNull, MaybeNull]
    static Utf8StreamReader Input
    {
        get
        {
            field ??= new Utf8StreamReader(inputStream ?? Console.OpenStandardInput());
            return field;
        }
    }

    static Stream output = Console.OpenStandardOutput();

    public static void UseIO(Stream reader, Stream writer)
    {
        inputStream = reader;
        lock (writeLock)
        {
            output = writer;
        }
    }

    public static async Task RunAsync()
    {
        // Send an initial output event so the client knows we're alive
        WriteLogToConsole("[Lua.DebugServer] ready");

        ReadOnlyMemory<byte>? line;
        while ((line = await Input.ReadLineAsync()) is not null)
        {
            if (line.Value.IsEmpty) continue;
            try
            {
                using var doc = JsonDocument.Parse(line.Value);
                var root = doc.RootElement;
                var id = root.GetProperty("id"u8).GetInt32();
                var method = root.GetProperty("method"u8).GetString();
                var @params = root.TryGetProperty("params"u8, out var p) ? p : default;

                switch (method)
                {
                    case "initialize":
                        LuaDebugSession.Current ??= new();
                        WriteEvent("initialized"u8);
                        WriteResponse(id);
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
                        WriteResponse(id);
                        return;
                    default:
                        WriteError(id, $"Unknown method: {method}");
                        break;
                }
            }
            catch (Exception ex)
            {
                WriteEvent("output"u8, new { category = "stderr", output = ex + "\n" });
            }
        }
    }

    public static void Publish(string ev, object body) => WriteEvent(ev, body);
    public static void Publish(ReadOnlySpan<byte> ev, object? body = null) => WriteEvent(ev, body);

    static void WriteResponse(int id, ReadOnlySpan<byte> key, object value)
    {
        lock (writeLock)
        {
            output.Write("{\"type\":\"response\",\"id\":\""u8);
            var span = (stackalloc byte[11]);
            id.TryFormat(span, out var written);
            output.Write(span[..written]);
            output.WriteByte((byte)'"');
            {
                output.Write(",\"result\":{\""u8);
                output.Write(key);
                output.WriteByte((byte)'\"');
                output.WriteByte((byte)':');
                JsonSerializer.Serialize(output, value, options);
                output.WriteByte((byte)'}');
            }

            output.WriteByte((byte)'}');
            output.WriteByte((byte)'\n');
            output.Flush();
        }
        //var payload = new { type = "response", id, result, error };
        // Write(payload);
    }

    static void WriteError(int id, string message)
    {
        lock (writeLock)
        {
            output.Write("{\"type\":\"response\",\"id\":\""u8);
            var span = (stackalloc byte[11]);
            id.TryFormat(span, out var written);
            output.Write(span[..written]);
            output.WriteByte((byte)'"');
            {
                output.Write(",\"error\":{\"message\":"u8);
                JsonSerializer.Serialize(output, message, options);
                output.WriteByte((byte)'}');
            }

            output.WriteByte((byte)'}');
            output.WriteByte((byte)'\n');
            output.Flush();
        }
        //var payload = new { type = "response", id, result, error };
        // Write(payload);
    }

    static void WriteResponse(int id)
    {
        lock (writeLock)
        {
            output.Write("{\"type\":\"response\",\"id\":\""u8);
            var span = (stackalloc byte[11]);
            id.TryFormat(span, out var written);
            output.Write(span[..written]);
            output.WriteByte((byte)'"');

            output.WriteByte((byte)'}');
            output.WriteByte((byte)'\n');
            output.Flush();
        }
        //var payload = new { type = "response", id, result, error };
        // Write(payload);
    }

    static void WriteResponse(int id, object result)
    {
        lock (writeLock)
        {
            output.Write("{\"type\":\"response\",\"id\":\""u8);
            var span = (stackalloc byte[11]);
            id.TryFormat(span, out var written);
            output.Write(span[..written]);
            output.WriteByte((byte)'"');


            output.Write(",\"result\":"u8);
            JsonSerializer.Serialize(output, result, options);


            output.WriteByte((byte)'}');
            output.WriteByte((byte)'\n');
            output.Flush();
        }
        //var payload = new { type = "response", id, result, error };
        // Write(payload);
    }

    static void WriteEvent(string ev, object? body = null)
    {
        var payload = new { type = "event", @event = ev, body };
        Write(payload);
    }

    static void WriteEvent(ReadOnlySpan<byte> ev, object? body = null)
    {
        lock (writeLock)
        {
            output.Write("{\"type\":\"event\",\"event\":\""u8);
            output.Write(ev);
            output.WriteByte((byte)'"');

            if (body is not null)
            {
                output.Write(",\"body\":"u8);
                JsonSerializer.Serialize(output, body, options);
            }

            output.WriteByte((byte)'}');
            output.WriteByte((byte)'\n');
            output.Flush();
        }
        //var  payload = new { type = "event", @event =  System.Text.Encoding.UTF8.GetString(ev) , body };
        //Write( payload);
    }

    public static void WriteToConsole(string text, string category = "console")
    {
        WriteEvent("output"u8, new { category = category, output = text + "\n" });
    }

    public static void WriteLogToConsole(string text)
    {
        WriteEvent("output"u8, new { category = "important", output = text + "\n" });
    }

    static void Write(object payload)
    {
        lock (writeLock)
        {
            JsonSerializer.Serialize(output, payload, options);
            output.WriteByte((byte)'\n');
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

            await using var stream = client.GetStream();
            UseIO(stream, stream);

            await RunAsync();
        }
        finally
        {
            listener.Stop();
        }
    }

    // Handlers
    static void HandleSetBreakpoints(int id, JsonElement @params)
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
        WriteResponse(id, "breakpoints"u8, list.Select(l => new { verified = true, line = l.line }).ToArray());
    }

    static async Task HandleLaunchAsync(int id, JsonElement @params)
    {
        var program = @params.GetProperty("program").GetString() ?? string.Empty;
        var cwd = @params.TryGetProperty("cwd", out var cEl) ? cEl.GetString() : null;
        var stopOnEntry = @params.TryGetProperty("stopOnEntry", out var sEl) && sEl.GetBoolean();

        LuaDebugSession.Current ??= new LuaDebugSession();

        await LuaDebugSession.Current.LaunchAsync(program, cwd, stopOnEntry);
        WriteResponse(id);
    }

    static void HandleContinue(int id)
    {
        LuaDebugSession.Current?.Continue(true);
        WriteResponse(id, "allThreadsContinued"u8, true);
    }

    static void HandleGetLocals(int id, JsonElement @params)
    {
        object[] locals;
        if (@params.ValueKind != JsonValueKind.Undefined && @params.TryGetProperty("frameId", out var fEl) && fEl.TryGetInt32(out var fid))
            locals = LuaDebugSession.Current?.GetLocalsForFrame(fid) ?? Array.Empty<object>();
        else
            locals = LuaDebugSession.Current?.GetLocals() ?? Array.Empty<object>();
        WriteResponse (id, "variables"u8, locals);
    }

    static void HandleNext(int id)
    {
        var session = LuaDebugSession.Current;
        if (session is null) return;
        session.StepNext();
        session.Continue();
        WriteResponse(id);
    }

    static void HandleStepIn(int id)
    {
        var session = LuaDebugSession.Current;
        if (session is null) return;
        session.StepIn();
        session.Continue();
        WriteResponse(id);
    }

    static void HandleStepOut(int id)
    {
        var session = LuaDebugSession.Current;
        if (session is null) return;
        session.StepOut();
        session.Continue();
        WriteResponse(id);
    }

    static void HandleGetGlobals(int id)
    {
        var globals = LuaDebugSession.Current?.GetGlobals() ?? Array.Empty<object>();
        WriteResponse (id ,"variables"u8, globals);
    }

    static void HandleGetOptions(int id)
    {
        var mode = MinimalDebugger.Active?.GetStepOverMode().ToString() ?? "Line";
        WriteResponse(id, "stepOverMode"u8, mode.ToString());
    }

    static void HandleSetStepOverMode(int id, JsonElement @params)
    {
        var text = @params.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? (m.GetString() ?? "Line") : "Line";
        StepOverMode mode;
        if (!Enum.TryParse<StepOverMode>(text, ignoreCase: true, out mode)) mode = StepOverMode.Line;
        MinimalDebugger.Active?.SetStepOverMode(mode);
        WriteResponse(id, "stepOverMode"u8, mode.ToString());
    }

    static void HandleGetUpvalues(int id, JsonElement @params)
    {
        object[] upvalues;
        if (@params.ValueKind != JsonValueKind.Undefined && @params.TryGetProperty("frameId", out var fEl) && fEl.TryGetInt32(out var fid))
            upvalues = LuaDebugSession.Current?.GetUpvaluesForFrame(fid) ?? Array.Empty<object>();
        else
            upvalues = LuaDebugSession.Current?.GetUpvalues() ?? Array.Empty<object>();
        WriteResponse(id, "variables"u8, upvalues);
    }

    static void HandleSetLocal(int id, JsonElement @params)
    {
        var name = @params.GetProperty("name").GetString() ?? string.Empty;
        var value = @params.GetProperty("value").GetString() ?? string.Empty;
        var res = LuaDebugSession.Current?.SetLocal(name, value) ?? (false, null);
        if (!res.ok || res.value is null)
        {
            WriteError(id, $"failed to set local '{name}'");
        }
        else
        {
            WriteResponse(id, "value"u8, res.value);
        }
    }

    static void HandleSetUpvalue(int id, JsonElement @params)
    {
        var name = @params.GetProperty("name").GetString() ?? string.Empty;
        var value = @params.GetProperty("value").GetString() ?? string.Empty;
        var res = LuaDebugSession.Current?.SetUpvalue(name, value);
        if (res is null)
        {
            WriteError(id, $"failed to set upvalue '{name}'");
        }
        else
        {
            WriteResponse(id, "value"u8, res);
        }
    }

    static void HandleGetBytecode(int id, JsonElement @params)
    {
        object? result;
        if (@params.ValueKind != JsonValueKind.Undefined && @params.TryGetProperty("frameId", out var fEl) && fEl.TryGetInt32(out var fid))
            result = LuaDebugSession.Current?.GetBytecodeSnapshotForFrame(fid);
        else
            result = LuaDebugSession.Current?.GetBytecodeSnapshot();
        if (result is null)
        {
            WriteError(id, "no paused location");
            return;
        }

        WriteResponse(id, result);
    }

    static void HandleGetStack(int id)
    {
        var frames = LuaDebugSession.Current?.GetCallStack() ?? Array.Empty<object>();
        WriteResponse(id, "frames"u8, frames);
    }

    static void HandleGetInstrBreakpoints(int id, JsonElement @params)
    {
        var chunk = @params.GetProperty("chunk").GetString() ?? string.Empty;
        var bps = LuaDebugSession.Current?.GetInstructionBreakpoints(chunk) ?? Array.Empty<int>();
        WriteResponse(id, "breakpoints"u8, bps);
    }

    static void HandleSetInstrBreakpoint(int id, JsonElement @params)
    {
        var chunk = @params.GetProperty("chunk").GetString() ?? string.Empty;
        var index = @params.GetProperty("index").GetInt32();
        var enabled = @params.TryGetProperty("enabled", out var e) && e.GetBoolean();
        LuaDebugSession.Current?.SetInstructionBreakpoint(chunk, index, enabled);
        WriteResponse(id);
    }

    static void HandleFindPrototype(int id, JsonElement @params)
    {
        if (!@params.TryGetProperty("file", out var f) || f.ValueKind != JsonValueKind.String)
        {
            WriteError(id, "missing file");
            return;
        }

        if (!@params.TryGetProperty("line", out var l) || !l.TryGetInt32(out var line))
        {
            WriteError(id, "missing line");
            return;
        }

        var file = f.GetString() ?? string.Empty;
        var result = LuaDebugSession.Current?.FindPrototypeBytecode(file, line);
        if (result is null)
        {
            WriteError(id, "not found");
            return;
        }

        WriteResponse(id, result);
    }
}