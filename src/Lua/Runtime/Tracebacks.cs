using System.Runtime.CompilerServices;
using Lua.CodeAnalysis;

namespace Lua.Runtime;

public class Traceback
{
    public required Closure RootFunc { get; init; }
    public required CallStackFrame[] StackFrames { get; init; }

    internal string RootChunkName => RootFunc.Proto.Name; //StackFrames.Length == 0 ? "" : StackFrames[^1].Function is Closure closure ? closure.Proto.GetRoot().Name : StackFrames[^2].Function.Name;

    internal SourcePosition LastPosition
    {
        get
        {
            var stackFrames = StackFrames.AsSpan();
            for (var index = stackFrames.Length - 1; index >= 0; index--)
            {
                LuaFunction lastFunc = index > 0 ? stackFrames[index - 1].Function : RootFunc;
                var frame = stackFrames[index];
                if (lastFunc is Closure closure)
                {
                    var p = closure.Proto;
                    return p.SourcePositions[frame.CallerInstructionIndex];
                }
            }
            return default;
        }
    }


    public override string ToString()
    {
        var builder = new DefaultInterpolatedStringHandler(64, 64);
        builder.AppendLiteral("stack traceback:\n");
        var stackFrames = StackFrames.AsSpan();
        for (var index = stackFrames.Length - 1; index >= 0; index--)
        {
            LuaFunction lastFunc = index > 0 ? stackFrames[index - 1].Function : RootFunc;
            var frame = stackFrames[index];
            if (lastFunc is not null and not Closure)
            {
                builder.AppendLiteral("\t[C#]: in function '");
                builder.AppendFormatted(lastFunc.Name);
                builder.AppendLiteral("'\n");
            }
            else if (lastFunc is Closure closure)
            {
                var p = closure.Proto;
                var root = p.GetRoot();
                builder.AppendLiteral("\t");
                builder.AppendFormatted(root.Name);
                builder.AppendLiteral(":");
                builder.AppendFormatted(p.SourcePositions[frame.CallerInstructionIndex].Line);
                builder.AppendLiteral(root == p ? ": in '" : ": in function '");
                builder.AppendFormatted(p.Name);
                builder.AppendLiteral("'\n");
            }
        } 
        return builder.ToStringAndClear();
    }
}