using System.Runtime.CompilerServices;
using Lua.Runtime;
using Lua.Internal;

namespace Lua.Standard;

public class DebugLibrary
{
    public static readonly DebugLibrary Instance = new();

    public DebugLibrary()
    {
        Functions =
        [
            new("getlocal", GetLocal),
            new("setlocal", SetLocal),
            new("getupvalue", GetUpValue),
            new("setupvalue", SetUpValue),
            new("getuservalue", GetUserValue),
            new("setuservalue", SetUserValue),
            new("getmetatable", GetMetatable),
            new("setmetatable", SetMetatable),
            new("traceback", Traceback),
            new("getregistry", GetRegistry),
            new("upvaluejoin", UpValueJoin),
            new("getinfo", GetInfo),
        ];
    }

    public readonly LuaFunction[] Functions;


    LuaThread GetLuaThread(in LuaFunctionExecutionContext context, out int argOffset)
    {
        if (context.ArgumentCount < 1)
        {
            argOffset = 0;
            return context.Thread;
        }

        if (context.GetArgument(0).TryRead<LuaThread>(out var thread))
        {
            argOffset = 1;
            return thread;
        }

        argOffset = 0;
        return context.Thread;
    }

    ref LuaValue FindLocal(LuaThread thread, int level, int index, out string? name)
    {
        if (index == 0)
        {
            name = null;
            return ref Unsafe.NullRef<LuaValue>();
        }

        var callStack = thread.GetCallStackFrames();
        var frame = callStack[^(level + 1)];
        if (index < 0)
        {
            index = -index - 1;
            var frameVariableArgumentCount = frame.VariableArgumentCount;
            if (frameVariableArgumentCount > 0 && index < frameVariableArgumentCount)
            {
                name = "(vararg)";
                return ref thread.Stack.Get(frame.Base - frameVariableArgumentCount + index);
            }

            name = null;
            return ref Unsafe.NullRef<LuaValue>();
        }

        index -= 1;


        var frameBase = frame.Base;
        var nextFrameBase = level != 0 ? callStack[^level].Base : thread.Stack.Count;
        if (nextFrameBase - frameBase <= index)
        {
            name = null;
            return ref Unsafe.NullRef<LuaValue>();
        }

        if (frame.Function is Closure closure)
        {
            var locals = closure.Proto.Locals;
            var currentPc = callStack[^level].CallerInstructionIndex;
            foreach (var local in locals)
            {
                if (local.Index == index && currentPc >= local.StartPc && currentPc < local.EndPc)
                {
                    name = local.Name.ToString();
                    return ref thread.Stack.Get(frameBase + local.Index);
                }

                if (local.Index >= index)
                {
                    break;
                }
            }
        }

        name = "(*temporary)";
        return ref thread.Stack.Get(frameBase + index);
    }

    public ValueTask<int> GetLocal(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    {
        static LuaValue GetParam(LuaFunction function, int index)
        {
            if (function is Closure closure)
            {
                var paramCount = closure.Proto.ParameterCount;
                if (0 <= index && index < paramCount)
                {
                    return closure.Proto.Locals[index].Name.ToString();
                }
            }

            return LuaValue.Nil;
        }

        var thread = GetLuaThread(context, out var argOffset);

        var index = context.GetArgument<int>(argOffset + 1);
        if (context.GetArgument(argOffset).TryReadFunction(out var f))
        {
            buffer.Span[0] = GetParam(f, index - 1);
            return new(1);
        }

        var level = context.GetArgument<int>(argOffset);


        if (level < 0 || level >= thread.GetCallStackFrames().Length)
        {
            context.ThrowBadArgument(1, "level out of range");
        }

        ref var local = ref FindLocal(thread, level, index, out var name);
        if (name is null)
        {
            buffer.Span[0] = LuaValue.Nil;
            return new(1);
        }

        buffer.Span[0] = name;
        buffer.Span[1] = local;
        return new(2);
    }

    public ValueTask<int> SetLocal(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    {
        var thread = GetLuaThread(context, out var argOffset);

        var value = context.GetArgument(argOffset + 2);
        var index = context.GetArgument<int>(argOffset + 1);
        var level = context.GetArgument<int>(argOffset);


        if (level < 0 || level >= thread.GetCallStackFrames().Length)
        {
            context.ThrowBadArgument(1, "level out of range");
        }

        ref var local = ref FindLocal(thread, level, index, out var name);
        if (name is null)
        {
            buffer.Span[0] = LuaValue.Nil;
            return new(1);
        }

        buffer.Span[0] = name;
        local = value;
        return new(1);
    }

    public ValueTask<int> GetUpValue(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    {
        var func = context.GetArgument<LuaFunction>(0);
        var index = context.GetArgument<int>(1) - 1;
        if (func is not Closure closure)
        {
            return new(0);
        }

        var upValues = closure.UpValues;
        var descriptions = closure.Proto.UpValues;
        if (index < 0 || index >= descriptions.Length)
        {
            return new(0);
        }

        var description = descriptions[index];
        buffer.Span[0] = description.Name.ToString();
        buffer.Span[1] = upValues[index].GetValue();
        return new(2);
    }

    public ValueTask<int> SetUpValue(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    {
        var func = context.GetArgument<LuaFunction>(0);
        var index = context.GetArgument<int>(1) - 1;
        var value = context.GetArgument(2);
        if (func is not Closure closure)
        {
            return new(0);
        }

        var upValues = closure.UpValues;
        var descriptions = closure.Proto.UpValues;
        if (index < 0 || index >= descriptions.Length)
        {
            return new(0);
        }

        var description = descriptions[index];
        buffer.Span[0] = description.Name.ToString();
        upValues[index].SetValue(value);
        return new(1);
    }

    public ValueTask<int> GetUserValue(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    {
        var iUserData = context.GetArgument<ILuaUserData>(0);
        var index = context.GetArgument<int>(1);
        if (iUserData is not LuaUserData userData || index < 1 || index > userData.Values.Length)
        {
            buffer.Span[0] = LuaValue.Nil;
            return new(1);
        }

        buffer.Span[0] = userData.Values[index - 1];
        return new(1);
    }

    public ValueTask<int> SetUserValue(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    {
        var iUserData = context.GetArgument<ILuaUserData>(0);
        var value = context.GetArgument(1);
        var index = context.GetArgument<int>(2);
        if (iUserData is not LuaUserData userData ||
            index < 1 || index > userData.Values.Length)
        {
            buffer.Span[0] = LuaValue.Nil;
            return new(1);
        }

        userData.Values[index - 1] = value;
        buffer.Span[0] = new LuaValue(iUserData);
        return new(1);
    }

    public ValueTask<int> GetMetatable(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument(0);

        if (context.State.TryGetMetatable(arg0, out var table))
        {
            buffer.Span[0] = table;
        }
        else
        {
            buffer.Span[0] = LuaValue.Nil;
        }

        return new(1);
    }

    public ValueTask<int> SetMetatable(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument(0);
        var arg1 = context.GetArgument(1);

        if (arg1.Type is not (LuaValueType.Nil or LuaValueType.Table))
        {
            LuaRuntimeException.BadArgument(context.State.GetTraceback(), 2, "setmetatable", [LuaValueType.Nil, LuaValueType.Table]);
        }

        context.State.SetMetatable(arg0, arg1.UnsafeRead<LuaTable>());

        buffer.Span[0] = arg0;
        return new(1);
    }

    public ValueTask<int> Traceback(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    {
        var thread = GetLuaThread(context, out var argOffset);

        var message = context.GetArgumentOrDefault(argOffset);
        var level = context.GetArgumentOrDefault<int>(argOffset + 1, argOffset == 0 ? 1 : 0);

        if (message.Type is not (LuaValueType.Nil or LuaValueType.String or LuaValueType.Number))
        {
            buffer.Span[0] = message;
            return new(1);
        }

        if (level < 0)
        {
            buffer.Span[0] = LuaValue.Nil;
            return new(1);
        }

        var currentFrame = thread.GetCallStackFrames().Length > 0 ? thread.GetCallStackFrames()[^1] : default;
        thread.PushCallStackFrame(currentFrame);
        var callStack = thread.GetCallStackFrames();
        var skipCount = Math.Min(level, callStack.Length - 1);
        var frames = callStack[1..^skipCount];
        buffer.Span[0] = Runtime.Traceback.GetTracebackString(context.State, (Closure)callStack[0].Function, frames, message);
        thread.PopCallStackFrame();
        return new(1);
    }

    public ValueTask<int> GetRegistry(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    {
        buffer.Span[0] = context.State.Registry;
        return new(1);
    }

    public ValueTask<int> UpValueJoin(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    {
        var n2 = context.GetArgument<int>(3);
        var f2 = context.GetArgument<LuaFunction>(2);
        var n1 = context.GetArgument<int>(1);
        var f1 = context.GetArgument<LuaFunction>(0);

        if (f1 is not Closure closure1 || f2 is not Closure closure2)
        {
            buffer.Span[0] = LuaValue.Nil;
            return new(1);
        }

        var upValues1 = closure1.GetUpValuesSpan();
        var upValues2 = closure2.GetUpValuesSpan();
        if (n1 <= 0 || n1 > upValues1.Length)
        {
            context.ThrowBadArgument(1, "invalid upvalue index");
        }

        if (n2 < 0 || n2 > upValues2.Length)
        {
            context.ThrowBadArgument(3, "invalid upvalue index");
        }

        upValues1[n1 - 1] = upValues2[n2 - 1];
        return new(0);
    }

    public ValueTask<int> GetInfo(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    {
        //return new(0);
        var thread = GetLuaThread(context, out var argOffset);
        string what = context.GetArgumentOrDefault<string>(argOffset + 1, "flnStu");
        CallStackFrame? previousFrame = null;
        CallStackFrame? currentFrame = null;
        int pc = 0;
        var arg1 = context.GetArgument(argOffset);

        if (arg1.TryReadFunction(out var functionToInspect))
        {
            //what = ">" + what;
        }
        else if (arg1.TryReadNumber(out _))
        {
            var level = context.GetArgument<int>(argOffset) + 1;

            var callStack = thread.GetCallStackFrames();
            if (level <= 0 || level > callStack.Length)
            {
                context.ThrowBadArgument(1, "level out of range");
            }

            currentFrame = thread.GetCallStackFrames()[^(level)];
            functionToInspect = currentFrame.Value.Function;
            previousFrame = level + 1 <= callStack.Length ? callStack[^(level + 1)] : null;
            if (level != 0)
            {
                pc = thread.GetCallStackFrames()[^(level - 1)].CallerInstructionIndex;
            }
        }
        else
        {
            context.ThrowBadArgument(argOffset, "function or level expected");
        }

        using var debug = LuaDebug.Create(context.State, previousFrame, currentFrame, functionToInspect, pc, what, out var isValid);
        if (!isValid)
        {
            context.ThrowBadArgument(argOffset + 1, "invalid option");
        }

        var table = new LuaTable(0, 1);
        if (what.Contains('S'))
        {
            table["source"] = debug.Source ?? LuaValue.Nil;
            table["short_src"] = debug.ShortSource.ToString();
            table["linedefined"] = debug.LineDefined;
            table["lastlinedefined"] = debug.LastLineDefined;
            table["what"] = debug.What ?? LuaValue.Nil;
            ;
        }

        if (what.Contains('l'))
        {
            table["currentline"] = debug.CurrentLine;
        }

        if (what.Contains('u'))
        {
            table["nups"] = debug.UpValueCount;
            table["nparams"] = debug.ParameterCount;
            table["isvararg"] = debug.IsVarArg;
        }

        if (what.Contains('n'))
        {
            table["name"] = debug.Name ?? LuaValue.Nil;
            ;
            table["namewhat"] = debug.NameWhat ?? LuaValue.Nil;
            ;
        }

        if (what.Contains('t'))
        {
            table["istailcall"] = debug.IsTailCall;
        }

        if (what.Contains('f'))
        {
            table["func"] = functionToInspect;
        }

        if (what.Contains('L'))
        {
            var activeLines = new LuaTable(0, 8);
            if (functionToInspect is Closure closure)
            {
                foreach (var pos in closure.Proto.SourcePositions)
                {
                    activeLines[pos.Line] = true;
                }
            }

            table["activelines"] = activeLines;
        }

        buffer.Span[0] = table;

        return new(1);
    }
}