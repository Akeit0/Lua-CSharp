using Lua.Internal;
using Lua.Standard.Internal;

namespace Lua.Standard;

public sealed class IOLibrary
{
    public static readonly IOLibrary Instance = new();

    public IOLibrary()
    {
        Functions = [
            new("close", Close),
            new("flush", Flush),
            new("input", Input),
            new("lines", Lines),
            new("open", Open),
            new("output", Output),
            new("read", Read),
            new("type", Type),
            new("write", Write),
        ];
    }

    public readonly LuaFunction[] Functions;

    public ValueTask Close(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var file = context.HasArgument(0)
            ? context.GetArgument<FileHandle>(0)
            : context.State.Environment["io"].Read<LuaTable>()["stdout"].Read<FileHandle>();

        try
        {
            file.Close();
            context.Return(true);
            return default;
        }
        catch (IOException ex)
        {
            context.Return(LuaValue.Nil, ex.Message, ex.HResult);
            return default;
        }
    }

    public ValueTask Flush(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var file = context.State.Environment["io"].Read<LuaTable>()["stdout"].Read<FileHandle>();

        try
        {
            file.Flush();
            context.Return(true);
            return default;
        }
        catch (IOException ex)
        {
            context.Return(LuaValue.Nil, ex.Message, ex.HResult);
            return default;
        }
    }

    public ValueTask Input(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var io = context.State.Environment["io"].Read<LuaTable>();

        if (context.ArgumentCount == 0 || context.Arguments[0].Type is LuaValueType.Nil)
        {
            context.Return(io["stdio"]);
            return default;
        }

        var arg = context.Arguments[0];
        if (arg.TryRead<FileHandle>(out var file))
        {
            io["stdio"] = new(file);
            context.Return(new LuaValue(file));
            return default;
        }
        else
        {
            var stream = File.Open(arg.ToString()!, FileMode.Open, FileAccess.ReadWrite);
            var handle = new FileHandle(stream);
            io["stdio"] = new(handle);
            context.Return(new LuaValue(handle));
            return default;
        }
    }

    public ValueTask Lines(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.ArgumentCount == 0)
        {
            var file = context.State.Environment["io"].Read<LuaTable>()["stdio"].Read<FileHandle>();
            context.Return(new LuaFunction("iterator", (context, ct) =>
            {
                if (!IOHelper.Read(context, file, "lines", 0, [], true))
                {
                    file.Close();
                }
                return default;
            }));
            return default;
        }
        else
        {
            var fileName = context.GetArgument<string>(0);
            IOHelper.Open(context, fileName, "r",  true);

            var file = IOHelper.Open(context, fileName, "r",  true)!;
            var formats = context.Arguments[1..].ToArray();

            context.Return(new LuaFunction("iterator", (context, ct) =>
            {
                if (!IOHelper.Read(context, file, "lines", 0, formats, true))
                {
                    file.Close();
                }
                return default;
            }));

            return default;
        }
    }

    public ValueTask Open(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var fileName = context.GetArgument<string>(0);
        var mode = context.HasArgument(1)
            ? context.GetArgument<string>(1)
            : "r";

        IOHelper.Open(context, fileName, mode, false);
        return default;
    }

    public ValueTask Output(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var io = context.State.Environment["io"].Read<LuaTable>();

        if (context.ArgumentCount == 0 || context.Arguments[0].Type is LuaValueType.Nil)
        {
            context.Return(io["stdout"]);
            return default;
        }

        var arg = context.Arguments[0];
        if (arg.TryRead<FileHandle>(out var file))
        {
            io["stdout"] = new(file);
            context.Return(new LuaValue(file));
            return default;
        }
        else
        {
            var stream = File.Open(arg.ToString()!, FileMode.Open, FileAccess.ReadWrite);
            var handle = new FileHandle(stream);
            io["stdout"] = new(handle);
            context.Return(new LuaValue(handle));
            return default;
        }
    }

    public ValueTask Read(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var file = context.State.Environment["io"].Read<LuaTable>()["stdio"].Read<FileHandle>();
        IOHelper.Read(context, file, "read", 0, context.Arguments, false);
        return default;
    }

    public ValueTask Type(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument(0);

        if (arg0.TryRead<FileHandle>(out var file))
        {
            context.Return(file.IsClosed ? "closed file" : "file");
        }
        else
        {
            context.Return(LuaValue.Nil);
        }

        return default;
    }

    public ValueTask Write(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var file = context.State.Environment["io"].Read<LuaTable>()["stdout"].Read<FileHandle>();
        IOHelper.Write(file, "write", context);
        return default;
    }
}