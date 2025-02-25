using System.Diagnostics;
using System.Runtime.CompilerServices;
using Lua.Runtime;

namespace Lua;

public class LuaFunction(string name, Func<LuaFunctionExecutionContext, CancellationToken, ValueTask> func)
{
    public string Name { get; } = name;
    internal Func<LuaFunctionExecutionContext, CancellationToken, ValueTask> Func { get; } = func;

    public LuaFunction(Func<LuaFunctionExecutionContext, CancellationToken, ValueTask> func) : this("anonymous", func)
    {
    }

    public async ValueTask InvokeAsync(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var frame = new CallStackFrame
        {
            Base = context.FrameBase,
            VariableArgumentCount = this is Closure closure ? Math.Max(context.ArgumentCount - closure.Proto.ParameterCount, 0) : 0,
            Function = this,
            ReturnBase = context.ReturnFrameBase
        };
       // Guid id = Guid.NewGuid();
       // Console.WriteLine("InvokeAsync S " +id +"  " + context.Thread.CallStack.Count);
        context.Thread.PushCallStackFrame(frame);
        try
        {
            await Func(context, cancellationToken);
        }
        finally
        {
            //Console.WriteLine("InvokeAsync E " +id +"  " + context.Thread.CallStack.Count);
            context.Thread.PopCallStackFrameUnsafe();
        }
    }
    
    internal async ValueTask<int> InvokeReturnDummyAsync(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var frame = new CallStackFrame
        {
            Base = context.FrameBase,
            VariableArgumentCount = this is Closure closure ? Math.Max(context.ArgumentCount - closure.Proto.ParameterCount, 0) : 0,
            Function = this,
            ReturnBase = context.ReturnFrameBase
        };
      //  Console.WriteLine("InvokeReturnDummyAsync" +context.Thread.CallStack.Count);
        context.Thread.PushCallStackFrame(frame);
        try
        {
            await Func(context, cancellationToken);
           //  Console.WriteLine("InvokeReturnDummyAsync Return" +context.Thread.CallStack.Count);
            return 0;
        }
        // catch(Exception e)
        // {
        //      Console.WriteLine("InvokeReturnDummyAsync Catch" +e);
        //      throw;
        // }
        
        finally
        {
            // Console.WriteLine("InvokeReturnDummyAsync Finally" +context.Thread.CallStack.Count);
            context.Thread.PopCallStackFrameUnsafe();
        }
    }
}