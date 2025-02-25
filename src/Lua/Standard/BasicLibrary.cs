using System.Globalization;
using Lua.CodeAnalysis.Compilation;
using Lua.Internal;
using Lua.Runtime;

namespace Lua.Standard;

public sealed class BasicLibrary
{
    public static readonly BasicLibrary Instance = new();

    public BasicLibrary()
    {
        Functions =
        [
            new("assert", Assert),
            new("collectgarbage", CollectGarbage),
            new("dofile", DoFile),
            new("error", Error),
            new("getmetatable", GetMetatable),
            new("ipairs", IPairs),
            new("loadfile", LoadFile),
            new("load", Load),
            new("next", Next),
            new("pairs", Pairs),
            new("pcall", PCall),
            new("print", Print),
            new("rawequal", RawEqual),
            new("rawget", RawGet),
            new("rawlen", RawLen),
            new("rawset", RawSet),
            new("select", Select),
            new("setmetatable", SetMetatable),
            new("tonumber", ToNumber),
            new("tostring", ToString),
            new("type", Type),
            new("xpcall", XPCall),
        ];

        IPairsIterator = new("iterator", (context, cancellationToken) =>
        {
            var table = context.GetArgument<LuaTable>(0);
            var i = context.GetArgument<double>(1);

            i++;
            if (table.TryGetValue(i, out var value))
            {
                context.Return(i, value);
            }
            else
            {
                context.Return(LuaValue.Nil, LuaValue.Nil);
            }

            return default;
        });

        PairsIterator = new("iterator", Next);
    }

    public readonly LuaFunction[] Functions;
    readonly LuaFunction IPairsIterator;
    readonly LuaFunction PairsIterator;

    public ValueTask Assert(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument(0);

        if (!arg0.ToBoolean())
        {
            var message = "assertion failed!";
            if (context.HasArgument(1))
            {
                message = context.GetArgument<string>(1);
            }

            throw new LuaAssertionException(context.State.GetTraceback(), message);
        }
        context.Return(context.Arguments);

        return default;
    }

    public ValueTask CollectGarbage(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        GC.Collect();
        context.Return();
        return default;
    }

    public async ValueTask DoFile(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<string>(0);

        // do not use LuaState.DoFileAsync as it uses the newExecutionContext
        var text = await File.ReadAllTextAsync(arg0, cancellationToken);
        var fileName = Path.GetFileName(arg0);
        var chunk = LuaCompiler.Default.Compile(text, fileName);

        await new Closure(context.State, chunk).InvokeAsync(context, cancellationToken);
    }

    public ValueTask Error(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var value = context.ArgumentCount == 0
            ? LuaValue.Nil
            : context.Arguments[0];

        var traceback = context.State.GetTraceback();
        if (value.TryReadString(out var str))
        {
            value = $"{traceback.RootChunkName}:{traceback.LastPosition.Line}: {str}";
        }

        throw new LuaRuntimeException(traceback, value);
    }

    public ValueTask GetMetatable(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument(0);

        if (arg0.TryRead<LuaTable>(out var table))
        {
            if (table.Metatable == null)
            {
                context.Return(LuaValue.Nil);
            }
            else if (table.Metatable.TryGetValue(Metamethods.Metatable, out var metatable))
            {
                context.Return(metatable);
            }
            else
            {
                context.Return(table.Metatable);
            }
        }
        else
        {
            context.Return(LuaValue.Nil);
        }

        return default;
    }

    public ValueTask IPairs(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<LuaTable>(0);

        // If table has a metamethod __ipairs, calls it with table as argument and returns the first three results from the call.
        if (arg0.Metatable != null && arg0.Metatable.TryGetValue(Metamethods.IPairs, out var metamethod))
        {
            if (!metamethod.TryRead<LuaFunction>(out var function))
            {
                LuaRuntimeException.AttemptInvalidOperation(context.State.GetTraceback(), "call", metamethod);
            }

            return function.InvokeAsync(context, cancellationToken);
        }

        context.Return(IPairsIterator, arg0, 0);
        return default;
    }

    public async ValueTask LoadFile(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        // Lua-CSharp does not support binary chunks, the mode argument is ignored.
        var arg0 = context.GetArgument<string>(0);
        var arg2 = context.HasArgument(2)
            ? context.GetArgument<LuaTable>(2)
            : null;

        // do not use LuaState.DoFileAsync as it uses the newExecutionContext
        try
        {
            var text = await File.ReadAllTextAsync(arg0, cancellationToken);
            var fileName = Path.GetFileName(arg0);
            var chunk = LuaCompiler.Default.Compile(text, fileName);
            context.Return(new Closure(context.State, chunk, arg2));
        }
        catch (Exception ex)
        {
            context.Return(LuaValue.Nil, ex.Message);
        }
    }

    public ValueTask Load(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        // Lua-CSharp does not support binary chunks, the mode argument is ignored.
        var arg0 = context.GetArgument(0);

        var arg1 = context.HasArgument(1)
            ? context.GetArgument<string>(1)
            : null;

        var arg3 = context.HasArgument(3)
            ? context.GetArgument<LuaTable>(3)
            : null;

        // do not use LuaState.DoFileAsync as it uses the newExecutionContext
        try
        {
            if (arg0.TryRead<string>(out var str))
            {
                var chunk = LuaCompiler.Default.Compile(str, arg1 ?? str);
                context.Return(new Closure(context.State, chunk, arg3));
                return default;
            }
            else if (arg0.TryRead<LuaFunction>(out var function))
            {
                // TODO: 
                throw new NotImplementedException();
            }
            else
            {
                LuaRuntimeException.BadArgument(context.State.GetTraceback(), 1, "load");
                return default; // dummy
            }
        }
        catch (Exception ex)
        {
            context.Return(LuaValue.Nil, ex.Message);
            return default;
        }
    }

    public ValueTask Next(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<LuaTable>(0);
        var arg1 = context.HasArgument(1) ? context.Arguments[1] : LuaValue.Nil;

        if (arg0.TryGetNext(arg1, out var kv))
        {
            context.Return(kv.Key, kv.Value);
        }
        else
        {
            context.Return(LuaValue.Nil);
        }

        return default;
    }

    public ValueTask Pairs(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<LuaTable>(0);

        // If table has a metamethod __pairs, calls it with table as argument and returns the first three results from the call.
        if (arg0.Metatable != null && arg0.Metatable.TryGetValue(Metamethods.Pairs, out var metamethod))
        {
            if (!metamethod.TryRead<LuaFunction>(out var function))
            {
                LuaRuntimeException.AttemptInvalidOperation(context.State.GetTraceback(), "call", metamethod);
            }

            return function.InvokeAsync(context, cancellationToken);
        }

        context.Return(PairsIterator, arg0, LuaValue.Nil);
        return default;
    }

    public async ValueTask PCall(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        //Guid id = Guid.NewGuid();
        //Console.WriteLine("PCall S1 " + id + " " + context.Thread.CallStack.Count);
        var arg0 = context.GetArgument<LuaFunction>(0);
        try
        {
            //Console.WriteLine("PCall S2 " + id + " " + context.Thread.CallStack.Count);
            await arg0.InvokeAsync(context with
            {
                State = context.State,
                ArgumentCount = context.ArgumentCount - 1,
                FrameBase = context.FrameBase + 1,
                ReturnFrameBase = context.FrameBase + 1
            }, cancellationToken);

            context.Thread.Stack.Get(context.FrameBase) = true;
        }
        catch (Exception ex)
        {
            if (ex is LuaRuntimeException { ErrorObject: not null } luaEx)
            {
                context.Return(false, luaEx.ErrorObject.Value);
            }
            else
            {
                context.Return(false, ex.Message);
            }
        }
        finally
        {
            //Console.WriteLine("PCall E3 " + id + " " + context.Thread.CallStack.Count);
        }
    }

    public async ValueTask Print(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        for (int i = 0; i < context.ArgumentCount; i++)
        {
            var top = context.Thread.Stack.Count;
            await context.Arguments[i].CallToStringAsync(context, cancellationToken);
            Console.Write(context.Thread.Stack.Get(top).ToString());
            Console.Write('\t');
        }

        Console.WriteLine();
        context.Return();

        //Console.WriteLine("Print  CallStack " + context.Thread.CallStack.Count);
    }

    public ValueTask RawEqual(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument(0);
        var arg1 = context.GetArgument(1);

        context.Return(arg0 == arg1);
        return default;
    }

    public ValueTask RawGet(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<LuaTable>(0);
        var arg1 = context.GetArgument(1);
        context.Return(arg0[arg1]);
        return default;
    }

    public ValueTask RawLen(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument(0);

        if (arg0.TryRead<LuaTable>(out var table))
        {
            context.Return(table.ArrayLength);
        }
        else if (arg0.TryRead<string>(out var str))
        {
            context.Return(str.Length);
        }
        else
        {
            LuaRuntimeException.BadArgument(context.State.GetTraceback(), 2, "rawlen", [LuaValueType.String, LuaValueType.Table]);
        }

        return default;
    }

    public ValueTask RawSet(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<LuaTable>(0);
        var arg1 = context.GetArgument(1);
        var arg2 = context.GetArgument(2);

        arg0[arg1] = arg2;
        context.Return();
        return default;
    }

    public ValueTask Select(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument(0);

        if (arg0.TryRead<int>(out var index))
        {
            if (Math.Abs(index) > context.ArgumentCount)
            {
                throw new LuaRuntimeException(context.State.GetTraceback(), "bad argument #1 to 'select' (index out of range)");
            }

            var span = index >= 0
                ? context.Arguments[index..]
                : context.Arguments[(context.ArgumentCount + index)..];

            context.Return(span);

            return default;
        }
        else if (arg0.TryRead<string>(out var str) && str == "#")
        {
            context.Return(context.ArgumentCount - 1);
            return default;
        }
        else
        {
            LuaRuntimeException.BadArgument(context.State.GetTraceback(), 1, "select", "number", arg0.Type.ToString());
            return default;
        }
    }

    public ValueTask SetMetatable(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<LuaTable>(0);
        var arg1 = context.GetArgument(1);

        if (arg1.Type is not (LuaValueType.Nil or LuaValueType.Table))
        {
            LuaRuntimeException.BadArgument(context.State.GetTraceback(), 2, "setmetatable", [LuaValueType.Nil, LuaValueType.Table]);
        }

        if (arg0.Metatable != null && arg0.Metatable.TryGetValue(Metamethods.Metatable, out _))
        {
            throw new LuaRuntimeException(context.State.GetTraceback(), "cannot change a protected metatable");
        }
        else if (arg1.Type is LuaValueType.Nil)
        {
            arg0.Metatable = null;
        }
        else
        {
            arg0.Metatable = arg1.Read<LuaTable>();
        }

        context.Return(arg0);
        return default;
    }

    public ValueTask ToNumber(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var e = context.GetArgument(0);
        int? toBase = context.HasArgument(1)
            ? (int)context.GetArgument<double>(1)
            : null;

        if (toBase != null && (toBase < 2 || toBase > 36))
        {
            throw new LuaRuntimeException(context.State.GetTraceback(), "bad argument #2 to 'tonumber' (base out of range)");
        }

        double? value = null;
        if (e.Type is LuaValueType.Number)
        {
            value = e.UnsafeRead<double>();
        }
        else if (e.TryRead<string>(out var str))
        {
            if (toBase == null)
            {
                if (e.TryRead<double>(out var result))
                {
                    value = result;
                }
            }
            else if (toBase == 10)
            {
                if (double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
                {
                    value = result;
                }
            }
            else
            {
                try
                {
                    // if the base is not 10, str cannot contain a minus sign
                    var span = str.AsSpan().Trim();
                    if (span.Length == 0) goto END;

                    var first = span[0];
                    var sign = first == '-' ? -1 : 1;
                    if (first is '+' or '-')
                    {
                        span = span[1..];
                    }

                    if (span.Length == 0) goto END;

                    if (toBase == 16 && span.Length > 2 && span[0] is '0' && span[1] is 'x' or 'X')
                    {
                        value = sign * HexConverter.ToDouble(span);
                    }
                    else
                    {
                        value = sign * StringToDouble(span, toBase.Value);
                    }
                }
                catch (FormatException)
                {
                    goto END;
                }
            }
        }
        else
        {
            goto END;
        }

    END:
        if (value is double.NaN)
        {
            value = null;
        }

        context.Return(value ?? LuaValue.Nil);
        return default;
    }

    static double StringToDouble(ReadOnlySpan<char> text, int toBase)
    {
        var value = 0.0;
        for (int i = 0; i < text.Length; i++)
        {
            var v = text[i] switch
            {
                '0' => 0,
                '1' => 1,
                '2' => 2,
                '3' => 3,
                '4' => 4,
                '5' => 5,
                '6' => 6,
                '7' => 7,
                '8' => 8,
                '9' => 9,
                'a' or 'A' => 10,
                'b' or 'B' => 11,
                'c' or 'C' => 12,
                'd' or 'D' => 13,
                'e' or 'E' => 14,
                'f' or 'F' => 15,
                'g' or 'G' => 16,
                'h' or 'H' => 17,
                'i' or 'I' => 18,
                'j' or 'J' => 19,
                'k' or 'K' => 20,
                'l' or 'L' => 21,
                'm' or 'M' => 22,
                'n' or 'N' => 23,
                'o' or 'O' => 24,
                'p' or 'P' => 25,
                'q' or 'Q' => 26,
                'r' or 'R' => 27,
                's' or 'S' => 28,
                't' or 'T' => 29,
                'u' or 'U' => 30,
                'v' or 'V' => 31,
                'w' or 'W' => 32,
                'x' or 'X' => 33,
                'y' or 'Y' => 34,
                'z' or 'Z' => 35,
                _ => 0,
            };

            if (v >= toBase)
            {
                throw new FormatException();
            }

            value += v * Math.Pow(toBase, text.Length - i - 1);
        }

        return value;
    }

    public ValueTask ToString(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument(0);
        context.Return();
        return arg0.CallToStringAsync(context, cancellationToken);
    }

    public ValueTask Type(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument(0);

        context.Return(arg0.Type switch
        {
            LuaValueType.Nil => "nil",
            LuaValueType.Boolean => "boolean",
            LuaValueType.String => "string",
            LuaValueType.Number => "number",
            LuaValueType.Function => "function",
            LuaValueType.Thread => "thread",
            LuaValueType.LightUserData => "userdata",
            LuaValueType.UserData => "userdata",
            LuaValueType.Table => "table",
            _ => throw new NotImplementedException(),
        });

        return default;
    }

    public async ValueTask XPCall(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<LuaFunction>(0);
        var arg1 = context.GetArgument<LuaFunction>(1);

        try
        {
            await arg0.InvokeAsync(context with
            {
                State = context.State,
                ArgumentCount = context.ArgumentCount - 2,
                FrameBase = context.FrameBase + 2,
                ReturnFrameBase = context.ReturnFrameBase + 1
            }, cancellationToken);

            context.Thread.Stack.Get(context.ReturnFrameBase) = true;
            return;
        }
        catch (Exception ex)
        {
            var error = ex is LuaRuntimeException { ErrorObject: not null } luaEx ? luaEx.ErrorObject.Value : ex.Message;

            context.State.Push(error);

            // invoke error handler
            await arg1.InvokeAsync(context with
            {
                State = context.State,
                ArgumentCount = 1,
                FrameBase = context.Thread.Stack.Count - 1,
                ReturnFrameBase = context.ReturnFrameBase + 1
            }, cancellationToken);
            context.Thread.Stack.Get(context.ReturnFrameBase) = false;

            return;
        }
    }
}