namespace Lua;

public sealed class LuaMainThread : LuaThread
{
    public override LuaThreadStatus GetStatus()
    {
        return LuaThreadStatus.Running;
    }

    public override void UnsafeSetStatus(LuaThreadStatus status)
    {
        // Do nothing
    }

    public override ValueTask ResumeAsync(LuaFunctionExecutionContext context, CancellationToken cancellationToken = default)
    {
        context.Return(false, "cannot resume non-suspended coroutine");
        return default;
    }

    public override ValueTask YieldAsync(LuaFunctionExecutionContext context, 
        CancellationToken cancellationToken = default)
    {
        throw new LuaRuntimeException(context.State.GetTraceback(), "attempt to yield from outside a coroutine");
    }
}
