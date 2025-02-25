using Lua.Runtime;

namespace Lua;

public readonly struct LuaResults(LuaStack stack, int returnBase) : IDisposable
{
    public int Count => stack.Count - returnBase;
    public int Length => stack.Count - returnBase;
    public ReadOnlySpan<LuaValue> AsSpan() => stack.AsSpan()[returnBase..];

    public LuaValue this[int index] => AsSpan()[index];
    
    public void Dispose()
    {
        stack.PopUntil(returnBase);
    }
}