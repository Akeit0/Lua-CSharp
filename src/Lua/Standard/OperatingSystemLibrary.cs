using System.Diagnostics;
using Lua.Standard.Internal;

namespace Lua.Standard;

public sealed class OperatingSystemLibrary
{
    public static readonly OperatingSystemLibrary Instance = new();

    public OperatingSystemLibrary()
    {
        Functions = [
            new("clock", Clock),
            new("date", Date),
            new("difftime", DiffTime),
            new("execute", Execute),
            new("exit", Exit),
            new("getenv", GetEnv),
            new("remove", Remove),
            new("rename", Rename),
            new("setlocale", SetLocale),
            new("time", Time),
            new("tmpname", TmpName),
        ];
    }

    public readonly LuaFunction[] Functions;

    public ValueTask Clock(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        context.Return(DateTimeHelper.GetUnixTime(DateTime.UtcNow, Process.GetCurrentProcess().StartTime));
        return default;
    }

    public ValueTask Date(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var format = context.HasArgument(0)
            ? context.GetArgument<string>(0).AsSpan()
            : "%c".AsSpan();

        DateTime now;
        if (context.HasArgument(1))
        {
            var time = context.GetArgument<double>(1);
            now = DateTimeHelper.FromUnixTime(time);
        }
        else
        {
            now = DateTime.UtcNow;
        }

        var isDst = false;
        if (format[0] == '!')
        {
            format = format[1..];
        }
        else
        {
            now = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local);
            isDst = now.IsDaylightSavingTime();
        }

        if (format == "*t")
        {
            var table = new LuaTable();

            table["year"] = now.Year;
            table["month"] = now.Month;
            table["day"] = now.Day;
            table["hour"] = now.Hour;
            table["min"] = now.Minute;
            table["sec"] = now.Second;
            table["wday"] = ((int)now.DayOfWeek) + 1;
            table["yday"] = now.DayOfYear;
            table["isdst"] = isDst;

            context.Return(table);
        }
        else
        {
            context.Return(DateTimeHelper.StrFTime(context.State, format, now));
        }

        return default;
    }

    public ValueTask DiffTime(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var t2 = context.GetArgument<double>(0);
        var t1 = context.GetArgument<double>(1);
        context.Return(t2 - t1);
        return default;
    }

    public ValueTask Execute(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        // os.execute(command) is not supported

        if (context.HasArgument(0))
        {
            throw new NotSupportedException("os.execute(command) is not supported");
        }
        else
        {
            context.Return(false);
        return default;
        }
    }

    public ValueTask Exit(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        // Ignore 'close' parameter

        if (context.HasArgument(0))
        {
            var code = context.Arguments[0];

            if (code.TryRead<bool>(out var b))
            {
                Environment.Exit(b ? 0 : 1);
            }
            else if (code.TryRead<int>(out var d))
            {
                Environment.Exit(d);
            }
            else
            {
                LuaRuntimeException.BadArgument(context.State.GetTraceback(), 1, "exit", LuaValueType.Nil.ToString(), code.Type.ToString());
            }
        }
        else
        {
            Environment.Exit(0);
        }

        return default;
    }

    public ValueTask GetEnv(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var variable = context.GetArgument<string>(0);
        context.Return(Environment.GetEnvironmentVariable(variable) ?? LuaValue.Nil);
        return default;
    }

    public ValueTask Remove(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var fileName = context.GetArgument<string>(0);
        try
        {
            File.Delete(fileName);
            context.Return(true);
        return default;
        }
        catch (IOException ex)
        {
            context.Return(LuaValue.Nil, ex.Message, ex.HResult);
            return default;
        }
    }

    public ValueTask Rename(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var oldName = context.GetArgument<string>(0);
        var newName = context.GetArgument<string>(1);
        try
        {
            File.Move(oldName, newName);
            context.Return(true);
        return default;
        }
        catch (IOException ex)
        {
            context.Return(LuaValue.Nil, ex.Message, ex.HResult);
            return default;
        }
    }

    public ValueTask SetLocale(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        // os.setlocale is not supported (always return nil)

        context.Return(LuaValue.Nil);
        return default;
    }

    public ValueTask Time(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        if (context.HasArgument(0))
        {
            var table = context.GetArgument<LuaTable>(0);
            var date = DateTimeHelper.ParseTimeTable(context.State, table);
            context.Return(DateTimeHelper.GetUnixTime(date));
        return default;
        }
        else
        {
            context.Return(DateTimeHelper.GetUnixTime(DateTime.UtcNow));
        return default;
        }
    }

    public ValueTask TmpName(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        context.Return(Path.GetTempFileName());
        return default;
    }
}