namespace Lua.Standard;

public sealed class CoroutineLibrary
{
    public static readonly CoroutineLibrary Instance = new();

    public CoroutineLibrary()
    {
        Functions = [
            new("create", Create),
            new("resume", Resume),
            new("running", Running),
            new("status", Status),
            new("wrap", Wrap),
            new("yield", Yield),
        ];
    }

    public readonly LuaFunction[] Functions;

    public ValueTask Create(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<LuaFunction>(0);
        context.Return(new LuaCoroutine(arg0, true));
        return default;
    }

    public ValueTask Resume(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var thread = context.GetArgument<LuaThread>(0);
        return thread.ResumeAsync(context, cancellationToken);
    }

    public ValueTask Running(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        context.Return(context.Thread,context.Thread == context.State.MainThread);
        return default;
    }

    public ValueTask Status(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var thread = context.GetArgument<LuaThread>(0);
        context.Return(thread.GetStatus() switch
        {
            LuaThreadStatus.Normal => "normal",
            LuaThreadStatus.Suspended => "suspended",
            LuaThreadStatus.Running => "running",
            LuaThreadStatus.Dead => "dead",
            _ => "",
        });
        return default;
    }

    public ValueTask Wrap(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<LuaFunction>(0);
        var thread = new LuaCoroutine(arg0, false);

        context.Return(new LuaFunction("wrap", async (context, cancellationToken) =>
        {
            var stack = context.Thread.Stack;
            var frameBase = stack.Count;

            stack.Push(thread);
            stack.PushRange(context.Arguments);
            context.Thread.PushCallStackFrame(new()
            {
                Base = frameBase,
                ReturnBase = context.ReturnFrameBase,
                VariableArgumentCount = 0,
                Function = arg0,
            });
            try
            {
                await thread.ResumeAsync(context with
                {
                    ArgumentCount = context.ArgumentCount + 1,
                    FrameBase = frameBase,
                    ReturnFrameBase = context.ReturnFrameBase,
                }, cancellationToken);
                var result =context.GetReturnBuffer(context.Thread.Stack.Count - context.ReturnFrameBase);
                result[1..].CopyTo(result);
                context.Thread.Stack.Pop();
                return;
            }
            finally
            {
                context.Thread.PopCallStackFrameUnsafe();
            }

           
        }));

        return default;
    }

    public ValueTask Yield(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        return context.Thread.YieldAsync(context, cancellationToken);
    }
}