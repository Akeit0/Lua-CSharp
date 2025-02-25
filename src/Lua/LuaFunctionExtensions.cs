using Lua.Internal;

namespace Lua;

public static class LuaFunctionExtensions
{
    public static async ValueTask<LuaValue[]> InvokeAsync(this LuaFunction function, LuaState state, LuaValue[] arguments, CancellationToken cancellationToken = default)
    {
        
        var thread = state.CurrentThread;
        var frameBase = thread.Stack.Count;
        
        for (int i = 0; i < arguments.Length; i++)
        {
            thread.Stack.Push(arguments[i]);
        }

        await function.InvokeAsync(new()
        {
            State = state,
            Thread = thread,
            ArgumentCount = arguments.Length,
            FrameBase = frameBase,
            ReturnFrameBase = frameBase
        },  cancellationToken);
        var results = thread.Stack.GetBuffer()[frameBase..].ToArray();
        thread.Stack.PopUntil(frameBase);
        return results;
    }
}